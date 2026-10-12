<#
.SYNOPSIS
    Step 1 of 2 for SharePoint: creates the app registration "Hive BDC SharePoint", without any permission.

.DESCRIPTION
    Run in Azure Cloud Shell (PowerShell). It shows the tenant and asks first, skips the app
    if it already exists, creates its service principal, and prints the IDs plus the
    command for step 2, Grant-HiveBDCSharePointAccess.ps1, which assigns Sites.Selected and the site access.

    Role: Application Developer or Application Administrator. No client secret is
    created; the script prints how. -WhatIf shows the plan only.

.EXAMPLE
    ./New-HiveBDCSharePointApp.ps1
#>
#Requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateLength(3, 120)]
    [string] $AppName = 'Hive BDC SharePoint',

    [ValidatePattern('^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$')]
    [string] $TenantId,

    # Do not ask before changing the tenant.
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

function Write-Step { param([string] $Text) Write-Host "`n== $Text" -ForegroundColor Cyan }
function Write-Ok { param([string] $Text) Write-Host "   [ok]   $Text" -ForegroundColor Green }
function Write-Skip { param([string] $Text) Write-Host "   [have] $Text" -ForegroundColor DarkGray }
function Write-Note { param([string] $Text) Write-Host "   $Text" -ForegroundColor Gray }

$isCloudShell = [bool]$env:ACC_CLOUD -or ("$env:AZUREPS_HOST_ENVIRONMENT" -like '*cloud-shell*')
$useDeviceCode = $isCloudShell -or ($PSVersionTable.PSVersion.Major -ge 6 -and -not $IsWindows)

# Runs az and returns its JSON output; a failed command stops the script.
function Invoke-Az {
    $output = & az @args --only-show-errors --output json
    if ($LASTEXITCODE -ne 0) { throw "'az $($args[0]) $($args[1])' failed (exit code $LASTEXITCODE), see above." }
    $text = ($output | Out-String).Trim()
    if (-not $text) { return }
    $parsed = $text | ConvertFrom-Json
    return $parsed   # return, not the pipeline, so a JSON array arrives as its items
}

function Get-AzCliAccount {
    $ErrorActionPreference = 'Continue'   # 'not signed in' arrives on stderr
    $text = (& az account show --output json 2>$null | Out-String).Trim()
    if ($LASTEXITCODE -eq 0 -and $text) { return $text | ConvertFrom-Json }
}

if ($WhatIfPreference) { Write-Host 'DRY RUN - nothing will be changed.' -ForegroundColor Yellow }

# -- 1. sign-in -----------------------------------------------------------------------
Write-Step '1/2 Sign-in'
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'Azure CLI (az) was not found. Run this script in Azure Cloud Shell (PowerShell).'
}
$account = Get-AzCliAccount
if (-not $account -or ($TenantId -and $account.tenantId -ne $TenantId)) {
    $login = @('login', '--allow-no-subscriptions', '--only-show-errors', '--output', 'none')
    if ($TenantId) { $login += @('--tenant', $TenantId) }
    if ($useDeviceCode) { $login += '--use-device-code' }
    & az @login
    if ($LASTEXITCODE -ne 0) { throw 'az login failed.' }
    $account = Get-AzCliAccount
}
$tenantId = $account.tenantId
$tenantName = (@((Invoke-Az rest --method GET --url 'https://graph.microsoft.com/v1.0/organization?$select=displayName').value) |
    Select-Object -First 1).displayName
Write-Note "tenant:  $tenantName ($tenantId)"
Write-Note "account: $($account.user.name)"
if (-not $Force -and -not $WhatIfPreference) {
    $answer = Read-Host "   Create '$AppName' in '$tenantName'? (y/n)"
    if ($answer -notmatch '^(y|yes|j|ja)$') { Write-Note 'Stopped. Nothing was changed.'; return }
}

# -- 2. app registration and service principal ----------------------------------------
Write-Step "2/2 App registration '$AppName'"
$apps = @(Invoke-Az ad app list --display-name $AppName | Where-Object { $_.displayName -eq $AppName })
if ($apps.Count -gt 1) { throw "There are $($apps.Count) app registrations named '$AppName'. Remove the extra ones, or use -AppName." }
$reg = $apps | Select-Object -First 1
if ($reg) { Write-Skip "app registration exists (appId $($reg.appId))" }
elseif ($PSCmdlet.ShouldProcess($AppName, 'Create the app registration')) {
    $reg = Invoke-Az ad app create --display-name $AppName --sign-in-audience AzureADMyOrg
    Write-Ok "created the app registration (appId $($reg.appId))"
}
if (-not $reg) { Write-Note 'Dry run ends here: the app does not exist yet.'; return }
$appId = $reg.appId

$sp = Invoke-Az ad sp list --spn $appId | Select-Object -First 1
if ($sp) { Write-Skip "service principal exists ($($sp.id))" }
elseif ($PSCmdlet.ShouldProcess($appId, 'Create the service principal')) {
    for ($attempt = 1; -not $sp; $attempt++) {   # a new app needs a few seconds to replicate
        try { $sp = Invoke-Az ad sp create --id $appId }
        catch { if ($attempt -ge 4) { throw }; Start-Sleep -Seconds 10 }
    }
    Write-Ok "created the service principal ($($sp.id))"
}

# -- next -----------------------------------------------------------------------------
$grant = "./Grant-HiveBDCSharePointAccess.ps1 -AppId $appId -SiteUrl https://<tenant>.sharepoint.com/sites/<site>"
Write-Host "`nDONE, the app has no permissions yet" -ForegroundColor Yellow
Write-Host "  Tenant ID  $tenantId"
Write-Host "  Client ID  $appId"
Write-Note ''
Write-Note 'Step 2, assign the permissions:'
Write-Note "  $grant"
Write-Note 'Client secret (shown once, enter it straight into the Hive BDC dashboard):'
Write-Note "  az ad app credential reset --id $appId --append --display-name HiveBDC --years 2 --query password -o tsv"

<#
.SYNOPSIS
    Sign-in for Hive BDC in Azure: creates the app registration "Hive BDC sign-in" (before the installation) and
    connects it to the installed instance (after it). No client secret is created.

.DESCRIPTION
    Run in Azure Cloud Shell (PowerShell). It shows the tenant and asks first; every step skips what already exists.

    Before the installation (no parameters): creates the app registration with the app roles Viewer, Operator and
    Administrator, lets only users with a role sign in, gives you the Administrator role and prints the Application
    (client) ID that the Hive BDC installation asks for.

    After the installation (-ClientId, -RedirectUri, -IdentityObjectId; the managed application shows the complete
    command under Parameters and Outputs as signInStep2): adds the redirect URI of the instance and a federated
    credential that lets the instance's sign-in identity act for the app registration instead of a client secret.

    Role: Application Developer or Application Administrator; giving yourself the Administrator role on the app also
    works as its owner. -WhatIf shows the plan only.

.EXAMPLE
    ./New-HiveBDCSignInApp.ps1

.EXAMPLE
    ./New-HiveBDCSignInApp.ps1 -ClientId 11111111-2222-3333-4444-555555555555 -RedirectUri https://hivebdc-abc.azurewebsites.net/.auth/login/aad/callback -IdentityObjectId 66666666-7777-8888-9999-000000000000
#>
#Requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true, DefaultParameterSetName = 'Create')]
param(
    [Parameter(ParameterSetName = 'Create')]
    [ValidateLength(3, 120)]
    [string] $AppName = 'Hive BDC sign-in',

    [Parameter(ParameterSetName = 'Connect', Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$')]
    [string] $ClientId,

    [Parameter(ParameterSetName = 'Connect', Mandatory = $true)]
    [ValidatePattern('^https://[^/\s]+/\.auth/login/aad/callback$')]
    [string] $RedirectUri,

    [Parameter(ParameterSetName = 'Connect', Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$')]
    [string] $IdentityObjectId,

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

# The roles the Hive BDC web console checks: reading, retrying documents, changing settings.
$roles = @(
    @{ value = 'Viewer'; displayName = 'Viewer'; description = 'Sees the Hive BDC status, documents and logs.' },
    @{ value = 'Operator'; displayName = 'Operator'; description = 'Viewer, and retries documents that need attention.' },
    @{ value = 'Administrator'; displayName = 'Administrator'; description = 'Operator, and changes settings, connections and the licence.' }
)

# Only Cloud Shell has no browser to sign in with; there a device code is the way in.
# Elsewhere the browser is used, because many tenants block sign-in by code.
$isCloudShell = [bool]$env:ACC_CLOUD -or ("$env:AZUREPS_HOST_ENVIRONMENT" -like '*cloud-shell*')
$useDeviceCode = $isCloudShell

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

# az takes JSON bodies from a file; quoting JSON on a command line differs between shells.
function Write-JsonFile {
    param([object] $Value)
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("hivebdc-" + [guid]::NewGuid() + '.json')
    ConvertTo-Json -InputObject $Value -Depth 6 | Set-Content -Path $path -Encoding UTF8
    return $path
}

if ($WhatIfPreference) { Write-Host 'DRY RUN - nothing will be changed.' -ForegroundColor Yellow }
$connect = $PSCmdlet.ParameterSetName -eq 'Connect'
$steps = if ($connect) { 3 } else { 4 }

# -- sign-in ----------------------------------------------------------------------------
Write-Step "1/$steps Sign-in"
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
    $question = if ($connect) { "Connect the Hive BDC instance to app $ClientId in '$tenantName'? (y/n)" }
                else { "Create '$AppName' in '$tenantName'? (y/n)" }
    $answer = Read-Host "   $question"
    if ($answer -notmatch '^(y|yes|j|ja)$') { Write-Note 'Stopped. Nothing was changed.'; return }
}

if ($connect) {
    # -- after the installation: redirect URI and federated credential ------------------
    Write-Step "2/$steps Redirect URI"
    $reg = Invoke-Az ad app show --id $ClientId
    $uris = @($reg.web.redirectUris)
    if ($uris -contains $RedirectUri) { Write-Skip $RedirectUri }
    elseif ($PSCmdlet.ShouldProcess($RedirectUri, 'Add the redirect URI')) {
        $all = @($uris | Where-Object { $_ }) + $RedirectUri
        & az ad app update --id $ClientId --web-redirect-uris @all --only-show-errors
        if ($LASTEXITCODE -ne 0) { throw 'Adding the redirect URI failed, see above.' }
        Write-Ok "added $RedirectUri"
    }

    Write-Step "3/$steps Federated credential for the instance's sign-in identity"
    $existing = @(Invoke-Az ad app federated-credential list --id $ClientId) | Where-Object { $_.subject -eq $IdentityObjectId }
    if ($existing) { Write-Skip "credential '$($existing[0].name)' trusts $IdentityObjectId" }
    elseif ($PSCmdlet.ShouldProcess($IdentityObjectId, 'Trust the sign-in identity')) {
        $file = Write-JsonFile @{
            name        = 'hivebdc-signin-' + $IdentityObjectId.Substring(0, 8).ToLowerInvariant()
            issuer      = "https://login.microsoftonline.com/$tenantId/v2.0"
            subject     = $IdentityObjectId
            audiences   = @('api://AzureADTokenExchange')
            description = 'App Service sign-in of Hive BDC: the instance proves itself with its managed identity, no client secret.'
        }
        try { $null = Invoke-Az ad app federated-credential create --id $ClientId --parameters "@$file" }
        finally { Remove-Item -Path $file -ErrorAction SilentlyContinue }
        Write-Ok "the identity $IdentityObjectId may sign in for the app"
    }

    $console = $RedirectUri.Substring(0, $RedirectUri.Length - '/.auth/login/aad/callback'.Length)
    Write-Host "`nDONE" -ForegroundColor Green
    Write-Note "Open $console and sign in."
    Write-Note "Colleagues need a role first: Enterprise applications > $($reg.displayName) > Users and groups > Add (Viewer, Operator or Administrator)."
    return
}

# -- before the installation: app registration, roles, service principal, your role -----
Write-Step "2/$steps App registration '$AppName'"
$apps = @(Invoke-Az ad app list --display-name $AppName | Where-Object { $_.displayName -eq $AppName })
if ($apps.Count -gt 1) { throw "There are $($apps.Count) app registrations named '$AppName'. Remove the extra ones, or use -AppName." }
$reg = $apps | Select-Object -First 1
if ($reg) { Write-Skip "app registration exists (appId $($reg.appId))" }
elseif ($PSCmdlet.ShouldProcess($AppName, 'Create the app registration')) {
    $file = Write-JsonFile @($roles | ForEach-Object {
        @{ id = [guid]::NewGuid().ToString(); allowedMemberTypes = @('User'); isEnabled = $true
           value = $_.value; displayName = $_.displayName; description = $_.description } })
    try {
        $reg = Invoke-Az ad app create --display-name $AppName --sign-in-audience AzureADMyOrg `
            --enable-id-token-issuance true --app-roles "@$file"
    }
    finally { Remove-Item -Path $file -ErrorAction SilentlyContinue }
    Write-Ok "created the app registration (appId $($reg.appId))"
}
if (-not $reg) { Write-Note 'Dry run ends here: the app does not exist yet.'; return }
$appId = $reg.appId

# An existing registration gets the roles it lacks; roles it already has keep their ids.
$reg = Invoke-Az ad app show --id $appId
$have = @($reg.appRoles)
$missing = @($roles | Where-Object { $have.value -notcontains $_.value })
if (-not $missing) { Write-Skip 'app roles Viewer, Operator, Administrator' }
elseif ($PSCmdlet.ShouldProcess($appId, "Add the app roles $(($missing.value) -join ', ')")) {
    $all = @($have | ForEach-Object { $_ }) + @($missing | ForEach-Object {
        [pscustomobject]@{ id = [guid]::NewGuid().ToString(); allowedMemberTypes = @('User'); isEnabled = $true
                           value = $_.value; displayName = $_.displayName; description = $_.description } })
    $file = Write-JsonFile $all
    try { $null = Invoke-Az ad app update --id $appId --app-roles "@$file" }
    finally { Remove-Item -Path $file -ErrorAction SilentlyContinue }
    Write-Ok "added the app roles $(($missing.value) -join ', ')"
    $reg = Invoke-Az ad app show --id $appId
}
if (-not $reg.web.implicitGrantSettings.enableIdTokenIssuance) {
    if ($PSCmdlet.ShouldProcess($appId, 'Allow ID tokens for the App Service sign-in')) {
        $null = Invoke-Az ad app update --id $appId --enable-id-token-issuance true
        Write-Ok 'ID tokens allowed'
    }
}

Write-Step "3/$steps Service principal"
$sp = Invoke-Az ad sp list --spn $appId | Select-Object -First 1
if ($sp) { Write-Skip "service principal exists ($($sp.id))" }
elseif ($PSCmdlet.ShouldProcess($appId, 'Create the service principal')) {
    for ($attempt = 1; -not $sp; $attempt++) {   # a new app needs a few seconds to replicate
        try { $sp = Invoke-Az ad sp create --id $appId }
        catch { if ($attempt -ge 4) { throw }; Start-Sleep -Seconds 10 }
    }
    Write-Ok "created the service principal ($($sp.id))"
}
if (-not $sp) { Write-Note 'Dry run ends here: the service principal does not exist yet.'; return }
if ($sp.appRoleAssignmentRequired) { Write-Skip 'only users with a role can sign in' }
elseif ($PSCmdlet.ShouldProcess($sp.id, 'Let only users with a role sign in')) {
    & az ad sp update --id $sp.id --set appRoleAssignmentRequired=true --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw 'Setting "assignment required" failed, see above.' }
    Write-Ok 'only users with a role can sign in'
}

Write-Step "4/$steps Your role"
$me = Invoke-Az ad signed-in-user show
$admin = @($reg.appRoles) | Where-Object { $_.value -eq 'Administrator' } | Select-Object -First 1
$assigned = @((Invoke-Az rest --method GET --url "https://graph.microsoft.com/v1.0/servicePrincipals/$($sp.id)/appRoleAssignedTo").value) |
    Where-Object { $_.principalId -eq $me.id -and $_.appRoleId -eq $admin.id }
if ($assigned) { Write-Skip "$($me.userPrincipalName) is Administrator" }
elseif ($PSCmdlet.ShouldProcess($me.userPrincipalName, 'Give the Administrator role')) {
    $file = Write-JsonFile @{ principalId = $me.id; resourceId = $sp.id; appRoleId = $admin.id }
    try {
        $null = Invoke-Az rest --method POST --url "https://graph.microsoft.com/v1.0/servicePrincipals/$($sp.id)/appRoleAssignedTo" `
            --headers 'Content-Type=application/json' --body "@$file"
    }
    finally { Remove-Item -Path $file -ErrorAction SilentlyContinue }
    Write-Ok "$($me.userPrincipalName) is Administrator"
}

# -- next -------------------------------------------------------------------------------
Write-Host "`nDONE" -ForegroundColor Green
Write-Host "  Tenant ID  $tenantId"
Write-Host "  Client ID  $appId"
Write-Note ''
Write-Note 'Next: install Hive BDC from the Azure Marketplace and enter this Client ID on the Sign-in step.'
Write-Note 'After the installation, run this script once more with the command the managed application shows'
Write-Note 'under Parameters and Outputs (signInStep2).'
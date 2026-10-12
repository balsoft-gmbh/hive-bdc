<#
.SYNOPSIS
    Step 2 of 2 for mail: allows the Hive BDC Mail app to send as ONE mailbox, and as no other.

.DESCRIPTION
    Run in Azure Cloud Shell (PowerShell) after New-HiveBDCMailApp.ps1.
    Steps, each skipped when already in place:
      1. Azure CLI: show the tenant, the app and the mailbox, and ask before changing anything.
      2. Exchange Online: RBAC for Applications, the role "Application Mail.Send" limited to
         a management scope that contains only -SenderMailbox.

    The app gets NO Mail.Send consent in Entra ID, because an Entra consent is tenant-wide
    and RBAC grants add to it instead of narrowing it; an existing Entra consent is
    removed. Tenants without RBAC for Applications get the older application access
    policy, which does restrict an Entra consent.

    Role: Exchange Administrator (plus Privileged Role Administrator only to remove an old
    Entra consent, or for the access policy fallback). -WhatIf shows the plan only.

.EXAMPLE
    ./Grant-HiveBDCMailAccess.ps1 -AppId 11111111-2222-3333-4444-555555555555 -SenderMailbox invoices@contoso.com
#>
#Requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    # Application (client) ID printed by New-HiveBDCMailApp.ps1.
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$')]
    [string] $AppId,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[^@\s]+@[^@\s]+\.[^@\s]+$')]
    [string] $SenderMailbox,

    # Prefix of the Exchange objects (scope, role assignment, security group).
    [ValidatePattern('^[A-Za-z0-9-]{3,40}$')]
    [string] $Name = 'HiveBDCMail',

    [switch] $UseApplicationAccessPolicy,

    [ValidatePattern('^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$')]
    [string] $TenantId,

    # Do not ask before changing the tenant.
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$GraphAppId = '00000003-0000-0000-c000-000000000000'   # Microsoft Graph, same in every tenant
$GraphV1 = 'https://graph.microsoft.com/v1.0'

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

# One Microsoft Graph call through 'az rest'. The body goes through a file because
# Windows PowerShell 5.1 and az.cmd both mangle quotes in JSON arguments.
function Invoke-GraphAz {
    param([string] $Method, [string] $Path, $Body)
    $azArgs = @('rest', '--method', $Method, '--url', "$GraphV1/$Path")
    $bodyFile = $null
    if ($null -ne $Body) {
        $bodyFile = [IO.Path]::GetTempFileName()
        [IO.File]::WriteAllText($bodyFile, ($Body | ConvertTo-Json -Depth 8))
        $azArgs += @('--body', "@$bodyFile", '--headers', 'Content-Type=application/json')
    }
    try { return Invoke-Az @azArgs }
    finally { if ($bodyFile) { Remove-Item -LiteralPath $bodyFile -ErrorAction SilentlyContinue } }
}

function Get-AzCliAccount {
    $ErrorActionPreference = 'Continue'   # 'not signed in' arrives on stderr
    $text = (& az account show --output json 2>$null | Out-String).Trim()
    if ($LASTEXITCODE -eq 0 -and $text) { return $text | ConvertFrom-Json }
}

function Test-Cmdlet { param([string] $CmdletName) [bool](Get-Command $CmdletName -ErrorAction SilentlyContinue) }

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
$tenantName = (@((Invoke-GraphAz GET 'organization?$select=displayName').value) | Select-Object -First 1).displayName
Write-Note "tenant:  $tenantName ($tenantId)"
Write-Note "account: $($account.user.name)"
$app = Invoke-Az ad app show --id $AppId
$AppName = $app.displayName
$appId = $app.appId
$sp = Invoke-Az ad sp list --spn $appId | Select-Object -First 1
if (-not $sp) { throw "The app $appId has no service principal. Run New-HiveBDCMailApp.ps1 first." }
Write-Note "app:     $AppName ($appId)"
Write-Note "mailbox: $SenderMailbox"
if (-not $Force -and -not $WhatIfPreference) {
    $answer = Read-Host "   Allow it to send as $SenderMailbox only? (y/n)"
    if ($answer -notmatch '^(y|yes|j|ja)$') { Write-Note 'Stopped. Nothing was changed.'; return }
}

# The tenant-wide Entra consent, if someone granted one; step 2 decides what happens to it.
$graphSp = Invoke-Az ad sp show --id $GraphAppId
$mailSend = @($graphSp.appRoles | Where-Object { $_.value -eq 'Mail.Send' -and $_.allowedMemberTypes -contains 'Application' }) |
    Select-Object -First 1
$entraGrant = @((Invoke-GraphAz GET "servicePrincipals/$($sp.id)/appRoleAssignments").value |
    Where-Object { $_.appRoleId -eq $mailSend.id -and $_.resourceId -eq $graphSp.id }) | Select-Object -First 1

# -- 3. Exchange Online ---------------------------------------------------------------
Write-Step '2/2 Exchange Online: send as one mailbox only'
if (-not (Get-Module -ListAvailable -Name ExchangeOnlineManagement)) {
    Install-Module -Name ExchangeOnlineManagement -Scope CurrentUser -Force -AllowClobber
}
Import-Module ExchangeOnlineManagement
if ($useDeviceCode) { Connect-ExchangeOnline -Device -ShowBanner:$false } else { Connect-ExchangeOnline -ShowBanner:$false }

if (Test-Cmdlet 'Get-ConnectionInformation') {
    $exoTenant = @(Get-ConnectionInformation)[0].TenantID
    if ($exoTenant -and "$exoTenant" -ne $tenantId) {
        Disconnect-ExchangeOnline -Confirm:$false | Out-Null
        throw "Exchange Online signed in to tenant $exoTenant, but the app is in $tenantId."
    }
}
$mailbox = Get-Mailbox -Identity $SenderMailbox -ErrorAction SilentlyContinue
if (-not $mailbox) { throw "The mailbox '$SenderMailbox' was not found in Exchange Online." }

$useRbac = (Test-Cmdlet 'New-ServicePrincipal') -and (Test-Cmdlet 'New-ManagementRoleAssignment') -and
           -not $UseApplicationAccessPolicy

if ($useRbac) {
    $scopeName = "$Name-Scope"
    $assignmentName = "$Name-MailSend"

    if (Get-ServicePrincipal -Identity $appId -ErrorAction SilentlyContinue) { Write-Skip 'registered in Exchange Online' }
    elseif ($PSCmdlet.ShouldProcess($appId, 'Register the service principal in Exchange Online')) {
        # Exchange needs the service principal's OBJECT id, not the application id.
        New-ServicePrincipal -AppId $appId -ObjectId $sp.id -DisplayName $AppName | Out-Null
        Write-Ok 'registered in Exchange Online'
    }

    $scope = Get-ManagementScope -Identity $scopeName -ErrorAction SilentlyContinue
    if ($scope) {
        Write-Skip "scope '$scopeName': $($scope.RecipientFilter)"
        if ($scope.RecipientFilter -notmatch [regex]::Escape($SenderMailbox)) {
            Write-Warning "The scope '$scopeName' does NOT match $SenderMailbox. Review it, or use another -Name."
        }
    }
    elseif ($PSCmdlet.ShouldProcess($scopeName, "Create a management scope with only $SenderMailbox")) {
        # Doubled apostrophes keep an address with ' from breaking the filter.
        $filter = "PrimarySmtpAddress -eq '{0}'" -f $SenderMailbox.Replace("'", "''")
        New-ManagementScope -Name $scopeName -RecipientRestrictionFilter $filter | Out-Null
        Write-Ok "created the scope '$scopeName'"
    }

    if (Get-ManagementRoleAssignment -Identity $assignmentName -ErrorAction SilentlyContinue) { Write-Skip "role assignment '$assignmentName'" }
    elseif ($PSCmdlet.ShouldProcess($assignmentName, "Assign 'Application Mail.Send' limited to $scopeName")) {
        New-ManagementRoleAssignment -Name $assignmentName -App $appId -Role 'Application Mail.Send' `
            -CustomResourceScope $scopeName | Out-Null
        Write-Ok "assigned 'Application Mail.Send' for $SenderMailbox"
    }

    # An Entra consent would add tenant-wide send-as on top of the scoped role.
    if (-not $entraGrant) { Write-Skip 'no tenant-wide Mail.Send consent in Entra ID (correct)' }
    elseif ($PSCmdlet.ShouldProcess($AppName, 'Remove the tenant-wide Mail.Send consent from Entra ID')) {
        Invoke-GraphAz DELETE "servicePrincipals/$($sp.id)/appRoleAssignments/$($entraGrant.id)" | Out-Null
        Write-Ok 'removed the tenant-wide Mail.Send consent from Entra ID'
    }
    $verify = "Test-ServicePrincipalAuthorization -Identity $appId -Resource $SenderMailbox"
}
else {
    # Older tenants: Entra consent for Mail.Send, restricted by an application access
    # policy to a mail-enabled SECURITY group (a Microsoft 365 group does not work).
    if (-not (Test-Cmdlet 'New-ApplicationAccessPolicy')) {
        throw 'Neither RBAC for Applications nor application access policies are available. Update ExchangeOnlineManagement and retry.'
    }
    $groupAlias = $Name.ToLowerInvariant()
    $groupAddress = "$groupAlias@$($SenderMailbox.Split('@')[1])"

    if (Get-DistributionGroup -Identity $groupAddress -ErrorAction SilentlyContinue) { Write-Skip "group $groupAddress" }
    elseif ($PSCmdlet.ShouldProcess($groupAddress, 'Create the mail-enabled security group')) {
        New-DistributionGroup -Name $Name -Alias $groupAlias -PrimarySmtpAddress $groupAddress -Type Security | Out-Null
        Write-Ok "created the group $groupAddress"
    }
    $members = @(Get-DistributionGroupMember -Identity $groupAddress -ErrorAction SilentlyContinue | ForEach-Object { $_.PrimarySmtpAddress })
    if ($members -contains $mailbox.PrimarySmtpAddress) { Write-Skip "$SenderMailbox is a member" }
    elseif ($PSCmdlet.ShouldProcess($groupAddress, "Add $SenderMailbox")) {
        Add-DistributionGroupMember -Identity $groupAddress -Member $SenderMailbox
        Write-Ok "added $SenderMailbox to the group"
    }
    if (@(Get-ApplicationAccessPolicy -ErrorAction SilentlyContinue | Where-Object { $_.AppId -eq $appId })) { Write-Skip 'application access policy' }
    elseif ($PSCmdlet.ShouldProcess($appId, "Restrict the app to $groupAddress")) {
        New-ApplicationAccessPolicy -AppId $appId -PolicyScopeGroupId $groupAddress -AccessRight RestrictAccess `
            -Description "Hive BDC may send only as members of $groupAddress" | Out-Null
        Write-Ok 'created the application access policy'
    }

    # The consent comes last, so it is never in place without the policy that limits it.
    if ($entraGrant) { Write-Skip 'Mail.Send consented in Entra ID' }
    elseif ($PSCmdlet.ShouldProcess($AppName, 'Grant admin consent for Mail.Send')) {
        Invoke-GraphAz POST "servicePrincipals/$($sp.id)/appRoleAssignments" @{
            principalId = $sp.id; resourceId = $graphSp.id; appRoleId = $mailSend.id
        } | Out-Null
        Write-Ok 'Mail.Send consented, limited by the policy above'
    }
    $verify = "Test-ApplicationAccessPolicy -Identity $SenderMailbox -AppId $appId"
}
Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue | Out-Null

# -- next -----------------------------------------------------------------------------
Write-Host "`nDONE" -ForegroundColor Yellow
Write-Host "  Tenant ID  $tenantId"
Write-Host "  Client ID  $appId"
Write-Host "  Send from  $SenderMailbox"
Write-Note 'Hive BDC dashboard: Settings > Connections > Document mail, then Test mail.'
Write-Note 'Exchange needs up to 30 minutes to apply this; until then sending can fail with ErrorAccessDenied.'
Write-Note "Check later: $verify"

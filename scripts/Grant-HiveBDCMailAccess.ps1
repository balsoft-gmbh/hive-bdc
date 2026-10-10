<#
.SYNOPSIS
    Lets Hive BDC send mail as ONE mailbox, and as no other mailbox.

.DESCRIPTION
    Hive BDC sends documents (invoices, confirmations, orders, ...) through Microsoft Graph with its own app registration.
    App-only sending needs the APPLICATION permission Mail.Send, which on its own lets the
    app send as EVERY mailbox in the tenant. This script grants that permission and then
    restricts it to a single sender, so the app can only ever send as, for example,
    the mailbox given in -SenderMailbox.

    Three steps, each skipped when it is already in place:

      1. Entra ID       consent the Microsoft Graph application permission Mail.Send.
      2. Exchange       register the app's service principal.
      3. Exchange       restrict it to the one mailbox. RBAC for Applications is used where
                        the tenant has it (a management scope plus the "Application
                        Mail.Send" role), otherwise an application access policy.

    Step 1 alone is tenant-wide send-as. Steps 2 and 3 are what narrow it down, so run the
    whole script rather than only consenting the permission in the portal.

    Nothing about the client secret is touched, asked for, or written. This script grants
    access to a mailbox; Hive BDC itself is configured in its dashboard.

    HOW TO RUN IT

      Azure Cloud Shell (PowerShell), or any PowerShell 5.1+ with internet access:

        ./Grant-HiveBDCMailAccess.ps1 -AppId <application-client-id> -WhatIf   # dry run
        ./Grant-HiveBDCMailAccess.ps1 -AppId <application-client-id>           # for real

      You need Global Administrator (step 1) and Exchange Administrator (steps 2 and 3).
      Missing modules are installed for the current user; in Cloud Shell that persists.
      Sign-in uses a device code there, so watch the console for the code and URL.

      Everything the script creates is skipped when it already exists, so re-running it is
      safe.

    AFTERWARDS

      Allow up to 30 minutes for Exchange Online to apply the restriction everywhere. Until
      it has, a send can still fail with ErrorAccessDenied; that is propagation, not a
      misconfiguration.

      Then in the Hive BDC dashboard: Settings > Document mail. Set "Send from" to the
      mailbox, enter the client ID and secret, add the test recipients, press "Test mail".

.PARAMETER AppId
    Application (client) ID of the Hive BDC mail app registration.

.PARAMETER SenderMailbox
    The one mailbox the app may send as, e.g. invoices@contoso.com.

.PARAMETER Name
    Prefix for the objects this script creates (scope, role assignment, security group).

.PARAMETER UseApplicationAccessPolicy
    Force the older application access policy even where RBAC for Applications is available.

.PARAMETER SkipGraphConsent
    Skip step 1, for when Mail.Send was already consented in the Entra portal.

.PARAMETER Interactive
    Force a browser sign-in instead of a device code. Device code is the default in Cloud
    Shell and on Linux, where no browser can be opened.

.EXAMPLE
    ./Grant-HiveBDCMailAccess.ps1 -AppId 11111111-2222-3333-4444-555555555555

.EXAMPLE
    ./Grant-HiveBDCMailAccess.ps1 -AppId 1111... -SenderMailbox rechnung@contoso.de -WhatIf
#>

#Requires -Version 5.1

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$')]
    [string] $AppId,

    [Parameter(Mandatory)]
    [ValidatePattern('^[^@\s]+@[^@\s]+\.[^@\s]+$')]
    [string] $SenderMailbox,

    [ValidatePattern('^[A-Za-z0-9-]{3,40}$')]
    [string] $Name = 'HiveBDCMail',

    [switch] $UseApplicationAccessPolicy,

    [switch] $SkipGraphConsent,

    [switch] $Interactive
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$GraphAppId = '00000003-0000-0000-c000-000000000000'   # Microsoft Graph, same in every tenant
$MailSendRole = 'Mail.Send'
$GraphV1 = 'https://graph.microsoft.com/v1.0'

function Write-Step { param([string] $Text) Write-Host "`n== $Text" -ForegroundColor Cyan }
function Write-Ok { param([string] $Text) Write-Host "   [ok]   $Text" -ForegroundColor Green }
function Write-Skip { param([string] $Text) Write-Host "   [have] $Text" -ForegroundColor DarkGray }
function Write-Note { param([string] $Text) Write-Host "   $Text" -ForegroundColor Gray }

# Cloud Shell and Linux have no browser to hand off to, so device code is the default there.
$isCloudShell = [bool]$env:ACC_CLOUD -or ("$env:AZUREPS_HOST_ENVIRONMENT" -like '*cloud-shell*')
$useDeviceCode = -not $Interactive -and ($isCloudShell -or $IsLinux -or $IsMacOS)

function Install-IfMissing {
    param([string] $ModuleName)
    if (Get-Module -ListAvailable -Name $ModuleName) { return }
    Write-Note "installing $ModuleName for the current user (one time)..."
    # Trusting the gallery avoids an interactive prompt that would stall an unattended run.
    if ((Get-PSRepository -Name PSGallery).InstallationPolicy -ne 'Trusted') {
        Set-PSRepository -Name PSGallery -InstallationPolicy Trusted
    }
    Install-Module -Name $ModuleName -Scope CurrentUser -Force -AllowClobber
    Write-Ok "$ModuleName installed"
}

# Whether a cmdlet exists in the current session, used to tell the two Exchange paths apart.
function Test-Cmdlet { param([string] $CmdletName) [bool](Get-Command $CmdletName -ErrorAction SilentlyContinue) }

# One Graph call. Returns $null on 404 rather than throwing, so "does it exist" reads plainly.
function Invoke-Graph {
    param(
        [string] $Method,
        [string] $Uri,
        [hashtable] $Body
    )
    try {
        if ($Body) {
            return Invoke-MgGraphRequest -Method $Method -Uri $Uri -Body ($Body | ConvertTo-Json -Depth 6) `
                -ContentType 'application/json' -OutputType PSObject
        }
        return Invoke-MgGraphRequest -Method $Method -Uri $Uri -OutputType PSObject
    }
    catch {
        if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 404) { return $null }
        throw
    }
}

Write-Host 'Hive BDC - grant send-as for one mailbox' -ForegroundColor White
Write-Note "app:     $AppId"
Write-Note "mailbox: $SenderMailbox"
if ($WhatIfPreference) { Write-Host '   DRY RUN - nothing will be changed.' -ForegroundColor Yellow }
if ($useDeviceCode) { Write-Note 'sign-in: device code (watch for the code and URL below)' }

# ── 1. Entra ID: consent Mail.Send for the app ────────────────────────────────
$spObjectId = $null

if ($SkipGraphConsent) {
    Write-Step '1/3 Microsoft Graph permission Mail.Send (skipped by -SkipGraphConsent)'
}
else {
    Write-Step '1/3 Microsoft Graph permission Mail.Send'

    # Only the Authentication module is needed: the calls below go through
    # Invoke-MgGraphRequest, which keeps the install small and quick in Cloud Shell.
    Install-IfMissing 'Microsoft.Graph.Authentication'
    Import-Module Microsoft.Graph.Authentication -ErrorAction Stop

    $connect = @{ Scopes = @('Application.ReadWrite.All', 'AppRoleAssignment.ReadWrite.All'); NoWelcome = $true }
    if ($useDeviceCode) { $connect['UseDeviceCode'] = $true }
    Connect-MgGraph @connect
    Write-Note ("signed in to tenant {0}" -f (Get-MgContext).TenantId)

    $found = Invoke-Graph GET "$GraphV1/servicePrincipals?`$filter=appId eq '$AppId'&`$select=id,displayName"
    $appSp = @($found.value)[0]

    if (-not $appSp) {
        # An app registration has no service principal in its tenant until something creates
        # one, and nothing can be granted to it before that exists.
        if ($PSCmdlet.ShouldProcess($AppId, 'Create the service principal for the app registration')) {
            $appSp = Invoke-Graph POST "$GraphV1/servicePrincipals" @{ appId = $AppId }
            Write-Ok "created the service principal ($($appSp.id))"
        }
    }
    else {
        Write-Skip "service principal exists: $($appSp.displayName) ($($appSp.id))"
    }

    if ($appSp) {
        $spObjectId = $appSp.id

        $graphSpResult = Invoke-Graph GET "$GraphV1/servicePrincipals?`$filter=appId eq '$GraphAppId'&`$select=id,appRoles"
        $graphSp = @($graphSpResult.value)[0]
        $role = @($graphSp.appRoles | Where-Object { $_.value -eq $MailSendRole -and $_.allowedMemberTypes -contains 'Application' })[0]
        if (-not $role) { throw "The application role '$MailSendRole' was not found on Microsoft Graph." }

        $assignments = Invoke-Graph GET "$GraphV1/servicePrincipals/$spObjectId/appRoleAssignments"
        $already = @($assignments.value | Where-Object { $_.appRoleId -eq $role.id -and $_.resourceId -eq $graphSp.id })[0]

        if ($already) {
            Write-Skip "$MailSendRole is already consented for this app"
        }
        elseif ($PSCmdlet.ShouldProcess($AppId, "Consent the Graph application permission $MailSendRole")) {
            Invoke-Graph POST "$GraphV1/servicePrincipals/$spObjectId/appRoleAssignments" @{
                principalId = $spObjectId
                resourceId  = $graphSp.id
                appRoleId   = $role.id
            } | Out-Null
            Write-Ok "$MailSendRole consented (tenant-wide; steps 2 and 3 narrow it to one mailbox)"
        }
    }
}

# ── 2. Exchange Online: the app's service principal ───────────────────────────
Write-Step '2/3 Exchange Online service principal'
Install-IfMissing 'ExchangeOnlineManagement'
Import-Module ExchangeOnlineManagement -ErrorAction Stop

if ($useDeviceCode) { Connect-ExchangeOnline -Device -ShowBanner:$false }
else { Connect-ExchangeOnline -ShowBanner:$false }
Write-Note 'signed in to Exchange Online'

$mailbox = Get-Mailbox -Identity $SenderMailbox -ErrorAction SilentlyContinue
if (-not $mailbox) { throw "The mailbox '$SenderMailbox' was not found in this tenant." }
Write-Note "mailbox found: $($mailbox.DisplayName) <$($mailbox.PrimarySmtpAddress)>"

$rbacAvailable = (Test-Cmdlet 'New-ServicePrincipal') -and (Test-Cmdlet 'New-ManagementRoleAssignment')
$useRbac = $rbacAvailable -and -not $UseApplicationAccessPolicy

if ($useRbac) {
    $exoSp = Get-ServicePrincipal -Identity $AppId -ErrorAction SilentlyContinue
    if ($exoSp) {
        Write-Skip "Exchange service principal exists ($($exoSp.ServicePrincipalName))"
    }
    else {
        # Exchange needs the Entra service principal's OBJECT id, not the application id.
        if (-not $spObjectId) {
            throw "The service principal object id for $AppId is unknown, because step 1 was skipped. " +
                  'Re-run without -SkipGraphConsent, or register it by hand: ' +
                  'New-ServicePrincipal -AppId <appId> -ObjectId <objectId>'
        }
        if ($PSCmdlet.ShouldProcess($AppId, 'Register the service principal in Exchange Online')) {
            New-ServicePrincipal -AppId $AppId -ObjectId $spObjectId -DisplayName "$Name ($AppId)" | Out-Null
            Write-Ok 'registered the service principal in Exchange Online'
        }
    }
}
else {
    Write-Note 'RBAC for Applications is not in use here; step 3 will create an application access policy.'
}

# ── 3. Exchange Online: restrict the app to this one mailbox ──────────────────
Write-Step '3/3 Restrict the app to a single mailbox'

if ($useRbac) {
    $scopeName = "$Name-Scope"
    $assignmentName = "$Name-MailSend"
    # Quote-safe: an apostrophe in an address would otherwise break the OPATH filter.
    $filter = "PrimarySmtpAddress -eq '{0}'" -f $SenderMailbox.Replace("'", "''")

    $scope = Get-ManagementScope -Identity $scopeName -ErrorAction SilentlyContinue
    if ($scope) {
        Write-Skip "management scope '$scopeName' exists: $($scope.RecipientFilter)"
        if ($scope.RecipientFilter -notmatch [regex]::Escape($SenderMailbox)) {
            Write-Warning ("The existing scope '$scopeName' does NOT match $SenderMailbox. " +
                           'Review it, or re-run with a different -Name.')
        }
    }
    elseif ($PSCmdlet.ShouldProcess($scopeName, "Create a management scope limited to $SenderMailbox")) {
        New-ManagementScope -Name $scopeName -RecipientRestrictionFilter $filter | Out-Null
        Write-Ok "created the management scope '$scopeName'"
    }

    $assignment = Get-ManagementRoleAssignment -Identity $assignmentName -ErrorAction SilentlyContinue
    if ($assignment) {
        Write-Skip "role assignment '$assignmentName' exists"
    }
    elseif ($PSCmdlet.ShouldProcess($assignmentName, "Assign 'Application Mail.Send' scoped to $scopeName")) {
        New-ManagementRoleAssignment -Name $assignmentName -App $AppId `
            -Role 'Application Mail.Send' -CustomResourceScope $scopeName | Out-Null
        Write-Ok "assigned 'Application Mail.Send', scoped to $SenderMailbox"
    }

    Write-Note ''
    Write-Note 'Verify with:'
    Write-Note "  Get-ManagementRoleAssignment -Identity $assignmentName | Format-List Name,Role,CustomResourceScope"
}
else {
    if (-not (Test-Cmdlet 'New-ApplicationAccessPolicy')) {
        throw 'Neither RBAC for Applications nor application access policies are available in this ' +
              'session. Update the ExchangeOnlineManagement module, reconnect, and run this again.'
    }

    # Older mechanism: a mail-enabled security group holds the allowed mailboxes and the
    # policy restricts the app to that group. It must be a mail-enabled SECURITY group;
    # a Microsoft 365 group does not work here.
    $groupAlias = $Name.ToLowerInvariant()
    $domain = $SenderMailbox.Split('@')[1]
    $groupAddress = "$groupAlias@$domain"

    $group = Get-DistributionGroup -Identity $groupAddress -ErrorAction SilentlyContinue
    if ($group) {
        Write-Skip "security group '$groupAddress' exists"
    }
    elseif ($PSCmdlet.ShouldProcess($groupAddress, 'Create the mail-enabled security group')) {
        $group = New-DistributionGroup -Name $Name -Alias $groupAlias -PrimarySmtpAddress $groupAddress `
            -Type Security -Members $SenderMailbox
        Write-Ok "created the mail-enabled security group '$groupAddress'"
    }

    if ($group) {
        $members = @(Get-DistributionGroupMember -Identity $groupAddress -ErrorAction SilentlyContinue)
        $isMember = @($members | ForEach-Object { $_.PrimarySmtpAddress }) -contains $mailbox.PrimarySmtpAddress
        if ($isMember) {
            Write-Skip "$SenderMailbox is already a member"
        }
        elseif ($PSCmdlet.ShouldProcess($groupAddress, "Add $SenderMailbox as a member")) {
            Add-DistributionGroupMember -Identity $groupAddress -Member $SenderMailbox
            Write-Ok "added $SenderMailbox to the group"
        }
    }

    $policy = @(Get-ApplicationAccessPolicy -ErrorAction SilentlyContinue | Where-Object { $_.AppId -eq $AppId })[0]
    if ($policy) {
        Write-Skip "an application access policy for this app already exists ($($policy.AccessRight))"
    }
    elseif ($PSCmdlet.ShouldProcess($AppId, "Restrict the app to $groupAddress")) {
        New-ApplicationAccessPolicy -AppId $AppId -PolicyScopeGroupId $groupAddress `
            -AccessRight RestrictAccess `
            -Description "Hive BDC may send only as members of $groupAddress" | Out-Null
        Write-Ok 'created the application access policy'
    }

    Write-Note ''
    Write-Note 'Verify with (Granted = allowed):'
    Write-Note "  Test-ApplicationAccessPolicy -Identity $SenderMailbox -AppId $AppId"
    Write-Note '  and confirm another mailbox comes back Denied:'
    Write-Note "  Test-ApplicationAccessPolicy -Identity <someone.else@$domain> -AppId $AppId"
}

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
Write-Note "The app can send as $SenderMailbox, and as nothing else."
Write-Note 'Allow up to 30 minutes for Exchange Online to apply this everywhere; until then a send'
Write-Note 'can still fail with ErrorAccessDenied, which is propagation rather than a misconfiguration.'
Write-Note ''
Write-Note 'Then in the Hive BDC dashboard, Settings > Document mail:'
Write-Note "  Send from      $SenderMailbox"
Write-Note "  Client ID      $AppId"
Write-Note '  Client secret  from the app registration'
Write-Note '  Test recipients  the addresses that receive documents from a non-production AX'
Write-Note 'and press "Test mail".'

Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue | Out-Null

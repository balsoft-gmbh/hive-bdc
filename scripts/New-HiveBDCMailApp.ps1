# =====================================================================================
#  Hive BDC - create the mail app registration and let it send as ONE mailbox
#  -----------------------------------------------------------------------------------
#  Paste this whole file into Azure Cloud Shell (PowerShell) and press Enter.
#  It runs to the end and prints the Application (client) ID you need.
#
#  You need Global Administrator (steps 1-4) and Exchange Administrator (steps 5-6).
#  Sign-in uses a device code: watch the console for the code and the URL.
#
#  What it does, skipping anything that already exists so it is safe to run twice:
#    1. installs the two PowerShell modules it needs, for your user only
#    2. creates the app registration "Hive BDC Mail"
#    3. adds the Microsoft Graph APPLICATION permission Mail.Send to it
#    4. grants admin consent for that permission
#    5. registers the app in Exchange Online
#    6. restricts it so it can send as the one mailbox set below and no other
#
#  Step 4 on its own is send-as for EVERY mailbox in the tenant. Steps 5 and 6 are what
#  narrow it down, which is why this is one script and not a portal click-through.
#
#  It never asks for or stores a secret. When it finishes, create the client secret
#  yourself in the portal (the exact path is printed at the end) and paste it into the
#  Hive BDC dashboard under Settings > Document mail.
# =====================================================================================

& {
    # ---- edit these if needed -------------------------------------------------------
    $SenderMailbox = 'invoices@contoso.com'     # the ONLY mailbox the app may send as: SET THIS
    $AppName       = 'Hive BDC Mail'       # name of the app registration
    $Name          = 'HiveBDCMail'         # prefix for the Exchange objects created
    # ---------------------------------------------------------------------------------

    # Scoped to this block, so your console keeps its own settings afterwards.
    $ErrorActionPreference = 'Stop'

    $GraphAppId  = '00000003-0000-0000-c000-000000000000'   # Microsoft Graph, every tenant
    $GraphV1     = 'https://graph.microsoft.com/v1.0'
    $MailSendRole = 'Mail.Send'

    function Step { param($t) Write-Host "`n== $t" -ForegroundColor Cyan }
    function Ok   { param($t) Write-Host "   [ok]   $t" -ForegroundColor Green }
    function Have { param($t) Write-Host "   [have] $t" -ForegroundColor DarkGray }
    function Note { param($t) Write-Host "   $t" -ForegroundColor Gray }

    function Need-Module {
        param($ModuleName)
        if (Get-Module -ListAvailable -Name $ModuleName) { return }
        Note "installing $ModuleName for your user (one time, takes a minute)..."
        # -Force already suppresses the untrusted-gallery prompt; trusting it as well keeps
        # older PowerShellGet quiet. Not every host has Get-PSRepository, so this may fail.
        try {
            if ((Get-PSRepository -Name PSGallery).InstallationPolicy -ne 'Trusted') {
                Set-PSRepository -Name PSGallery -InstallationPolicy Trusted
            }
        }
        catch { }
        Install-Module -Name $ModuleName -Scope CurrentUser -Force -AllowClobber
        Ok "$ModuleName installed"
    }

    function Graph {
        param($Method, $Uri, $Body)
        if ($Body) {
            return Invoke-MgGraphRequest -Method $Method -Uri $Uri `
                -Body ($Body | ConvertTo-Json -Depth 8) -ContentType 'application/json' -OutputType PSObject
        }
        return Invoke-MgGraphRequest -Method $Method -Uri $Uri -OutputType PSObject
    }

    # Cloud Shell has no browser to hand off to, so a device code is the way in.
    $useDeviceCode = [bool]$env:ACC_CLOUD -or
                     ("$env:AZUREPS_HOST_ENVIRONMENT" -like '*cloud-shell*') -or
                     ($PSVersionTable.PSVersion.Major -ge 6 -and -not $IsWindows)

    Write-Host 'Hive BDC - mail app registration' -ForegroundColor White
    Note "app registration : $AppName"
    Note "sender mailbox   : $SenderMailbox"

    # -- 1. modules ------------------------------------------------------------------
    Step '1/6 PowerShell modules'
    Need-Module 'Microsoft.Graph.Authentication'
    Need-Module 'ExchangeOnlineManagement'
    Import-Module Microsoft.Graph.Authentication
    Import-Module ExchangeOnlineManagement
    Ok 'modules ready'

    # -- 2. app registration ----------------------------------------------------------
    Step '2/6 App registration'
    $connect = @{ Scopes = @('Application.ReadWrite.All', 'AppRoleAssignment.ReadWrite.All'); NoWelcome = $true }
    if ($useDeviceCode) { $connect['UseDeviceCode'] = $true }
    Connect-MgGraph @connect

    $ctx = Get-MgContext
    $tenantId = $ctx.TenantId
    Note "signed in to tenant $tenantId as $($ctx.Account)"

    $graphSp = @((Graph GET "$GraphV1/servicePrincipals?`$filter=appId eq '$GraphAppId'&`$select=id,appRoles").value)[0]
    $role = @($graphSp.appRoles | Where-Object { $_.value -eq $MailSendRole -and $_.allowedMemberTypes -contains 'Application' })[0]
    if (-not $role) { throw "The application role $MailSendRole was not found on Microsoft Graph." }

    $escapedName = $AppName.Replace("'", "''")
    $app = @((Graph GET "$GraphV1/applications?`$filter=displayName eq '$escapedName'&`$select=id,appId,displayName").value)[0]

    if ($app) {
        Have "app registration exists: $($app.displayName) (appId $($app.appId))"
    }
    else {
        # requiredResourceAccess is what makes the permission show up on the portal's
        # "API permissions" page. The consent in step 4 is what actually grants it.
        $app = Graph POST "$GraphV1/applications" @{
            displayName            = $AppName
            signInAudience         = 'AzureADMyOrg'
            requiredResourceAccess = @(
                @{
                    resourceAppId  = $GraphAppId
                    resourceAccess = @(@{ id = $role.id; type = 'Role' })
                }
            )
        }
        Ok "created the app registration (appId $($app.appId))"
        Start-Sleep -Seconds 5   # give the directory a moment before creating its principal
    }
    $appId = $app.appId

    # -- 3. service principal ---------------------------------------------------------
    Step '3/6 Service principal'
    $appSp = @((Graph GET "$GraphV1/servicePrincipals?`$filter=appId eq '$appId'&`$select=id,displayName").value)[0]
    if ($appSp) {
        Have "service principal exists ($($appSp.id))"
    }
    else {
        # An app registration has no service principal until something creates one, and
        # nothing can be granted to it before that exists.
        $appSp = Graph POST "$GraphV1/servicePrincipals" @{ appId = $appId }
        Ok "created the service principal ($($appSp.id))"
    }
    $spObjectId = $appSp.id

    # -- 4. admin consent for Mail.Send -----------------------------------------------
    Step "4/6 Admin consent for $MailSendRole"
    $assignments = Graph GET "$GraphV1/servicePrincipals/$spObjectId/appRoleAssignments"
    $already = @($assignments.value | Where-Object { $_.appRoleId -eq $role.id -and $_.resourceId -eq $graphSp.id })[0]
    if ($already) {
        Have "$MailSendRole is already consented"
    }
    else {
        Graph POST "$GraphV1/servicePrincipals/$spObjectId/appRoleAssignments" @{
            principalId = $spObjectId
            resourceId  = $graphSp.id
            appRoleId   = $role.id
        } | Out-Null
        Ok "$MailSendRole consented (tenant-wide for now; step 6 narrows it to one mailbox)"
    }

    # -- 5. Exchange Online service principal -----------------------------------------
    Step '5/6 Exchange Online'
    if ($useDeviceCode) { Connect-ExchangeOnline -Device -ShowBanner:$false }
    else { Connect-ExchangeOnline -ShowBanner:$false }
    Note 'signed in to Exchange Online'

    $mailbox = Get-Mailbox -Identity $SenderMailbox -ErrorAction SilentlyContinue
    if (-not $mailbox) { throw "The mailbox $SenderMailbox was not found in this tenant." }
    Note "mailbox found: $($mailbox.DisplayName) <$($mailbox.PrimarySmtpAddress)>"

    $rbac = [bool](Get-Command New-ServicePrincipal -ErrorAction SilentlyContinue) -and
            [bool](Get-Command New-ManagementRoleAssignment -ErrorAction SilentlyContinue)

    if ($rbac) {
        if (Get-ServicePrincipal -Identity $appId -ErrorAction SilentlyContinue) {
            Have 'Exchange service principal exists'
        }
        else {
            # Exchange wants the service principal OBJECT id, not the application id.
            New-ServicePrincipal -AppId $appId -ObjectId $spObjectId -DisplayName "$Name ($appId)" | Out-Null
            Ok 'registered the app in Exchange Online'
        }
    }
    else {
        Note 'RBAC for Applications is not available here; an application access policy will be used.'
    }

    # -- 6. restrict to the one mailbox -----------------------------------------------
    Step '6/6 Restrict the app to a single mailbox'

    if ($rbac) {
        $scopeName = "$Name-Scope"
        $assignmentName = "$Name-MailSend"
        $filter = "PrimarySmtpAddress -eq '{0}'" -f $SenderMailbox.Replace("'", "''")

        $scope = Get-ManagementScope -Identity $scopeName -ErrorAction SilentlyContinue
        if ($scope) {
            Have "management scope exists: $($scope.RecipientFilter)"
            if ($scope.RecipientFilter -notmatch [regex]::Escape($SenderMailbox)) {
                Write-Warning "The existing scope $scopeName does NOT match $SenderMailbox. Review it before relying on this."
            }
        }
        else {
            New-ManagementScope -Name $scopeName -RecipientRestrictionFilter $filter | Out-Null
            Ok "created the management scope, limited to $SenderMailbox"
        }

        if (Get-ManagementRoleAssignment -Identity $assignmentName -ErrorAction SilentlyContinue) {
            Have 'role assignment exists'
        }
        else {
            New-ManagementRoleAssignment -Name $assignmentName -App $appId `
                -Role 'Application Mail.Send' -CustomResourceScope $scopeName | Out-Null
            Ok "assigned 'Application Mail.Send', scoped to $SenderMailbox"
        }

        $verify = "Get-ManagementRoleAssignment -Identity $assignmentName | Format-List Name,Role,CustomResourceScope"
    }
    else {
        # Older mechanism: a mail-enabled SECURITY group holds the allowed mailboxes and
        # the policy restricts the app to that group. A Microsoft 365 group does not work.
        $groupAlias   = $Name.ToLowerInvariant()
        $domain       = $SenderMailbox.Split('@')[1]
        $groupAddress = "$groupAlias@$domain"

        if (Get-DistributionGroup -Identity $groupAddress -ErrorAction SilentlyContinue) {
            Have "security group $groupAddress exists"
        }
        else {
            New-DistributionGroup -Name $Name -Alias $groupAlias -PrimarySmtpAddress $groupAddress `
                -Type Security -Members $SenderMailbox | Out-Null
            Ok "created the mail-enabled security group $groupAddress"
        }

        $members = @(Get-DistributionGroupMember -Identity $groupAddress -ErrorAction SilentlyContinue)
        if (@($members | ForEach-Object { $_.PrimarySmtpAddress }) -contains $mailbox.PrimarySmtpAddress) {
            Have "$SenderMailbox is already a member"
        }
        else {
            Add-DistributionGroupMember -Identity $groupAddress -Member $SenderMailbox
            Ok "added $SenderMailbox to the group"
        }

        if (@(Get-ApplicationAccessPolicy -ErrorAction SilentlyContinue | Where-Object { $_.AppId -eq $appId })[0]) {
            Have 'an application access policy for this app already exists'
        }
        else {
            New-ApplicationAccessPolicy -AppId $appId -PolicyScopeGroupId $groupAddress `
                -AccessRight RestrictAccess `
                -Description "Hive BDC may send only as members of $groupAddress" | Out-Null
            Ok 'created the application access policy'
        }

        $verify = "Test-ApplicationAccessPolicy -Identity $SenderMailbox -AppId $appId"
    }

    Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue | Out-Null

    # -- what to do next ---------------------------------------------------------------
    Write-Host ''
    Write-Host '=====================================================================' -ForegroundColor Yellow
    Write-Host ' DONE. Write these two down.' -ForegroundColor Yellow
    Write-Host '=====================================================================' -ForegroundColor Yellow
    Write-Host ''
    Write-Host "  Application (client) ID :  $appId" -ForegroundColor White
    Write-Host "  Directory (tenant) ID   :  $tenantId" -ForegroundColor White
    Write-Host "  Send from               :  $SenderMailbox" -ForegroundColor White
    Write-Host ''
    Write-Host ' NEXT: create the client secret yourself' -ForegroundColor Yellow
    Note 'https://entra.microsoft.com  >  Applications  >  App registrations'
    Note "  >  $AppName  >  Certificates & secrets  >  New client secret"
    Note '  Copy the VALUE straight away. It is shown once and never again.'
    Write-Host ''
    Write-Host ' THEN: Hive BDC dashboard  >  Settings  >  Document mail' -ForegroundColor Yellow
    Note "  Enabled          yes"
    Note "  Client ID        $appId"
    Note '  Client secret    the value you just copied'
    Note "  Send from        $SenderMailbox"
    Note '  Test recipients  the addresses that receive documents from a non-production AX'
    Note '  then press "Test mail".'
    Write-Host ''
    Note 'Allow up to 30 minutes before the mailbox restriction applies everywhere. Until'
    Note 'then a send can still fail with ErrorAccessDenied; that is propagation, not a'
    Note 'mistake in the setup.'
    Write-Host ''
    Note 'To check the restriction later:'
    Note "  $verify"
}

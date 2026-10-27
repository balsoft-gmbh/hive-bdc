<#
.SYNOPSIS
    Step 2 of 2 for SharePoint: gives the Hive BDC SharePoint app write access to the sites
    you name, and to no other.

.DESCRIPTION
    Run in Azure Cloud Shell (PowerShell) after New-HiveBDCSharePointApp.ps1.
    Steps, each skipped when already in place:
      1. Azure CLI: show the tenant and the app, and ask before changing anything.
      2. Entra ID: Microsoft Graph application permission Sites.Selected with admin consent.
         Sites.Selected alone opens no site at all.
      3. SharePoint: write access on each -SiteUrl. This needs the delegated permission
         Sites.FullControl.All, which an Azure CLI sign-in does not carry, so it signs in
         once more through Microsoft's public client "Microsoft Graph Command Line Tools"
         (the one Connect-MgGraph uses): in a browser on a PC, by code in Cloud Shell. No
         PowerShell module needed. If the tenant blocks sign-in by code, run this script on
         your own computer instead of Cloud Shell.

    -AllSites grants Sites.ReadWrite.All (every site) instead and skips step 3.
    Roles: Privileged Role Administrator or Global Administrator for the consent, and
    SharePoint Administrator who is a member of each site. -WhatIf shows the plan only.
    Another site later: run it again with that site.

.EXAMPLE
    ./Grant-HiveBDCSharePointAccess.ps1 -AppId 11111111-2222-3333-4444-555555555555 -SiteUrl https://contoso.sharepoint.com/sites/Invoices
#>
#Requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    # Application (client) ID printed by New-HiveBDCSharePointApp.ps1.
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$')]
    [string] $AppId,

    # Sites Hive BDC uploads to; a library or folder URL is reduced to its site.
    [ValidatePattern('^https://[^/\s]+\.sharepoint\.(com|us|de|cn)(/\S*)?$')]
    [string[]] $SiteUrl = @(),

    [switch] $AllSites,

    [ValidatePattern('^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$')]
    [string] $TenantId,

    # Do not ask before changing the tenant.
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
if (-not $AllSites -and $SiteUrl.Count -eq 0) { throw 'Name the sites with -SiteUrl, or use -AllSites.' }

$GraphAppId = '00000003-0000-0000-c000-000000000000'   # Microsoft Graph, same in every tenant
$GraphV1 = 'https://graph.microsoft.com/v1.0'
$RoleName = if ($AllSites) { 'Sites.ReadWrite.All' } else { 'Sites.Selected' }

function Write-Step { param([string] $Text) Write-Host "`n== $Text" -ForegroundColor Cyan }
function Write-Ok { param([string] $Text) Write-Host "   [ok]   $Text" -ForegroundColor Green }
function Write-Skip { param([string] $Text) Write-Host "   [have] $Text" -ForegroundColor DarkGray }
function Write-Note { param([string] $Text) Write-Host "   $Text" -ForegroundColor Gray }

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

# The application ids a site permission is granted to, from either property Graph returns.
function Get-PermissionAppIds {
    param($Permission)
    foreach ($property in @('grantedToIdentitiesV2', 'grantedToIdentities')) {
        if ($Permission.PSObject.Properties[$property]) {
            foreach ($identity in @($Permission.$property)) {
                if ($identity -and $identity.PSObject.Properties['application'] -and $identity.application) { $identity.application.id }
            }
        }
    }
}

# A delegated Microsoft Graph token for Sites.FullControl.All, which Azure CLI cannot get.
# It signs in through Microsoft's public client "Microsoft Graph Command Line Tools" (the
# one Connect-MgGraph uses) without that module, whose current versions fail on older
# PowerShell hosts. With a browser: authorization code with PKCE on a localhost redirect.
# Without one (Cloud Shell): a device code, which some tenants block by Conditional Access.
# The token stays in memory.
$GraphCliClientId = '14d82eec-204b-4c2f-b7e8-296a70dab67e'

function ConvertTo-Base64Url { param([byte[]] $Bytes) [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') }

function Get-BrowserToken {
    param([string] $Tenant, [string] $Scope)
    $probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $probe.Start(); $port = $probe.LocalEndpoint.Port; $probe.Stop()
    $redirect = "http://localhost:$port/"
    $listener = [System.Net.HttpListener]::new()
    $listener.Prefixes.Add($redirect)
    $listener.Start()
    try {
        $random = [byte[]]::new(32)
        [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($random)
        $verifier = ConvertTo-Base64Url $random
        $sha = [System.Security.Cryptography.SHA256]::Create()
        $challenge = ConvertTo-Base64Url ($sha.ComputeHash([Text.Encoding]::ASCII.GetBytes($verifier)))
        $state = [guid]::NewGuid().ToString('N')
        $query = @{
            client_id = $GraphCliClientId; response_type = 'code'; redirect_uri = $redirect; scope = $Scope
            code_challenge = $challenge; code_challenge_method = 'S256'; state = $state; prompt = 'select_account'
        }.GetEnumerator() | ForEach-Object { "$($_.Key)=$([Uri]::EscapeDataString($_.Value))" }
        $url = "https://login.microsoftonline.com/$Tenant/oauth2/v2.0/authorize?" + ($query -join '&')

        Write-Note 'A browser window opens for the sign-in. If it does not, open this address:'
        Write-Note "  $url"
        if ($IsMacOS) { & open $url } elseif ($IsLinux) { & xdg-open $url 2>$null } else { Start-Process $url }

        $pending = $listener.GetContextAsync()
        if (-not $pending.Wait([TimeSpan]::FromMinutes(5))) { throw 'No sign-in within 5 minutes. Run the script again.' }
        $context = $pending.Result
        $answer = $context.Request.QueryString
        $page = [Text.Encoding]::UTF8.GetBytes('<html><body style="font-family:sans-serif">Signed in. You can close this tab and go back to PowerShell.</body></html>')
        $context.Response.ContentType = 'text/html; charset=utf-8'
        $context.Response.OutputStream.Write($page, 0, $page.Length)
        $context.Response.Close()

        if ($answer['error']) { throw "Sign-in failed: $($answer['error_description'])" }
        if ($answer['state'] -ne $state) { throw 'Sign-in answer did not match this request.' }
        $token = Invoke-RestMethod -Method POST -Uri "https://login.microsoftonline.com/$Tenant/oauth2/v2.0/token" -Body @{
            grant_type = 'authorization_code'; client_id = $GraphCliClientId; code = $answer['code']
            redirect_uri = $redirect; code_verifier = $verifier; scope = $Scope
        }
        return $token.access_token
    }
    finally { $listener.Stop(); $listener.Close() }
}

function Get-DeviceCodeToken {
    param([string] $Tenant, [string] $Scope)
    $base = "https://login.microsoftonline.com/$Tenant/oauth2/v2.0"
    $code = Invoke-RestMethod -Method POST -Uri "$base/devicecode" -Body @{ client_id = $GraphCliClientId; scope = $Scope }
    Write-Host "   $($code.message)" -ForegroundColor Yellow
    $deadline = (Get-Date).AddSeconds([int]$code.expires_in)
    $interval = [int]$code.interval
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds $interval
        try {
            $token = Invoke-RestMethod -Method POST -Uri "$base/token" -Body @{
                grant_type = 'urn:ietf:params:oauth:grant-type:device_code'; client_id = $GraphCliClientId; device_code = $code.device_code
            }
            return $token.access_token
        }
        catch {
            $reason = try { ($_.ErrorDetails.Message | ConvertFrom-Json).error } catch { '' }
            if ($reason -eq 'authorization_pending') { continue }
            if ($reason -eq 'slow_down') { $interval += 5; continue }
            if ($reason -in @('access_denied', 'invalid_grant')) {
                throw ('Sign-in with a code was refused; your organisation may block it. Run this script on your own ' +
                       'computer instead (PowerShell with Azure CLI), where the sign-in opens a browser.')
            }
            throw
        }
    }
    throw 'The sign-in code expired. Run the script again.'
}

if ($WhatIfPreference) { Write-Host 'DRY RUN - nothing will be changed.' -ForegroundColor Yellow }

# -- 1. sign-in -----------------------------------------------------------------------
Write-Step '1/3 Sign-in'
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
if (-not $sp) { throw "The app $appId has no service principal. Run New-HiveBDCSharePointApp.ps1 first." }
Write-Note "app:     $AppName ($appId)"
Write-Note "access:  $(if ($AllSites) { 'Sites.ReadWrite.All, every site' } else { 'Sites.Selected, write on the sites below' })"
foreach ($url in $SiteUrl) { Write-Note "site:    $url" }
if (-not $Force -and -not $WhatIfPreference) {
    $answer = Read-Host "   Grant this access in '$tenantName'? (y/n)"
    if ($answer -notmatch '^(y|yes|j|ja)$') { Write-Note 'Stopped. Nothing was changed.'; return }
}

# -- 2. permission and admin consent ---------------------------------------------------
Write-Step "2/3 Permission $RoleName"
$graphSp = Invoke-Az ad sp show --id $GraphAppId
$role = @($graphSp.appRoles | Where-Object { $_.value -eq $RoleName -and $_.allowedMemberTypes -contains 'Application' }) |
    Select-Object -First 1

# Listing the permission makes it show under "API permissions"; the consent below grants it.
$listed = @($app.requiredResourceAccess | Where-Object { $_.resourceAppId -eq $GraphAppId } |
    ForEach-Object { $_.resourceAccess } | Where-Object { $_.id -eq $role.id })
if ($listed) { Write-Skip "$RoleName is listed" }
elseif ($PSCmdlet.ShouldProcess($AppName, "List the permission $RoleName")) {
    Invoke-Az ad app permission add --id $appId --api $GraphAppId --api-permissions "$($role.id)=Role" | Out-Null
    Write-Ok "listed $RoleName"
}

$consented = @((Invoke-GraphAz GET "servicePrincipals/$($sp.id)/appRoleAssignments").value |
    Where-Object { $_.appRoleId -eq $role.id -and $_.resourceId -eq $graphSp.id })
if ($consented) { Write-Skip "$RoleName is consented" }
elseif ($PSCmdlet.ShouldProcess($AppName, "Grant admin consent for $RoleName")) {
    Invoke-GraphAz POST "servicePrincipals/$($sp.id)/appRoleAssignments" @{
        principalId = $sp.id; resourceId = $graphSp.id; appRoleId = $role.id
    } | Out-Null
    Write-Ok "$RoleName consented"
}

# -- 3. write access per site ---------------------------------------------------------
$failed = @()
if ($AllSites) { Write-Step '3/3 Sites (not needed with -AllSites)' }
else {
    Write-Step '3/3 Write access on the sites'
    Write-Note 'Granting a site needs Sites.FullControl.All for your account; sign in once more:'
    $scope = 'https://graph.microsoft.com/Sites.FullControl.All'
    $graphToken = if ($useDeviceCode) { Get-DeviceCodeToken $tenantId $scope } else { Get-BrowserToken $tenantId $scope }
    $headers = @{ Authorization = "Bearer $graphToken" }

    foreach ($url in $SiteUrl) {
        $uri = [Uri]$url
        $segments = @($uri.AbsolutePath.Trim('/').Split('/') | Where-Object { $_ })
        $siteRef = if ($segments.Count -ge 2 -and $segments[0] -in @('sites', 'teams')) {
            "$($uri.Host):/$($segments[0])/$([Uri]::UnescapeDataString($segments[1]))"
        } else { $uri.Host }
        try {
            $site = Invoke-RestMethod -Headers $headers -Uri "$GraphV1/sites/$siteRef`?`$select=id,webUrl"
            $permissions = Invoke-RestMethod -Headers $headers -Uri "$GraphV1/sites/$($site.id)/permissions"
            $existing = @($permissions.value | Where-Object { (Get-PermissionAppIds $_) -contains $appId }) | Select-Object -First 1

            if ($existing -and @($existing.roles | Where-Object { $_ -in @('write', 'owner', 'fullcontrol') })) {
                Write-Skip "$($site.webUrl): $($existing.roles -join ', ')"
            }
            elseif ($existing) {
                if ($PSCmdlet.ShouldProcess($site.webUrl, 'Raise the access to write')) {
                    Invoke-RestMethod -Headers $headers -Method PATCH -Uri "$GraphV1/sites/$($site.id)/permissions/$($existing.id)" `
                        -Body (@{ roles = @('write') } | ConvertTo-Json) -ContentType 'application/json' | Out-Null
                    Write-Ok "$($site.webUrl): raised to write"
                }
            }
            elseif ($PSCmdlet.ShouldProcess($site.webUrl, 'Grant write access')) {
                $body = @{ roles = @('write'); grantedToIdentities = @(@{ application = @{ id = $appId; displayName = $AppName } }) }
                Invoke-RestMethod -Headers $headers -Method POST -Uri "$GraphV1/sites/$($site.id)/permissions" `
                    -Body ($body | ConvertTo-Json -Depth 5) -ContentType 'application/json' | Out-Null
                Write-Ok "$($site.webUrl): write"
            }
        }
        catch {
            $failed += $url
            $detail = if ($_.ErrorDetails.Message) { $_.ErrorDetails.Message } else { $_.Exception.Message }
            Write-Warning "$url : $detail"
            Write-Note 'Your account needs SharePoint Administrator and has to be a member of this site.'
        }
    }
}

# -- next -----------------------------------------------------------------------------
Write-Host "`nDONE" -ForegroundColor Yellow
Write-Host "  Tenant ID  $tenantId"
Write-Host "  Client ID  $appId"
if ($failed) { Write-Host "  NOT granted: $($failed -join ', ') (fix the cause above and run again)" -ForegroundColor Red }
Write-Note 'Hive BDC dashboard: Settings > Connections > SharePoint, then Test SharePoint.'

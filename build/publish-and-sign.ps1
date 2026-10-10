# Publish + Authenticode-sign Hive BDC, then build the setup wizard.
# Signing uses the Balsoft GmbH Sectigo OV cert whose private key lives in
# Google Cloud KMS (no PFX exists). Prerequisites on the build host:
#   - Google Cloud KMS CNG provider (kmscng.msi, provider "Google Cloud KMS Provider")
#   - signtool.exe (Windows SDK or Microsoft.Windows.SDK.BuildTools NuGet)
#   - gcloud Application Default Credentials with roles/cloudkms.signer on the key
#     (one-time: gcloud auth application-default login)
# Signing is the LAST packaging step per artifact: the exes are signed before
# they are embedded into Setup, and Setup (which is also the uninstaller) last.
# Run it with PowerShell 7 (pwsh): Windows PowerShell 5.1 misreads this UTF-8 file.
param(
    [string]$SignTool = $env:SIGNTOOL,           # path to signtool.exe; falls back to PATH
    [switch]$SkipSign,                           # unsigned dev build
    [switch]$SkipInstaller                       # publish + zip only
)
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$out = Join-Path $repo "publish\win-x64"
$kmsKey = "projects/balsoft-codesigning/locations/europe-west3/keyRings/balsoft-codesigning/cryptoKeys/balsoft-rsa3072/cryptoKeyVersions/1"
$cert = Join-Path $PSScriptRoot "BalsoftGmbH.cer"

if (-not $SignTool) { $SignTool = "signtool.exe" }

# A single-file publish CLEANS its output folder, and an incremental build can then decide
# the content files are already copied and skip them, leaving the installer to fail late on
# "Source file ... does not exist". Clearing the folder first and checking afterwards keeps
# that from reaching the installer build.
# Tests gate the release: nothing is published or signed from a tree whose tests fail.
dotnet test (Join-Path $repo "tests\Balsoft.Hive.BDC.Tests\Balsoft.Hive.BDC.Tests.csproj") -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet test failed - release aborted" }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish (Join-Path $repo "Balsoft.Hive.BDC.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

foreach ($contentFile in 'appsettings.json', 'install.bat', 'uninstall.bat') {
    $published = Join-Path $out $contentFile
    if (Test-Path $published) { continue }
    $source = Join-Path $repo $contentFile
    if (-not (Test-Path $source)) { throw "$contentFile is missing from the repo." }
    Write-Warning "$contentFile was not published (stale incremental state); copying it from the repo."
    Copy-Item $source $published
}

$exe = Join-Path $out "HiveBDC.exe"
if (-not $SkipSign) {
    & $SignTool sign /v /fd sha256 /td sha256 /tr http://timestamp.sectigo.com `
        /d "Hive BDC, by Balsoft" `
        /f $cert /csp "Google Cloud KMS Provider" /kc $kmsKey `
        $exe
    if ($LASTEXITCODE -ne 0) { throw "signtool sign failed" }

    & $SignTool verify /pa /v $exe
    if ($LASTEXITCODE -ne 0) { throw "signature verification failed" }
}

# ── Dashboard app (self-contained single-file win-x64), signed ──
# Publish to a SEPARATE folder: a single-file publish CLEANS its output dir, so
# publishing into $out would wipe the service's appsettings.json. Then copy only
# the exe next to the service so the installer bundles both from one folder.
$appOut = Join-Path $repo "publish\app"
dotnet publish (Join-Path $repo "app\HiveBDC.App\HiveBDC.App.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $appOut
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (dashboard app) failed" }

$appExe = Join-Path $out "HiveBDC.App.exe"
Copy-Item (Join-Path $appOut "HiveBDC.App.exe") $appExe -Force
if (-not $SkipSign) {
    & $SignTool sign /v /fd sha256 /td sha256 /tr http://timestamp.sectigo.com `
        /d "Hive BDC Dashboard, by Balsoft" `
        /f $cert /csp "Google Cloud KMS Provider" /kc $kmsKey `
        $appExe
    if ($LASTEXITCODE -ne 0) { throw "signtool sign (app) failed" }

    & $SignTool verify /pa /v $appExe
    if ($LASTEXITCODE -ne 0) { throw "signature verification (app) failed" }
}

$version = (Select-Xml -Path (Join-Path $repo "Balsoft.Hive.BDC.csproj") -XPath "//Version").Node.InnerText
$zip = Join-Path $repo "publish\HiveBDC-v$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip
Write-Host "Release artifact: $zip"

if (-not $SkipInstaller) {
    # ── Setup (setup\HiveBDC.Setup on Balsoft.Hive.Installer.Windows) ──
    # The signed program files are collected in publish\payload; the installer package's build
    # targets pack them and embed them in Setup. Setup itself is signed last.
    $payloadDir = Join-Path $repo "publish\payload"
    if (Test-Path $payloadDir) { Remove-Item $payloadDir -Recurse -Force }
    New-Item -ItemType Directory $payloadDir | Out-Null
    foreach ($f in 'HiveBDC.exe', 'HiveBDC.App.exe', 'appsettings.json') { Copy-Item (Join-Path $out $f) $payloadDir }
    foreach ($f in 'README.md', 'EULA.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md') { Copy-Item (Join-Path $repo $f) $payloadDir }
    $setupOut = Join-Path $repo "publish\setup"
    if (Test-Path $setupOut) { Remove-Item $setupOut -Recurse -Force }
    dotnet publish (Join-Path $repo "setup\HiveBDC.Setup\HiveBDC.Setup.csproj") `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:Version=$version "-p:InstallerPayloadDirectory=$payloadDir" `
        -o $setupOut
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish (setup) failed" }

    $setupExe = Join-Path $repo "publish\HiveBDC-Setup-v$version.exe"
    Copy-Item (Join-Path $setupOut "HiveBDC-Setup.exe") $setupExe -Force
    if (-not $SkipSign) {
        & $SignTool sign /v /fd sha256 /td sha256 /tr http://timestamp.sectigo.com `
            /d "Hive BDC, by Balsoft" `
            /f $cert /csp "Google Cloud KMS Provider" /kc $kmsKey `
            $setupExe
        if ($LASTEXITCODE -ne 0) { throw "signtool sign (setup) failed" }
        & $SignTool verify /pa /v $setupExe
        if ($LASTEXITCODE -ne 0) { throw "signature verification (setup) failed" }
    }
    Write-Host ("Setup: {0} ({1:N1} MB)" -f $setupExe, ((Get-Item $setupExe).Length / 1MB))

    # Update manifest for the dashboard's installer-based self-update ("Check for updates").
    # Publishing to the feed is a SEPARATE, explicit step (needs the storage account key —
    # never commit it): upload the Setup.exe FIRST, then latest.json, so the pointer is
    # never ahead of the file.
    if ((-not $SkipSign) -and (Test-Path $setupExe)) {
        $sha = (Get-FileHash $setupExe -Algorithm SHA256).Hash.ToLower()

        # changelog.json (app\...\Assets) is the single source of truth: copy it to the feed
        # staging dir and derive this version's release notes from it.
        $changelogSrc  = Join-Path $repo "app\HiveBDC.App\Assets\changelog.json"
        $changelogDst  = Join-Path $repo "publish\changelog.json"
        $notes = "Hive BDC $version"
        if (Test-Path $changelogSrc) {
            Copy-Item $changelogSrc $changelogDst -Force
            try {
                $entry = ((Get-Content $changelogSrc -Raw | ConvertFrom-Json).releases |
                          Where-Object { $_.version -eq $version } | Select-Object -First 1)
                if ($entry -and $entry.changes) { $notes = ($entry.changes -join "`n") }
            } catch { }
        }

        $manifest = [ordered]@{
            version     = $version
            url         = "https://hiveeinvoiceupdates.blob.core.windows.net/hive-bdc/HiveBDC-Setup-v$version.exe"
            sha256      = $sha
            releaseDate = (Get-Date).ToString('yyyy-MM-dd')
            notes       = $notes
        }
        $manifestPath = Join-Path $repo "publish\latest.json"
        ($manifest | ConvertTo-Json) | Set-Content -Path $manifestPath -Encoding utf8
        Write-Host ""
        Write-Host "Update manifest: $manifestPath  (notes from changelog.json)"
        Write-Host "Publish to the feed (needs the storage account key) — upload Setup + changelog FIRST, latest.json LAST:"
        Write-Host "  `$k = az storage account keys list -g rg-hive-einvoice -n hiveeinvoiceupdates --query [0].value -o tsv"
        Write-Host "  az storage blob upload -c hive-bdc --account-name hiveeinvoiceupdates --account-key `$k --overwrite -f `"$setupExe`" -n HiveBDC-Setup-v$version.exe"
        Write-Host "  az storage blob upload -c hive-bdc --account-name hiveeinvoiceupdates --account-key `$k --overwrite -f `"$changelogDst`" -n changelog.json --content-type application/json"
        Write-Host "  az storage blob upload -c hive-bdc --account-name hiveeinvoiceupdates --account-key `$k --overwrite -f `"$manifestPath`" -n latest.json --content-type application/json"
    }
}

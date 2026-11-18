# Signs the Azure self-update package and its feed entry with Balsoft's update key in Google Cloud KMS and completes
# azure/latest.json (design part 15b). Runs on the signing host after deploy/azure/package.sh; the key never leaves KMS.
# Instances check both signatures against the public half built into them, so before writing anything this script
# checks that the package carries the fingerprint of the KMS key and that both signatures verify with that key.
#
# Prerequisite: gcloud Application Default Credentials with roles/cloudkms.signer on the update key
#   (one-time: gcloud auth application-default login).
#
# Usage:
#   .\build\sign-azure-package.ps1 [-Dist deploy\azure\dist]
#
# Then upload to the feed: azure/hivebdc-app-{version}.zip first, azure/latest.json last.
param(
    [string]$Dist = (Join-Path $PSScriptRoot '..\deploy\azure\dist')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'azure-update-statement.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not $env:CLOUDSDK_AUTH_ACCESS_TOKEN) {
    $adcToken = (gcloud auth application-default print-access-token 2>$null)
    if ($adcToken) { $env:CLOUDSDK_AUTH_ACCESS_TOKEN = $adcToken }
}

# The update key sits beside the code-signing key; it signs update packages only.
$kms = @('--project', 'balsoft-codesigning', '--location', 'europe-west3', '--keyring', 'balsoft-codesigning', '--key', 'balsoft-update-rsa3072')
$kmsVersion = '1'

$manifestPath = Join-Path $Dist 'azure\latest.json'
if (-not (Test-Path $manifestPath)) { throw "No $manifestPath; run deploy/azure/package.sh with the release key first." }
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$zip = Join-Path $Dist ('azure\' + [IO.Path]::GetFileName(([Uri]$manifest.url).AbsolutePath))
if (-not (Test-Path $zip)) { throw "The package $zip that latest.json names is missing." }
if ((Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.sha256) { throw "$zip changed after package.sh wrote latest.json." }

$work = Join-Path ([IO.Path]::GetTempPath()) ('hivebdc-sign-' + [Guid]::NewGuid())
New-Item -ItemType Directory $work | Out-Null
try {
    # The KMS public key, and the fingerprint the package must carry: an instance updated to a package built with
    # another key could never check the next update.
    $pem = Join-Path $work 'kms.pem'
    & gcloud kms keys versions get-public-key $kmsVersion @kms --output-file $pem
    if ($LASTEXITCODE -ne 0) { throw 'gcloud kms keys versions get-public-key failed.' }
    $rsa = [System.Security.Cryptography.RSA]::Create()
    $rsa.ImportFromPem((Get-Content $pem -Raw))
    $fingerprint = ([BitConverter]::ToString([System.Security.Cryptography.SHA256]::HashData($rsa.ExportSubjectPublicKeyInfo())) -replace '-', '').ToLowerInvariant()
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $entry = $archive.GetEntry('hivebdc-package.json')
        if ($null -eq $entry) { throw "$zip has no hivebdc-package.json." }
        $reader = New-Object IO.StreamReader($entry.Open())
        $info = $reader.ReadToEnd() | ConvertFrom-Json
        $reader.Dispose()
    }
    finally { $archive.Dispose() }
    if ($info.keyFingerprint -ne $fingerprint) { throw "The package was built with another update key ($($info.keyFingerprint)) than KMS holds ($fingerprint)." }
    if ($info.version -ne $manifest.version) { throw "The package says $($info.version), latest.json $($manifest.version)." }

    function Sign([string]$file) {
        $signature = Join-Path $work ([Guid]::NewGuid().ToString() + '.sig')
        & gcloud kms asymmetric-sign @kms --version $kmsVersion --digest-algorithm sha256 --input-file $file --signature-file $signature
        if ($LASTEXITCODE -ne 0) { throw 'gcloud kms asymmetric-sign failed.' }
        return [Convert]::ToBase64String([IO.File]::ReadAllBytes($signature))
    }
    function Verify([byte[]]$data, [string]$signature) {
        return $rsa.VerifyData($data, [Convert]::FromBase64String($signature),
            [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    }

    $manifest.signature = Sign $zip
    $statementFile = Join-Path $work 'statement.txt'
    [IO.File]::WriteAllText($statementFile, (Get-UpdateStatement $manifest), (New-Object Text.UTF8Encoding($false)))
    $manifest.entrySignature = Sign $statementFile

    if (-not (Verify ([IO.File]::ReadAllBytes($zip)) $manifest.signature)) { throw 'The package signature does not verify with the KMS key; nothing was written.' }
    if (-not (Verify ([IO.File]::ReadAllBytes($statementFile)) $manifest.entrySignature)) { throw 'The entry signature does not verify with the KMS key; nothing was written.' }
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

$manifest | ConvertTo-Json -Depth 5 | Set-Content $manifestPath -Encoding utf8NoBOM
Write-Host "Signed $([IO.Path]::GetFileName($zip)) and its entry; both signatures are in $manifestPath."
Write-Host 'Upload the ZIP to <feed>/azure/ first, then latest.json last.'
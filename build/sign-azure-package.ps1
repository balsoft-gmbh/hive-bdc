# Signs the Azure self-update package and its feed entry with Balsoft's update key in Google Cloud KMS and completes
# azure/latest.json (design part 15b). Runs on the signing host after deploy/azure/package.sh; the key never leaves KMS.
# Instances check both signatures against the public half built into them, so before writing anything this script
# checks that the package carries the fingerprint of the KMS key and that both signatures verify with that key.
#
# Credentials: the signing host's service account key file named in GOOGLE_APPLICATION_CREDENTIALS (as
# tools/bdc-release/build.ps1 sets it), with roles/cloudkms.signerVerifier on the update key; elsewhere gcloud
# Application Default Credentials. The script talks to the KMS REST API itself, so the host needs no gcloud.
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

# The update key sits beside the code-signing key; it signs update packages only.
$keyVersion = 'https://cloudkms.googleapis.com/v1/projects/balsoft-codesigning/locations/europe-west3/keyRings/balsoft-codesigning/cryptoKeys/balsoft-update-rsa3072/cryptoKeyVersions/1'

function ConvertTo-Base64Url([byte[]]$Bytes) { [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') }

# An OAuth token for KMS: from the service account key file (JWT bearer grant), else from gcloud ADC.
function Get-KmsToken {
    $file = $env:GOOGLE_APPLICATION_CREDENTIALS
    if ($file -and (Test-Path $file)) {
        $account = Get-Content $file -Raw | ConvertFrom-Json
        if ($account.type -eq 'service_account') {
            $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
            $header = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes('{"alg":"RS256","typ":"JWT"}'))
            $claims = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes((@{
                iss = $account.client_email; scope = 'https://www.googleapis.com/auth/cloudkms'; aud = $account.token_uri; iat = $now; exp = $now + 600
            } | ConvertTo-Json -Compress)))
            $rsa = [System.Security.Cryptography.RSA]::Create()
            try {
                $rsa.ImportFromPem($account.private_key)
                $signature = ConvertTo-Base64Url ($rsa.SignData([Text.Encoding]::ASCII.GetBytes("$header.$claims"),
                    [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1))
            }
            finally { $rsa.Dispose() }
            $answer = Invoke-RestMethod -Method Post -Uri $account.token_uri -Body @{ grant_type = 'urn:ietf:params:oauth:grant-type:jwt-bearer'; assertion = "$header.$claims.$signature" }
            return $answer.access_token
        }
    }
    $token = (gcloud auth application-default print-access-token 2>$null)
    if (-not $token) { throw 'No KMS credentials: set GOOGLE_APPLICATION_CREDENTIALS to the signer key file, or run gcloud auth application-default login.' }
    return $token
}

$manifestPath = Join-Path $Dist 'azure\latest.json'
if (-not (Test-Path $manifestPath)) { throw "No $manifestPath; run deploy/azure/package.sh with the release key first." }
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$zip = Join-Path $Dist ('azure\' + [IO.Path]::GetFileName(([Uri]$manifest.url).AbsolutePath))
if (-not (Test-Path $zip)) { throw "The package $zip that latest.json names is missing." }
if ((Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.sha256) { throw "$zip changed after package.sh wrote latest.json." }

$headers = @{ Authorization = 'Bearer ' + (Get-KmsToken) }

# The KMS public key, and the fingerprint the package must carry: an instance updated to a package built with
# another key could never check the next update.
$rsa = [System.Security.Cryptography.RSA]::Create()
$rsa.ImportFromPem((Invoke-RestMethod -Headers $headers -Uri "$keyVersion/publicKey").pem)
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

function Sign([byte[]]$Data) {
    $digest = [Convert]::ToBase64String([System.Security.Cryptography.SHA256]::HashData($Data))
    $body = @{ digest = @{ sha256 = $digest } } | ConvertTo-Json -Compress
    return (Invoke-RestMethod -Method Post -Headers $headers -ContentType 'application/json' -Uri "${keyVersion}:asymmetricSign" -Body $body).signature
}
function Test-Signature([byte[]]$Data, [string]$Signature) {
    return $rsa.VerifyData($Data, [Convert]::FromBase64String($Signature),
        [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
}

$package = [IO.File]::ReadAllBytes($zip)
$statement = (New-Object Text.UTF8Encoding($false)).GetBytes((Get-UpdateStatement $manifest))
$manifest.signature = Sign $package
$manifest.entrySignature = Sign $statement
if (-not (Test-Signature $package $manifest.signature)) { throw 'The package signature does not verify with the KMS key; nothing was written.' }
if (-not (Test-Signature $statement $manifest.entrySignature)) { throw 'The entry signature does not verify with the KMS key; nothing was written.' }
$rsa.Dispose()

$manifest | ConvertTo-Json -Depth 5 | Set-Content $manifestPath -Encoding utf8NoBOM
Write-Host "Signed $([IO.Path]::GetFileName($zip)) and its entry; both signatures are in $manifestPath."
Write-Host 'Upload the ZIP to <feed>/azure/ first, then latest.json last.'
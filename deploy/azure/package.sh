#!/usr/bin/env bash
# Builds the Partner Center package of the Azure Application offer: mainTemplate.json, createUiDefinition.json and
# app/hivebdc.zip, the service published framework-dependent for App Service's .NET stack. Runs test-template.sh
# first, so a stale or failing template never gets packaged. It also writes the self-update package and its feed entry
# (design part 15b): azure/hivebdc-app-{version}.zip and azure/latest.json without signature, which
# build/sign-azure-package.ps1 adds on the signing host.
#   deploy/azure/package.sh [output folder]        default: deploy/azure/dist
#   UPDATE_KEY_FILE=<pem>    a test key instead of Hosting/update-key.pem; the files then end in -test, never for a release
#   UPDATE_FEED_URL=<url>    the feed the entry points at (default: Balsoft's public feed)
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
out="${1:-$here/dist}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }
for tool in dotnet jq zip unzip; do command -v "$tool" >/dev/null 2>&1 || fail "$tool not found"; done

version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/Balsoft.Hive.BDC.csproj" | head -1)"
[ -n "$version" ] || fail "no <Version> in Balsoft.Hive.BDC.csproj"

framework="$(sed -n 's:.*<TargetFramework>net\(.*\)</TargetFramework>.*:\1:p' "$root/Balsoft.Hive.BDC.csproj" | head -1)"
runtime="DOTNETCORE|$framework"
key="${UPDATE_KEY_FILE:-$root/Hosting/update-key.pem}"
suffix=""; [ -n "${UPDATE_KEY_FILE:-}" ] && suffix="-test"
[ -f "$key" ] || fail "no update key at $key: a package without Balsoft's update key never updates itself (make it on the signing host)"
feed="${UPDATE_FEED_URL:-https://hiveeinvoiceupdates.blob.core.windows.net/hive-bdc}"
feed="${feed%%\?*}"   # a mirror's SAS stays in the instance's app setting, never in the entry
fingerprint="$(openssl pkey -pubin -in "$key" -outform DER | shasum -a 256 | cut -d' ' -f1)"

bash "$here/test-template.sh"

echo "== Publish Hive BDC $version (framework-dependent)${suffix:+, TEST update key}"
dotnet publish "$root/Balsoft.Hive.BDC.csproj" -c Release -o "$work/app" -p:UseAppHost=false -p:UpdateKeyFile="$key" --nologo
rm -f "$work/app/install.bat" "$work/app/uninstall.bat"
jq -n --arg version "$version" --arg runtime "$runtime" --arg fingerprint "$fingerprint" \
  '{version: $version, runtime: $runtime, keyFingerprint: $fingerprint}' > "$work/app/hivebdc-package.json"

# Failure case 49: the start command is "dotnet HiveBDC.dll" from the root of the mounted package.
[ -f "$work/app/HiveBDC.dll" ] || fail "HiveBDC.dll is missing from the publish output"
runtimeconfig="$work/app/HiveBDC.runtimeconfig.json"
jq -e '.runtimeOptions.includedFrameworks == null' "$runtimeconfig" >/dev/null || fail "the publish is self-contained"
jq -e '[.runtimeOptions.framework, (.runtimeOptions.frameworks // [])[]] | map(select(. != null) | .name) | index("Microsoft.AspNetCore.App") != null' \
  "$runtimeconfig" >/dev/null || fail "HiveBDC.runtimeconfig.json does not name Microsoft.AspNetCore.App"
[ -f "$work/app/runtimes/linux-x64/native/libe_sqlite3.so" ] || fail "the SQLite library for linux-x64 is missing"

mkdir -p "$work/package/app"
(cd "$work/app" && zip -qrX "$work/package/app/hivebdc.zip" .)
entries="$(unzip -Z1 "$work/package/app/hivebdc.zip")"   # captured first: grep -q under pipefail would kill unzip
grep -qx 'HiveBDC.dll' <<<"$entries" || fail "HiveBDC.dll is not at the root of hivebdc.zip"
grep -qx 'hivebdc-package.json' <<<"$entries" || fail "hivebdc-package.json is not at the root of hivebdc.zip"

cp "$here/mainTemplate.json" "$here/createUiDefinition.json" "$work/package/"
mkdir -p "$out/azure"
app="hivebdc-app-$version$suffix.zip"
cp "$work/package/app/hivebdc.zip" "$out/azure/$app"
changelog="$root/app/HiveBDC.App/Assets/changelog.json"
[ -n "$suffix" ] || jq -e --arg v "$version" '[.releases[] | select(.version == $v) | .changes[]] | length > 0' "$changelog" >/dev/null \
  || fail "changelog.json has no changes for $version; the update notice shows them"
jq -n --arg version "$version" --arg url "$feed/azure/$app" --argjson size "$(wc -c <"$out/azure/$app" | tr -d ' ')" \
  --arg sha256 "$(shasum -a 256 "$out/azure/$app" | cut -d' ' -f1)" --arg runtime "$runtime" \
  --argjson notes "$(jq --arg v "$version" '[.releases[] | select(.version == $v) | .changes[]]' "$changelog")" \
  --arg date "$(jq -r --arg v "$version" '[.releases[] | select(.version == $v) | .date][0] // ""' "$changelog")" \
  '{version: $version, url: $url, size: $size, sha256: $sha256, signature: "", entrySignature: "", runtime: $runtime, notes: $notes, releaseDate: $date}' \
  > "$out/azure/latest$suffix.json"
package="$(cd "$out" && pwd)/hivebdc-azure-$version$suffix.zip"
rm -f "$package"
(cd "$work/package" && zip -qrXD "$package" mainTemplate.json createUiDefinition.json app)

root_entries="$(unzip -Z1 "$package" | sort | tr '\n' ' ')"
[ "$root_entries" = "app/hivebdc.zip createUiDefinition.json mainTemplate.json " ] \
  || fail "the package root must hold exactly mainTemplate.json, createUiDefinition.json and app/hivebdc.zip"
echo "== $package"
echo "   $(du -h "$package" | cut -f1)  sha256 $(shasum -a 256 "$package" | cut -d' ' -f1)"
echo "== $out/azure/$app and latest$suffix.json (unsigned: run build/sign-azure-package.ps1 on the signing host)"
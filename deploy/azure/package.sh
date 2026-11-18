#!/usr/bin/env bash
# Builds the Partner Center package of the Azure Application offer: mainTemplate.json, createUiDefinition.json and
# app/hivebdc.zip, the service published framework-dependent for App Service's .NET stack. Runs test-template.sh
# first, so a stale or failing template never gets packaged.
#   deploy/azure/package.sh [output folder]        default: deploy/azure/dist
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

bash "$here/test-template.sh"

echo "== Publish Hive BDC $version (framework-dependent)"
dotnet publish "$root/Balsoft.Hive.BDC.csproj" -c Release -o "$work/app" -p:UseAppHost=false --nologo
rm -f "$work/app/install.bat" "$work/app/uninstall.bat"

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

cp "$here/mainTemplate.json" "$here/createUiDefinition.json" "$work/package/"
mkdir -p "$out"
package="$(cd "$out" && pwd)/hivebdc-azure-$version.zip"
rm -f "$package"
(cd "$work/package" && zip -qrXD "$package" mainTemplate.json createUiDefinition.json app)

root_entries="$(unzip -Z1 "$package" | sort | tr '\n' ' ')"
[ "$root_entries" = "app/hivebdc.zip createUiDefinition.json mainTemplate.json " ] \
  || fail "the package root must hold exactly mainTemplate.json, createUiDefinition.json and app/hivebdc.zip"
echo "== $package"
echo "   $(du -h "$package" | cut -f1)  sha256 $(shasum -a 256 "$package" | cut -d' ' -f1)"
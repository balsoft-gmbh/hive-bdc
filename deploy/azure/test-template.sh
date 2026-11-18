#!/usr/bin/env bash
# Checks the Azure deployment package: the Bicep build matches the committed mainTemplate.json, the Bicep linter
# reports nothing, Microsoft's ARM template test toolkit passes on mainTemplate.json and createUiDefinition.json,
# the sign-in script parses, and DeploymentTemplateTests pass.
#   ARM_TTK  path to arm-ttk.psd1 (default ~/Repos/microsoft/arm-ttk/arm-ttk/arm-ttk.psd1)
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
armttk="${ARM_TTK:-$HOME/Repos/microsoft/arm-ttk/arm-ttk/arm-ttk.psd1}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }

if command -v bicep >/dev/null 2>&1; then bicep=bicep
elif [ -x "$HOME/.azure/bin/bicep" ]; then bicep="$HOME/.azure/bin/bicep"
else fail "Bicep CLI not found (az bicep install puts it in ~/.azure/bin)"; fi
for tool in jq pwsh dotnet; do command -v "$tool" >/dev/null 2>&1 || fail "$tool not found"; done
[ -f "$armttk" ] || fail "arm-ttk not found at $armttk (set ARM_TTK)"

echo "== 1/5 Bicep build matches mainTemplate.json"
"$bicep" build "$here/main.bicep" --outfile "$work/mainTemplate.json"
if ! diff <(jq -S 'del(.metadata._generator)' "$here/mainTemplate.json") \
          <(jq -S 'del(.metadata._generator)' "$work/mainTemplate.json") >/dev/null; then
  fail "mainTemplate.json is not the build of main.bicep: run $bicep build deploy/azure/main.bicep --outfile deploy/azure/mainTemplate.json"
fi
echo "   ok"

echo "== 2/5 Bicep linter"
lint="$("$bicep" lint "$here/main.bicep" 2>&1)" || { echo "$lint"; fail "bicep lint"; }
[ -z "$lint" ] || { echo "$lint"; fail "bicep lint reported findings"; }
echo "   ok"

echo "== 3/5 ARM template test toolkit"
mkdir -p "$work/package"
cp "$here/mainTemplate.json" "$here/createUiDefinition.json" "$work/package/"
ARMTTK="$armttk" PACKAGE="$work/package" pwsh -NoProfile -NonInteractive -Command '
  $ErrorActionPreference = "Stop"
  Import-Module $env:ARMTTK
  $results = @(Test-AzTemplate -TemplatePath $env:PACKAGE)
  $failed = @($results | Where-Object { -not $_.Passed })
  foreach ($r in $failed) {
    $why = @(@($r.Errors) + @($r.Warnings) | Where-Object { $_ } | ForEach-Object { $_.Exception.Message }) -join "; "
    Write-Host ("   FAILED {0}: {1}: {2}" -f $r.File.Name, $r.Name, $why)
  }
  Write-Host ("   {0} checks, {1} failed" -f $results.Count, $failed.Count)
  if ($results.Count -eq 0 -or $failed.Count -gt 0) { exit 1 }
' || fail "arm-ttk"

echo "== 4/5 Sign-in script parses"
SCRIPT="$root/scripts/New-HiveBDCSignInApp.ps1" pwsh -NoProfile -NonInteractive -Command '
  $errors = $null
  $null = [System.Management.Automation.Language.Parser]::ParseFile($env:SCRIPT, [ref]$null, [ref]$errors)
  foreach ($e in $errors) { Write-Host ("   {0} line {1}: {2}" -f $env:SCRIPT, $e.Extent.StartLineNumber, $e.Message) }
  if ($errors.Count -gt 0) { exit 1 }
' || fail "New-HiveBDCSignInApp.ps1 does not parse"
echo "   ok"

echo "== 5/5 DeploymentTemplateTests"
dotnet test "$root/tests/Balsoft.Hive.BDC.Tests/Balsoft.Hive.BDC.Tests.csproj" --nologo \
  --filter "FullyQualifiedName~DeploymentTemplateTests" || fail "DeploymentTemplateTests"

echo "PASS: deployment package checks"
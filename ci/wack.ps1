# Microsoft's Windows App Certification Kit (WACK) on the bundle, as Partner Center runs it before Microsoft's own
# review: a copy signed for this run only (ci/sign-test.ps1), the full test, its report as XML and a short summary.
#
#   ci/wack.ps1 <bundle> <out folder>
param([Parameter(Mandatory = $true)][string]$Bundle, [Parameter(Mandatory = $true)][string]$Out)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$copy = Join-Path $Out 'signed-for-wack.msixbundle'
Copy-Item $Bundle $copy
& (Join-Path $PSScriptRoot 'sign-test.ps1') $copy

$appcert = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe' -ErrorAction SilentlyContinue
if (-not $appcert) { throw 'the Windows App Certification Kit (appcert.exe) is not on this machine' }
Write-Host "WACK: $($appcert.FullName) $((Get-Item $appcert.FullName).VersionInfo.FileVersion)"
$report = Join-Path $Out 'wack-report.xml'
& $appcert.FullName reset | Out-Host
$sw = [Diagnostics.Stopwatch]::StartNew()
& $appcert.FullName test -appxpackagepath $copy -reportoutputpath $report | Out-Host
Write-Host "WACK took $([int]$sw.Elapsed.TotalMinutes) min, exit $LASTEXITCODE"
Remove-Item $copy
if (-not (Test-Path $report)) { throw 'WACK wrote no report' }

# the summary: every test with its result, failures and warnings with what WACK says
[xml]$x = Get-Content $report
$overall = $x.REPORT.OVERALL_RESULT
$lines = @("# Windows App Certification Kit", "", "Overall: **$overall**", "")
foreach ($req in $x.REPORT.REQUIREMENTS.REQUIREMENT) {
  foreach ($t in $req.TEST) {
    $lines += "- $($t.RESULT.'#cdata-section' ?? $t.RESULT) — $($t.NAME)"
    foreach ($m in $t.MESSAGES.MESSAGE) { if ($m.TEXT) { $lines += "  - $($m.TEXT)" } }
  }
}
$lines | Set-Content (Join-Path $Out 'wack-summary.md') -Encoding utf8
$lines | Out-String | Add-Content $env:GITHUB_STEP_SUMMARY
if ($overall -ne 'PASS') { Write-Host "WACK overall: $overall"; exit 1 }

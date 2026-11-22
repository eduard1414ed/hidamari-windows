# Makes sure the WebView2 runtime is on the GitHub machine (Windows 11 has it built in; Windows Server may not).
$keys = @(
  'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
  'HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
  'HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
)
foreach ($k in $keys) {
  $v = (Get-ItemProperty $k -ErrorAction SilentlyContinue).pv
  if ($v -and $v -ne '0.0.0.0') { Write-Host "WebView2 runtime $v"; exit 0 }
}
Write-Host 'WebView2 runtime missing, installing the evergreen runtime'
$setup = Join-Path $env:RUNNER_TEMP 'MicrosoftEdgeWebview2Setup.exe'
Invoke-WebRequest 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $setup
Start-Process $setup -ArgumentList '/silent', '/install' -Wait
foreach ($k in $keys) {
  $v = (Get-ItemProperty $k -ErrorAction SilentlyContinue).pv
  if ($v -and $v -ne '0.0.0.0') { Write-Host "WebView2 runtime $v"; exit 0 }
}
Write-Error 'WebView2 runtime could not be installed'
exit 1

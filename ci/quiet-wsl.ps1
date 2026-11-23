# GitHub's Windows 11 machine (windows-11-arm) has wsl.exe without the Linux part. The machine's own agent calls it
# every 30 seconds, and each call opens a console over the desktop: "Windows Subsystem for Linux must be updated…
# Press any key to install" (wsl.exe --update --confirm --prompt-before-exit). It takes the keyboard from the card and
# covers the wall, so the checks fail for a reason that has nothing to do with Hidamari.
# This machine is thrown away after the run and nothing on it uses Linux: wsl.exe is pointed at a program that does
# nothing and ends at once (Windows' "Image File Execution Options", the documented way to start another program in
# a program's place). A call then fails quietly, as if WSL were not there at all. The checks still close the console
# if it shows up anyway (Check/Desk.cs).
$ErrorActionPreference = 'Stop'
if (-not (Test-Path "$env:windir\System32\wsl.exe")) { Write-Host 'no wsl.exe here, nothing to do'; exit 0 }

$exe = 'C:\hidamari-ci\nothing.exe'
New-Item -ItemType Directory -Force (Split-Path $exe) | Out-Null
# a window program (no console of its own) that ends at once with "failed"; Windows PowerShell 5.1 can build one
$code = 'public static class Nothing { public static int Main(string[] a) { return 1; } }'
powershell.exe -NoProfile -Command "Add-Type -TypeDefinition '$code' -OutputAssembly '$exe' -OutputType WindowsApplication"
if (-not (Test-Path $exe)) { throw "could not build $exe" }

foreach ($root in 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options',
                  'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Image File Execution Options') {
    $k = Join-Path $root 'wsl.exe'
    New-Item -Force $k | Out-Null
    Set-ItemProperty $k -Name Debugger -Value $exe
}
Get-Process wsl -ErrorAction SilentlyContinue | Stop-Process -Force

# the proof: a call to wsl.exe now ends at once, and no wsl.exe is left running
$p = Start-Process "$env:windir\System32\wsl.exe" -ArgumentList '--status' -PassThru -WindowStyle Hidden
if (-not $p.WaitForExit(10000)) { throw 'wsl.exe still runs after 10 s' }
Start-Sleep 1
$left = @(Get-Process wsl -ErrorAction SilentlyContinue).Count
Write-Host "wsl.exe now starts $exe instead: ended with $($p.ExitCode), wsl processes left: $left"
if ($left -gt 0) { throw 'wsl.exe still running' }

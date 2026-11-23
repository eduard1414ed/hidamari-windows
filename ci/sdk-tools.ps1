# The folder with MakeAppx, MakePri and SignTool for this machine's processor, from Microsoft's NuGet package
# Microsoft.Windows.SDK.BuildTools (downloaded once into the runner's temp folder). Prints the folder.
$ErrorActionPreference = 'Stop'
$version = '10.0.28000.2705'
$base = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$dir = Join-Path $base "sdk-buildtools-$version"
if (-not (Test-Path (Join-Path $dir 'bin'))) {
  $zip = "$dir.zip"
  Invoke-WebRequest "https://www.nuget.org/api/v2/package/Microsoft.Windows.SDK.BuildTools/$version" -OutFile $zip
  Expand-Archive $zip $dir -Force
  Remove-Item $zip
}
$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$bin = Get-ChildItem (Join-Path $dir 'bin') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$tools = Join-Path $bin.FullName $arch
if (-not (Test-Path (Join-Path $tools 'makeappx.exe'))) { throw "no makeappx.exe in $tools" }
$tools

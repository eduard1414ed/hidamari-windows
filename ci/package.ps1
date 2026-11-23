# Builds the MSIX packages of Hidamari for the Microsoft Store: one for x64, one for arm64 (its own build, not
# emulation), and the bundle of both, Hidamari_<version>.msixbundle, which is what goes to Partner Center.
# Not signed: the Store signs it. The checks sign their own copy with a certificate made for the run (ci/sign-test.ps1).
#
#   ci/package.ps1 -Version 1.0.0 -Out out/package
#
# The tools (MakeAppx, MakePri, SignTool) come from Microsoft's NuGet package Microsoft.Windows.SDK.BuildTools, the
# same on every machine; nothing is installed into Windows.
param(
  [string]$Version = '1.0.0',
  [string]$Out = 'out/package',
  [string[]]$Arch = @('x64', 'arm64')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$Out = [IO.Path]::GetFullPath((Join-Path (Get-Location) $Out))
New-Item -ItemType Directory -Force $Out | Out-Null
$tools = & (Join-Path $PSScriptRoot 'sdk-tools.ps1')
$makeappx = Join-Path $tools 'makeappx.exe'
$makepri = Join-Path $tools 'makepri.exe'

# the package's version has four numbers and the Store wants the last one 0
$parts = $Version.Split('.')
while ($parts.Count -lt 3) { $parts += '0' }
$pkgVersion = "$($parts[0]).$($parts[1]).$($parts[2]).0"
Write-Host "Hidamari $Version, package $pkgVersion, for $($Arch -join ', ')"

$packages = @()
foreach ($a in $Arch) {
  $layout = Join-Path $Out "layout-$a"
  if (Test-Path $layout) { Remove-Item -Recurse -Force $layout }
  # the app as files, not one self-extracting file: in a package Windows keeps them as they are
  dotnet publish (Join-Path $root 'Hidamari') -c Release -r "win-$a" -o $layout `
    -p:Version=$Version -p:PublishSingleFile=false -p:SkipStringsCheck=true -p:DebugType=none | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish win-$a failed" }
  Copy-Item -Recurse (Join-Path $root 'Package/Images') (Join-Path $layout 'Images')
  (Get-Content (Join-Path $root 'Package/AppxManifest.xml') -Raw) `
    -replace '\$VERSION\$', $pkgVersion -replace '\$ARCH\$', $a |
    Set-Content (Join-Path $layout 'AppxManifest.xml') -Encoding utf8

  # resources.pri: how Windows picks Images\Square44x44Logo.targetsize-24.png and the like for each screen
  $priConfig = Join-Path $Out "priconfig-$a.xml"
  & $makepri createconfig /cf $priConfig /dq en-US /pv 10.0.0 /o | Out-Host
  if ($LASTEXITCODE -ne 0) { throw 'makepri createconfig failed' }
  Push-Location $layout
  & $makepri new /pr $layout /cf $priConfig /mn (Join-Path $layout 'AppxManifest.xml') /of (Join-Path $layout 'resources.pri') /o | Out-Host
  $code = $LASTEXITCODE
  Pop-Location
  if ($code -ne 0) { throw 'makepri new failed' }

  $msix = Join-Path $Out "Hidamari_$($pkgVersion)_$a.msix"
  & $makeappx pack /d $layout /p $msix /o | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "makeappx pack $a failed" }
  $packages += $msix
  $files = (Get-ChildItem -Recurse -File $layout).Count
  Write-Host ("{0}: {1} files, {2:N1} MB" -f (Split-Path $msix -Leaf), $files, ((Get-Item $msix).Length / 1MB))
}

$bundleDir = Join-Path $Out 'bundle-in'
if (Test-Path $bundleDir) { Remove-Item -Recurse -Force $bundleDir }
New-Item -ItemType Directory $bundleDir | Out-Null
$packages | ForEach-Object { Copy-Item $_ $bundleDir }
$bundle = Join-Path $Out "Hidamari_$pkgVersion.msixbundle"
& $makeappx bundle /d $bundleDir /p $bundle /bv $pkgVersion /o | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'makeappx bundle failed' }
Remove-Item -Recurse -Force $bundleDir
Write-Host ("{0}: {1:N1} MB" -f (Split-Path $bundle -Leaf), ((Get-Item $bundle).Length / 1MB))
$bundle

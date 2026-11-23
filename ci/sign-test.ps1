# For the checks on GitHub only: signs copies of the packages with a certificate made for this run, so that this
# machine installs them as Windows would install the Store's. The certificate has the Store identity's publisher name
# (the package must name its signer), is trusted only on this machine, and its private key never leaves it: it is made
# here, used, and deleted. The Store's own packages are signed by Microsoft; this one is never uploaded.
#
#   ci/sign-test.ps1 <package or bundle> [...]
param([Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)][string[]]$Files)
$ErrorActionPreference = 'Stop'
$tools = & (Join-Path $PSScriptRoot 'sdk-tools.ps1')
$signtool = Join-Path $tools 'signtool.exe'
$manifest = Get-Content (Join-Path (Split-Path $PSScriptRoot -Parent) 'Package/AppxManifest.xml') -Raw
$publisher = [regex]::Match($manifest, 'Publisher="([^"]+)"').Groups[1].Value

$cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature `
  -FriendlyName 'Hidamari check (this run only)' -CertStoreLocation 'Cert:\CurrentUser\My' `
  -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') -NotAfter (Get-Date).AddDays(2)
try {
  # the public half, so that Windows trusts packages signed by it (this machine only)
  $cer = Join-Path $env:RUNNER_TEMP 'hidamari-check.cer'
  Export-Certificate -Cert $cert -FilePath $cer | Out-Null
  Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
  Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
  Remove-Item $cer
  foreach ($f in $Files) {
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint /s My $f | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "signtool failed on $f" }
    Write-Host "signed for this run: $f"
  }
}
finally {
  # the private key goes; the trusted public half stays until the machine is thrown away at the end of the run
  Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -DeleteKey -ErrorAction SilentlyContinue
}

# Downloads Wintun, the virtual network adapter awdld uses to hand AWDL traffic
# to Windows, and puts wintun.dll where the build picks it up. The archive is
# checked against the hash published on wintun.net and the DLL against its
# WireGuard LLC signature before anything is kept.
$ErrorActionPreference = 'Stop'

$Version = '0.14.1'
$Url     = "https://www.wintun.net/builds/wintun-$Version.zip"
$Sha256  = '07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51'
$Signer  = 'WireGuard LLC'

$repo   = Split-Path $PSScriptRoot -Parent
$target = "$repo\deps\wintun"
$work   = Join-Path $env:TEMP "winawdl-wintun-$([guid]::NewGuid())"
New-Item -ItemType Directory $work | Out-Null
try {
  $zip = "$work\wintun.zip"
  Invoke-WebRequest $Url -OutFile $zip -UseBasicParsing
  $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
  if ($hash -ne $Sha256) { throw "Unexpected SHA-256 for ${Url}: $hash" }

  Expand-Archive $zip -DestinationPath $work
  $dll = "$work\wintun\bin\amd64\wintun.dll"
  $signature = Get-AuthenticodeSignature $dll
  if ($signature.Status -ne 'Valid' -or
      $signature.SignerCertificate.Subject -notmatch "O=$Signer") {
    throw "wintun.dll is not validly signed by ${Signer}: $($signature.Status)"
  }

  New-Item -ItemType Directory $target -Force | Out-Null
  Copy-Item $dll, "$work\wintun\LICENSE.txt" $target -Force
} finally {
  Remove-Item $work -Recurse -Force
}

# An existing build gets the DLL right away; later builds copy it themselves.
if (Test-Path "$repo\build\awdl") { Copy-Item "$target\wintun.dll" "$repo\build\awdl" -Force }
"Wintun $Version is in deps\wintun."

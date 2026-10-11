# Compiles the user-mode tools and probes into tools\build.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\scripts\toolchain.ps1"

$tools = Initialize-Toolchain
$out = Join-Path $PSScriptRoot 'build'
New-Item -ItemType Directory -Force $out | Out-Null

$client = "$PSScriptRoot\regproxy_client.c"
$targets = @(
  @{ Sources = @("$PSScriptRoot\regproxy_test.c", $client); Libs = @('setupapi.lib') },
  @{ Sources = @("$PSScriptRoot\regdump.c", $client);       Libs = @('setupapi.lib') },
  @{ Sources = @("$PSScriptRoot\..\probes\awe_probe.c");    Libs = @('advapi32.lib') }
)

Push-Location $out
try {
  foreach ($target in $targets) {
    $name = [IO.Path]::GetFileNameWithoutExtension($target.Sources[0])
    & $tools.Cl /nologo /W4 /WX /O2 /MT @($target.Sources) /link "/OUT:$out\$name.exe" @($target.Libs)
    if ($LASTEXITCODE -ne 0) { throw "$name failed to compile ($LASTEXITCODE)" }
  }
} finally { Pop-Location }

Get-ChildItem $out -Filter *.exe | Select-Object Name, Length

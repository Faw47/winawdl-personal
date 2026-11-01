# Compiles winawdl-menu.dll, the packaged Explorer command for Windows 11's own
# menu, into build\menu. Needs MSVC and the Windows SDK (scripts\toolchain.ps1).
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\..\scripts\toolchain.ps1"

$tools = Initialize-Toolchain
$out = Join-Path $PSScriptRoot '..\..\build\menu'
New-Item -ItemType Directory -Force $out | Out-Null
$out = (Resolve-Path $out).Path

Push-Location $out
try {
  & $tools.Cl /nologo /W4 /WX /O2 /GS /guard:cf /Zi /MT /EHsc /std:c++17 /permissive- `
      /DUNICODE /D_UNICODE "/Fd$out\winawdl-menu.pdb" "$PSScriptRoot\menu.cpp" `
      /link /DLL "/OUT:$out\winawdl-menu.dll" /GUARD:CF /DEBUG /INCREMENTAL:NO /OPT:REF /OPT:ICF `
      '/PDBALTPATH:%_PDB%' /EXPORT:DllGetClassObject,PRIVATE /EXPORT:DllCanUnloadNow,PRIVATE `
      shlwapi.lib ole32.lib advapi32.lib shell32.lib
  if ($LASTEXITCODE -ne 0) { throw "menu.cpp failed to compile ($LASTEXITCODE)" }
} finally { Pop-Location }

Get-Item "$out\winawdl-menu.dll" | Select-Object Name, Length

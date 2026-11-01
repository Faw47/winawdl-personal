# Locates MSVC and the Windows kit, sets INCLUDE and LIB, and returns tool paths.
# vcvars64.bat is not used because it fails in non-interactive shells.
function Initialize-Toolchain {
  param([string]$UmdfVersion)

  $vsRoot = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools'
  $msvc = Get-ChildItem "$vsRoot\VC\Tools\MSVC" -Directory |
          Sort-Object Name | Select-Object -Last 1
  if (-not $msvc) { throw "MSVC toolset not found under $vsRoot" }

  $kit = 'C:\Program Files (x86)\Windows Kits\10'
  $kitVersion = Get-ChildItem "$kit\Include" -Directory |
                Where-Object { Test-Path "$($_.FullName)\um\windows.h" } |
                Sort-Object Name | Select-Object -Last 1 -ExpandProperty Name
  if (-not $kitVersion) { throw "Windows SDK headers not found under $kit" }

  $include = @("$($msvc.FullName)\include", "$kit\Include\$kitVersion\ucrt",
               "$kit\Include\$kitVersion\um", "$kit\Include\$kitVersion\shared")
  $lib = @("$($msvc.FullName)\lib\x64", "$kit\Lib\$kitVersion\ucrt\x64",
           "$kit\Lib\$kitVersion\um\x64")

  if ($UmdfVersion) {
    $umdfInclude = "$kit\Include\wdf\umdf\$UmdfVersion"
    if (-not (Test-Path $umdfInclude)) {
      throw "UMDF $UmdfVersion headers not found. Is the Windows Driver Kit installed?"
    }
    $include += $umdfInclude
    $lib += "$kit\Lib\wdf\umdf\x64\$UmdfVersion"
  }

  $env:INCLUDE = $include -join ';'
  $env:LIB = $lib -join ';'

  @{
    Cl       = "$($msvc.FullName)\bin\Hostx64\x64\cl.exe"
    InfVerif = "$kit\Tools\$kitVersion\x64\infverif.exe"
    DevCon   = "$kit\Tools\$kitVersion\x64\devcon.exe"
    SignTool = "$kit\bin\$kitVersion\x64\signtool.exe"
    Inf2Cat  = "$kit\bin\$kitVersion\x86\Inf2Cat.exe"
    StampInf = "$kit\bin\$kitVersion\x64\stampinf.exe"
    MakeAppx = "$kit\bin\$kitVersion\x64\makeappx.exe"
  }
}

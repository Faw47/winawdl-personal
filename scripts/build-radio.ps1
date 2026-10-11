# Builds the radio-side programs and devourer (upstream\devourer) into build\radio.
# The devourer_demos target is devourer's rxdemo, txdemo and pcieprobe.
param([string[]]$Targets = @('winawdl_radio', 'awdl_sniff', 'pipe_server_test', 'frame_capture_test', 'dma_address_test', 'devourer_demos'))
$ErrorActionPreference = 'Stop'

$repo  = Split-Path $PSScriptRoot -Parent
$build = "$repo\build\radio"
if (-not (Test-Path "$repo\upstream\devourer\CMakeLists.txt")) {
  throw 'devourer sources missing: run "git submodule update --init".'
}

# The vcpkg that ships with the Build Tools only works from a manifest.
$env:VCPKG_ROOT = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\vcpkg'
if (-not (Test-Path "$env:VCPKG_ROOT\vcpkg.exe")) { throw "vcpkg not found at $env:VCPKG_ROOT" }

cmake -S "$repo\radio" -B $build -G 'Visual Studio 17 2022' -A x64 "-DVCPKG_MANIFEST_DIR=$repo\deps"
if ($LASTEXITCODE -ne 0) { throw "cmake configure failed ($LASTEXITCODE)" }

cmake --build $build --config Release --target @Targets -- /m /v:minimal
if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }

Get-ChildItem $build -Recurse -Filter *.exe |
  Where-Object { $_.DirectoryName -like '*\Release' } |
  Select-Object Name, Length, DirectoryName

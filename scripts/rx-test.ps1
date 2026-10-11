# Runs devourer's receive demo on the built-in card in monitor mode.
# Press Ctrl+C to stop; the demo then shuts the radio down cleanly.
param([int]$Channel = 6)

$rxdemo = "$PSScriptRoot\..\build\radio\devourer\Release\rxdemo.exe"
if (-not (Test-Path $rxdemo)) { throw 'Run scripts\build-radio.ps1 first.' }

# Any value selects the PCIe path; on Windows it only labels log lines.
$env:DEVOURER_PCIE_BDF = 'win'
$env:DEVOURER_CHANNEL = "$Channel"
& $rxdemo

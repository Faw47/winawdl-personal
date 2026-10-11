# Sends a burst of devourer's broadcast test frames, with the receive loop
# running so the chip's TX reports are counted. -PowerDbm lowers the power.
param([int]$Channel = 6, [int]$Frames = 500, [ValidateRange(0, 23)][int]$PowerDbm)

$txdemo = "$PSScriptRoot\..\build\radio\devourer\Release\txdemo.exe"
if (-not (Test-Path $txdemo)) { throw 'Run scripts\build-radio.ps1 first.' }

# Any value selects the PCIe path; on Windows it only labels log lines.
$env:DEVOURER_PCIE_BDF = 'win'
$env:DEVOURER_CHANNEL = "$Channel"
$env:DEVOURER_TX_FRAMES = "$Frames"
$env:DEVOURER_TX_WITH_RX = 'thread'
if ($PSBoundParameters.ContainsKey('PowerDbm')) { $env:DEVOURER_TX_PWR = "$PowerDbm" }
try { & $txdemo } finally { $env:DEVOURER_TX_PWR = $null }

# Starts the radio daemon on one AWDL channel, then the AWDL daemon on top of
# it, both in this console. Ctrl+C stops both: the radio daemon exits when its
# client leaves.
param([ValidateSet(6, 44, 149)][int]$Channel = 6, [switch]$Detail)
$ErrorActionPreference = 'Stop'

$repo  = Split-Path $PSScriptRoot -Parent
$radio = "$repo\build\radio\Release\winawdl_radio.exe"
$awdld = "$repo\build\awdl\awdld.exe"
if (-not (Test-Path $radio)) { throw 'Run scripts\build-radio.ps1 first.' }
if (-not (Test-Path $awdld)) { throw 'Run scripts\build-awdl.ps1 first.' }

# awdld waits for the radio daemon by itself. Probing the pipe from here would
# count as a client connecting and leaving.
$radioProcess = Start-Process $radio -ArgumentList $Channel -PassThru -NoNewWindow

$awdldArgs = @('-c', $Channel)
if ($Detail) { $awdldArgs += '-v' }
& $awdld @awdldArgs

if (-not $radioProcess.WaitForExit(5000)) { Stop-Process -Id $radioProcess.Id }

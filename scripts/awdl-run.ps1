# Starts the radio daemon on one AWDL channel, then the AWDL daemon on top of
# it. Ctrl+C stops both: the radio daemon exits when its client leaves.
# -Redact keeps the addresses and names of nearby devices out of the log.
# -Capture names a pcap file for the frames of the session; it stays local.
# -NoBlockAck makes senders go back to one frame per transmission.
# -Streams 1 and -LongGuard announce slower reception. -Join is how many
# microseconds the daemon waits for the next TCP segment of a burst before it
# hands Windows the joined ones; 0 hands each over as it comes.
# -Width 20 or 40 narrows a 5 GHz channel, which is 80 MHz unless told.
# -Retries is how often the card repeats a frame nobody acknowledged.
# -DataRate is the rate of data frames to a peer. "auto" starts each exchange
# at the AWDL daemon's 12 Mbit/s and goes faster while the peer acknowledges;
# "client" stays there; a rate in devourer's notation, such as MCS7 or
# VHT2SS_MCS7/80/SGI, is used for every such frame.
# -Aggregate asks a peer to take data frames in aggregates. Experimental.
param([ValidateSet(6, 44, 149)][int]$Channel = 44, [switch]$Detail, [switch]$Redact,
      [string]$Capture, [switch]$NoBlockAck, [ValidateSet(1, 2)][int]$Streams = 2,
      [switch]$LongGuard, [ValidateRange(0, 3000)][int]$Join = 400,
      [ValidateSet(0, 20, 40, 80)][int]$Width = 0,
      [ValidateRange(0, 62)][int]$Retries = 7, [string]$DataRate = 'auto',
      [switch]$Aggregate)
$ErrorActionPreference = 'Stop'

$repo  = Split-Path $PSScriptRoot -Parent
$radio = "$repo\build\radio\Release\winawdl_radio.exe"
$awdld = "$repo\build\awdl\awdld.exe"
if (-not (Test-Path $radio)) { throw 'Run scripts\build-radio.ps1 first.' }
if (-not (Test-Path $awdld)) { throw 'Run scripts\build-awdl.ps1 first.' }

# awdld waits for the radio daemon by itself. Probing the pipe from here would
# count as a client connecting and leaving.
$radioArgs = @("$Channel", '--retries', "$Retries", '--data-rate', $DataRate)
if ($Width) { $radioArgs += '--width', "$Width" }
if ($Capture) {
  $file = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Capture)
  $radioArgs += '--capture', "`"$file`""
}
if ($NoBlockAck) { $radioArgs += '--no-block-ack' }
if ($Aggregate) { $radioArgs += '--aggregate' }
$radioProcess = Start-Process $radio -ArgumentList $radioArgs -PassThru -NoNewWindow

$awdldArgs = @('-c', $Channel)
if ($Detail) { $awdldArgs += '-v' }
if ($Redact) { $awdldArgs += '-r' }
$awdldArgs += '-s', $Streams, '-j', $Join
if ($LongGuard) { $awdldArgs += '-l' }
& $awdld @awdldArgs

if (-not $radioProcess.WaitForExit(5000)) { Stop-Process -Id $radioProcess.Id }

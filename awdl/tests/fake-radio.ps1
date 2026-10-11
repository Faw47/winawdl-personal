# Runs awdld against a stand-in for the radio daemon, with no hardware: it must
# connect, transmit on schedule, and accept a peer played back to it. -Redact
# runs it with -r, to read what the log then shows of that peer.
param([int]$Seconds = 4, [switch]$Redact)
$ErrorActionPreference = 'Stop'

$repo  = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$awdld = "$repo\build\awdl\awdld.exe"
if (-not (Test-Path $awdld)) { throw 'Run scripts\build-awdl.ps1 first.' }

$TypeHello = 1; $TypeRx = 2; $TypeTx = 3
$RadiotapRx = [byte[]](0, 0, 10, 0, 0x22, 0, 0, 0, 0, 0xD8)   # flags 0, -40 dBm
$PeerAddress = [byte[]](0x02, 0x11, 0x22, 0x33, 0x44, 0x55)
$SenderOffset = 10

function Send-Message($pipe, [int]$type, [byte[]]$payload) {
  $header = [byte[]]::new(8)
  [BitConverter]::GetBytes([uint16]$type).CopyTo($header, 0)
  [BitConverter]::GetBytes([uint32]$payload.Length).CopyTo($header, 4)
  $pipe.Write($header, 0, 8)
  $pipe.Write($payload, 0, $payload.Length)
  $pipe.Flush()
}

function Read-Exact($pipe, [int]$count, [datetime]$deadline) {
  $buffer = [byte[]]::new($count)
  $read = 0
  while ($read -lt $count) {
    $task = $pipe.ReadAsync($buffer, $read, $count - $read)
    $left = [int]($deadline - (Get-Date)).TotalMilliseconds
    if ($left -le 0 -or -not $task.Wait($left) -or $task.Result -eq 0) { return $null }
    $read += $task.Result
  }
  return ,$buffer
}

$pipe = [IO.Pipes.NamedPipeServerStream]::new('winawdl-radio', 'InOut', 1, 'Byte', 'Asynchronous')
$log = Join-Path $env:TEMP 'awdld-fake-radio.log'
$daemonArgs = @('-c', '6', '-n', '-v')
if ($Redact) { $daemonArgs += '-r' }
$daemon = Start-Process $awdld -ArgumentList $daemonArgs -PassThru -NoNewWindow `
            -RedirectStandardError $log -RedirectStandardOutput "$log.out"
try {
  $pipe.WaitForConnection()
  Send-Message $pipe $TypeHello ([byte[]](3, 0, 0, 0, 6, 20, 0, 0))

  $counts = @{}
  $echoed = $false
  $deadline = (Get-Date).AddSeconds($Seconds)
  while ($true) {
    $header = Read-Exact $pipe 8 $deadline
    if ($null -eq $header) { break }
    $type = [BitConverter]::ToUInt16($header, 0)
    $length = [BitConverter]::ToUInt32($header, 4)
    $payload = if ($length) { Read-Exact $pipe $length $deadline } else { ,[byte[]]::new(0) }
    if ($null -eq $payload) { break }
    $counts[$type] = 1 + [int]$counts[$type]

    if ($type -eq $TypeTx -and -not $echoed) {
      $radiotapLength = [BitConverter]::ToUInt16($payload, 2)
      $frame = $payload[$radiotapLength..($payload.Length - 1)]
      "first transmitted frame: $($frame.Length) bytes, frame control 0x{0:x2}, radiotap {1} bytes" -f $frame[0], $radiotapLength
      $PeerAddress.CopyTo($frame, $SenderOffset)
      Send-Message $pipe $TypeRx ([byte[]]($RadiotapRx + $frame))
      $echoed = $true
    }
  }
  "messages from awdld by type: " + (($counts.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ', ')
} finally {
  if (-not $daemon.HasExited) { Stop-Process -Id $daemon.Id -Force }
  $pipe.Dispose()
}
'--- awdld log ---'
Get-Content $log | Select-Object -First 30

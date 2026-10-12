# Takes winawdl off this PC: gives the Wi-Fi card back to its own driver,
# removes the driver, the trust in the certificate it was signed with, the
# firewall rules, the Start Menu entry and the files. What was received stays
# where it was stored. -RemoveData also deletes the app's settings and logs.
# -DryRun says what would be done and changes nothing. -Yes asks no questions.
param([switch]$Yes, [switch]$DryRun, [switch]$RemoveData)
$ErrorActionPreference = 'Stop'

$Target        = Join-Path $env:ProgramFiles 'winawdl'
$HardwareId    = 'PCI\VEN_10EC&DEV_C852'
$InfName       = 'winawdl_regproxy.inf'
$CertSubject   = 'CN=winawdl driver (signed on this PC)'
$TrustStores   = 'Root', 'TrustedPublisher'
$FirewallGroup = 'winawdl AirDrop'
$UninstallKey  = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\winawdl'
$Shortcut      = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\winawdl.lnk'
$Data          = (Join-Path $env:LOCALAPPDATA 'winawdl'), (Join-Path $env:ProgramData 'winawdl')

function Step([string]$what, [scriptblock]$do) {
  if ($DryRun) { "  would: $what"; return }
  "  $what"
  & $do
}

function Get-CardDriver {
  $card = Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -like "$HardwareId*" } | Select-Object -First 1
  if (-not $card) { return 'not found' }
  $inf = (Get-PnpDeviceProperty -InstanceId $card.InstanceId -KeyName DEVPKEY_Device_DriverInfPath).Data
  "$($card.FriendlyName), driver package $inf"
}

function Get-PublishedPackages {
  $store = [xml](pnputil /enum-drivers /format xml | Out-String)
  $store.PnpUtil.Driver | Where-Object { $_.OriginalName -eq $InfName } | ForEach-Object { $_.DriverName }
}

if ($DryRun) { 'Dry run: nothing on this PC is changed.'; '' }
$me = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $DryRun -and -not $me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  throw 'This needs administrator rights. Start uninstall.cmd, which asks Windows for them.'
}
if (Get-Process winawdl -ErrorAction SilentlyContinue) {
  throw 'winawdl is running. Quit it from its icon in the notification area, then start again.'
}

"The Wi-Fi card now: $(Get-CardDriver)"
if (-not $Yes -and -not $DryRun -and (Read-Host 'Take winawdl off this PC? (y/n)') -ne 'y') {
  'Nothing was changed.'
  exit 1
}

'Driver'
Step 'remove it, so that Windows gives the card back to its own driver' {
  foreach ($package in Get-PublishedPackages) {
    pnputil /delete-driver $package /uninstall /force | Out-Null
  }
  pnputil /scan-devices | Out-Null
}
Step 'stop trusting the certificate it was signed with' {
  foreach ($store in $TrustStores) {
    Get-ChildItem "Cert:\LocalMachine\$store" | Where-Object { $_.Subject -eq $CertSubject } | Remove-Item
  }
  # An install that was cut short may have left the key behind.
  Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $CertSubject } | Remove-Item -DeleteKey
}

'Firewall'
Step 'remove the two rules' {
  Get-NetFirewallRule -Group $FirewallGroup -ErrorAction SilentlyContinue | Remove-NetFirewallRule
}

'Start Menu and files'
Step "remove the shortcut, the entry in the list of installed apps, and $Target" {
  Remove-Item $Shortcut -ErrorAction SilentlyContinue
  Remove-Item $UninstallKey -Recurse -ErrorAction SilentlyContinue
  if (Test-Path $Target) { Remove-Item $Target -Recurse -Force }
}
if ($RemoveData) {
  Step 'remove the app''s settings, its certificate and its logs' {
    $Data | Where-Object { Test-Path $_ } | ForEach-Object { Remove-Item $_ -Recurse -Force }
  }
}

''
if ($DryRun) { 'Dry run done: nothing was changed.'; exit 0 }
"The Wi-Fi card now: $(Get-CardDriver)"
'winawdl is off this PC. Files it received are still where they were stored.'
if (-not $RemoveData) { "Its settings and logs are still in $($Data -join ' and ')." }

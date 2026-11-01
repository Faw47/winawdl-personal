# Installs winawdl from the folder this script is in: copies it to Program
# Files, signs the driver with a certificate made on this PC, binds the driver
# to the Wi-Fi card, lets AirDrop through the firewall and adds a Start Menu
# entry. uninstall.ps1 undoes all of it.
# -DryRun says what would be done and changes nothing. -Yes asks no questions.
# The setup program uses two more. -Missing only lists what the PC still
# lacks, one "winget id, tab, name" a line. -Setup says the setup program has
# copied the files and makes the shortcut and the list entry itself.
param([switch]$Yes, [switch]$DryRun, [switch]$Missing, [switch]$Setup, [switch]$SkipExplorer)
$ErrorActionPreference = 'Stop'
# A 32-bit program that starts this is shown the 32-bit Program Files and no
# pnputil, so it carries on in the 64-bit PowerShell.
if (-not [Environment]::Is64BitProcess -and [Environment]::Is64BitOperatingSystem) {
  $native = Join-Path $env:SystemRoot 'Sysnative\WindowsPowerShell\v1.0\powershell.exe'
  $again = @($PSBoundParameters.Keys | ForEach-Object { "-$_" })
  & $native -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath @again
  exit $LASTEXITCODE
}

$Source        = $PSScriptRoot
$Target        = Join-Path $env:ProgramFiles 'winawdl'
$HardwareId    = 'PCI\VEN_10EC&DEV_C852'
$InfName       = 'winawdl_regproxy.inf'
# Not the subject scripts\package-driver.ps1 uses: a PC set up from source
# keeps its own certificate, and uninstalling removes only this one.
$CertSubject   = 'CN=winawdl driver (signed on this PC)'
$TrustStores   = 'Root', 'TrustedPublisher'
$FirewallGroup = 'winawdl AirDrop'
$ReceiverPort  = 8771
$UninstallKey  = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\winawdl'
$Shortcut      = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\winawdl.lnk'
# The oldest Windows the driver's INF accepts.
$OldestBuild   = 19041

function Step([string]$what, [scriptblock]$do) {
  if ($DryRun) { "  would: $what"; return }
  "  $what"
  & $do
}

function Test-Administrator {
  $me = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
  $me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-WifiCard {
  Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -like "$HardwareId*" } | Select-Object -First 1
}

function Get-CardDriver {
  $card = Get-WifiCard
  if (-not $card) { return 'not found' }
  $inf = (Get-PnpDeviceProperty -InstanceId $card.InstanceId -KeyName DEVPKEY_Device_DriverInfPath).Data
  "$($card.FriendlyName), driver package $inf"
}

# Published names (oemNN.inf) of every copy of the driver package in the driver store.
function Get-PublishedPackages {
  $store = [xml](pnputil /enum-drivers /format xml | Out-String)
  $store.PnpUtil.Driver | Where-Object { $_.OriginalName -eq $InfName } | ForEach-Object { $_.DriverName }
}

function Get-PythonVersion {
  # The Store's stand-in for python.exe answers on its error stream only, and
  # Windows PowerShell ends a script over that unless told to carry on.
  $ErrorActionPreference = 'Continue'
  foreach ($name in 'py', 'python') {
    $found = Get-Command $name -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $found) { continue }
    $said = & $found.Source --version 2>$null
    if ("$said" -match 'Python (\d+)\.(\d+)') { return [version]"$($Matches[1]).$($Matches[2])" }
  }
}

# What has to be on the PC already, with the name winget knows each by.
function Get-Missing {
  $lacking = @()
  if (-not (Test-Path (Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.WindowsDesktop.App\10.*'))) {
    $lacking += @{ Name = '.NET Desktop Runtime 10, which the app runs on'; Id = 'Microsoft.DotNet.DesktopRuntime.10' }
  }
  $python = Get-PythonVersion
  if (-not $python -or $python -lt [version]'3.10') {
    $lacking += @{ Name = 'Python 3.10 or newer, which the AirDrop part runs on'; Id = 'Python.Python.3.14' }
  }
  if (-not (Test-Path (Join-Path $env:SystemRoot 'System32\vcruntime140_1.dll'))) {
    $lacking += @{ Name = 'the Visual C++ runtime, which the radio daemon uses'; Id = 'Microsoft.VCRedist.2015+.x64' }
  }
  $lacking
}

if ($Missing) {
  Get-Missing | ForEach-Object { "$($_.Id)`t$($_.Name)" }
  exit 0
}

$Version = (Get-Content (Join-Path $Source 'VERSION') -TotalCount 1).Trim()
"winawdl $Version"
if ($DryRun) { 'Dry run: nothing on this PC is changed.' }
''

if (-not $DryRun -and -not (Test-Administrator)) {
  throw 'This needs administrator rights. Start install.cmd, which asks Windows for them.'
}
if (-not [Environment]::Is64BitOperatingSystem -or [Environment]::OSVersion.Version.Build -lt $OldestBuild) {
  throw "winawdl needs 64-bit Windows 10, build $OldestBuild, or newer."
}
if (-not (Get-WifiCard)) {
  "No Realtek RTL8852CE ($HardwareId) was found in this PC."
  'winawdl drives that card and no other. Nothing was installed.'
  exit 1
}
$Programs = 'winawdl', 'awdld', 'winawdl_radio'
if ($Setup) {
  # Setup has had the app closed. The copy that runs its radio goes a moment later.
  if (-not $DryRun) {
    foreach ($second in 1..15) {
      if (-not (Get-Process $Programs -ErrorAction SilentlyContinue)) { break }
      Start-Sleep -Seconds 1
    }
    Get-Process $Programs -ErrorAction SilentlyContinue | Stop-Process -Force
  }
} elseif (Get-Process winawdl -ErrorAction SilentlyContinue) {
  throw 'winawdl is running. Quit it from its icon in the notification area, then start again.'
}

$lacking = @(Get-Missing)
if ($lacking -and $Setup) {
  # The setup program has asked about these already.
  'Still not on this PC, and the app will not start without:'
  $lacking | ForEach-Object { "  $($_.Name)" }
  ''
} elseif ($lacking) {
  'Not on this PC yet:'
  $lacking | ForEach-Object { "  $($_.Name)" }
  $winget = Get-Command winget -ErrorAction SilentlyContinue
  if (-not $winget) {
    throw 'Install them, then start again.'
  }
  $lacking | ForEach-Object { "  winget install --id $($_.Id) -e" }
  if ($DryRun) {
    '  would: ask whether to run those now'
  } elseif ($Yes -or (Read-Host 'Run those now? Each asks for your agreement itself. (y/n)') -eq 'y') {
    foreach ($one in $lacking) {
      & $winget.Source install --id $one.Id -e
    }
    if (@(Get-Missing)) { throw 'Something is still missing. Install it, then start again.' }
  } else {
    throw 'Install them, then start again.'
  }
  ''
}

# Only an elevated prompt is shown the right, so only there can it be missed.
$whoami = Join-Path $env:SystemRoot 'System32\whoami.exe'
if ((Test-Administrator) -and -not ((& $whoami /priv | Out-String) -match 'SeLockMemoryPrivilege')) {
  'Note: this account may not lock pages in memory, which the radio needs for its buffers.'
  '  Give it "Lock pages in memory" under Local Security Policy, Local Policies, User Rights'
  '  Assignment, then sign out and in. The radio will not start until then.'
  ''
}

'This will:'
"  copy winawdl to $Target"
'  make a certificate on this PC, sign the driver with it, add the certificate to this'
'    PC''s trusted roots and trusted publishers, then delete its private key for good'
'  give the Wi-Fi card to that driver: Wi-Fi is down from then until you uninstall'
"  let AirDrop through Windows Firewall, from link-local addresses only (TCP $ReceiverPort, UDP 5353)"
'  add winawdl to the Start Menu and to the list of installed apps'
''
"The Wi-Fi card now: $(Get-CardDriver)"
''
if (-not $Yes -and -not $DryRun -and (Read-Host 'Go ahead? (y/n)') -ne 'y') {
  'Nothing was changed.'
  exit 1
}

'Files'
if ($Source.TrimEnd('\') -eq $Target) {
  '  already in place'
} else {
  Step "copy to $Target" {
    if (Test-Path $Target) { Remove-Item $Target -Recurse -Force }
    Copy-Item $Source $Target -Recurse
  }
}

'Driver'
$driver = Join-Path $Target 'driver'
$signed = 'winawdl_regproxy.cat', 'winawdl_regproxy.dll' | ForEach-Object { Join-Path $driver $_ }
Step 'forget the certificate of an earlier install' {
  foreach ($store in $TrustStores) {
    Get-ChildItem "Cert:\LocalMachine\$store" | Where-Object { $_.Subject -eq $CertSubject } | Remove-Item
  }
}
Step 'make a certificate, trust it on this PC, and sign the driver and the Share sheet package with it' {
  $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $CertSubject `
            -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy NonExportable `
            -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(10)
  try {
    $public = Join-Path $driver 'winawdl-signing.cer'
    Export-Certificate -Cert $cert -FilePath $public | Out-Null
    foreach ($store in $TrustStores) {
      Import-Certificate -FilePath $public -CertStoreLocation "Cert:\LocalMachine\$store" | Out-Null
    }
    foreach ($file in $signed) {
      $result = Set-AuthenticodeSignature -FilePath $file -Certificate $cert -HashAlgorithm SHA256
      if ($result.Status -ne 'Valid') { throw "$file could not be signed: $($result.StatusMessage)" }
    }
    # PowerShell cannot sign a package; the app does, with Windows' own signer.
    $package = Join-Path $Target 'package\winawdl.msix'
    $result = Start-Process -FilePath (Join-Path $Target 'winawdl.exe') -Wait -PassThru -WindowStyle Hidden `
                -ArgumentList '--share-sign', ('"' + $package + '"'), $cert.Thumbprint
    if ($result.ExitCode -ne 0) { throw 'The Share sheet package could not be signed.' }
  } finally {
    # Nothing else is ever signed with it, so what this PC now trusts can sign nothing more.
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -DeleteKey
  }
}
Step 'add the driver package to Windows' {
  pnputil /add-driver (Join-Path $driver $InfName) | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "pnputil could not add the package ($LASTEXITCODE)" }
}
Step 'give the Wi-Fi card to it' {
  # The vendor's driver ranks higher, so the change has to be forced. This is
  # the call devcon makes for "update".
  Add-Type -Namespace Winawdl -Name NewDev -MemberDefinition @'
[DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
public static extern bool UpdateDriverForPlugAndPlayDevicesW(
    IntPtr window, string hardwareId, string infPath, uint flags, out bool rebootNeeded);
'@
  $InstallFlagForce = 1
  $reboot = $false
  $done = [Winawdl.NewDev]::UpdateDriverForPlugAndPlayDevicesW(
    [IntPtr]::Zero, $HardwareId, (Join-Path $driver $InfName), $InstallFlagForce, [ref]$reboot)
  if (-not $done) {
    $code = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
    throw ('Windows would not give the card to the driver (error 0x{0:X8}). Run uninstall.cmd to undo the rest.' -f $code)
  }
  if ($reboot) { '  Windows wants a restart before the card works with it.' }
  $bound = (Get-PnpDeviceProperty -InstanceId (Get-WifiCard).InstanceId -KeyName DEVPKEY_Device_DriverInfPath).Data
  Get-PublishedPackages | Where-Object { $_ -ne $bound } | ForEach-Object { pnputil /delete-driver $_ | Out-Null }
}

'Firewall'
Step 'two inbound rules, for link-local addresses only' {
  Get-NetFirewallRule -Group $FirewallGroup -ErrorAction SilentlyContinue | Remove-NetFirewallRule
  $common = @{
    Group = $FirewallGroup; Direction = 'Inbound'; Action = 'Allow'; Profile = 'Any'
    RemoteAddress = 'fe80::/64'
  }
  New-NetFirewallRule @common -DisplayName 'winawdl AirDrop receiver' -Protocol TCP -LocalPort $ReceiverPort | Out-Null
  New-NetFirewallRule @common -DisplayName 'winawdl AirDrop discovery' -Protocol UDP -LocalPort 5353 | Out-Null
}

if ($Setup) {
  # An install made from the zip left an entry of its own in the list of
  # installed apps. Its shortcut is where the setup program has just put its own.
  'Earlier install'
  Step 'take over its entry in the list of installed apps, if one was made from the zip' {
    Remove-Item $UninstallKey -Recurse -ErrorAction SilentlyContinue
  }
  ''
  if ($DryRun) { 'Dry run done: nothing was changed.'; exit 0 }
  "The Wi-Fi card now: $(Get-CardDriver)"
  'Installed.'
  exit 0
}

'Start Menu'
Step 'a shortcut, and an entry in the list of installed apps' {
  $link = (New-Object -ComObject WScript.Shell).CreateShortcut($Shortcut)
  $link.TargetPath = Join-Path $Target 'winawdl.exe'
  $link.WorkingDirectory = $Target
  $link.Description = 'AirDrop on Windows'
  $link.Save()
  $app = Join-Path $Target 'winawdl.exe'
  $result = Start-Process -FilePath $app -ArgumentList '--notification-shortcut',('"' + $Shortcut + '"') -Wait -PassThru -WindowStyle Hidden
  if ($result.ExitCode -ne 0) { throw 'The Start Menu notification identity could not be set.' }
  New-Item $UninstallKey -Force | Out-Null
  $entry = @{
    DisplayName = 'winawdl'; DisplayVersion = $Version; Publisher = 'winawdl project'
    InstallLocation = $Target; DisplayIcon = (Join-Path $Target 'winawdl.exe')
    # Windows asks before it runs this, so the uninstaller does not ask again.
    UninstallString = "`"$(Join-Path $Target 'uninstall.cmd')`" /yes"
    URLInfoAbout = 'https://github.com/raph559/winawdl'
    InstallDate = Get-Date -Format 'yyyyMMdd'
  }
  foreach ($name in $entry.Keys) { Set-ItemProperty $UninstallKey -Name $name -Value $entry[$name] }
  foreach ($name in 'NoModify', 'NoRepair') { Set-ItemProperty $UninstallKey -Name $name -Value 1 -Type DWord }
  # The size the list of installed apps shows, in kilobytes.
  $bytes = (Get-ChildItem $Target -Recurse -File | Measure-Object Length -Sum).Sum
  Set-ItemProperty $UninstallKey -Name EstimatedSize -Value ([int][math]::Ceiling($bytes / 1KB)) -Type DWord
}

if (-not $SkipExplorer) {
  'Explorer'
  Step 'add Send with AirDrop for this account' {
    $app = Join-Path $Target 'winawdl.exe'
    $result = Start-Process -FilePath $app -ArgumentList '--explorer-install' -Wait -PassThru -WindowStyle Hidden
    if ($result.ExitCode -ne 0) { throw 'Send with AirDrop could not be added for this account.' }
    $result = Start-Process -FilePath $app -ArgumentList '--notifications-install' -Wait -PassThru -WindowStyle Hidden
    if ($result.ExitCode -ne 0) { throw 'Notifications could not be registered for this account.' }
    # A convenience: without it, winawdl works as before, so it does not stop the install.
    $result = Start-Process -FilePath $app -ArgumentList '--share-install' -Wait -PassThru -WindowStyle Hidden
    if ($result.ExitCode -ne 0) { '  AirDrop could not be added to the Share sheet for this account.' }
  }
}

''
if ($DryRun) { 'Dry run done: nothing was changed.'; exit 0 }
"The Wi-Fi card now: $(Get-CardDriver)"
'Installed. Start winawdl from the Start Menu and turn its Radio switch on.'
"To take it off again: `"$(Join-Path $Target 'uninstall.cmd')`", or Installed apps in Settings."

#Requires -RunAsAdministrator
# Trusts the local signing certificate, adds the driver package and binds it to
# the Wi-Fi card. Wi-Fi is down while it is bound. uninstall-driver.ps1 undoes it.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\toolchain.ps1"
. "$PSScriptRoot\driver-settings.ps1"

$package = $DriverSettings.PackageDir
$inf = Join-Path $package $DriverSettings.InfName
$certFile = Join-Path $package $DriverSettings.CertFile
if (-not (Test-Path $certFile)) { throw 'Run scripts\package-driver.ps1 first.' }

'Before:'
Show-WifiCardDriver

foreach ($store in $DriverSettings.TrustStores) {
  Import-Certificate -FilePath $certFile -CertStoreLocation "Cert:\LocalMachine\$store" |
    Out-Null
}

pnputil /add-driver $inf
if ($LASTEXITCODE -ne 0) { throw "pnputil could not add the package ($LASTEXITCODE)" }

# The vendor driver ranks higher, so the switch has to be forced.
$tools = Initialize-Toolchain
& $tools.DevCon update $inf $DriverSettings.HardwareId
$devconExit = $LASTEXITCODE

'After:'
Show-WifiCardDriver
if ($devconExit -eq 1) { 'devcon reports that a reboot is needed.' }
if ($devconExit -gt 1) { throw "devcon could not bind the driver ($devconExit)" }

# Earlier builds stay in the driver store until removed; drop the unused ones.
$boundInf = (Get-PnpDeviceProperty -InstanceId (Get-WifiCard).InstanceId `
               -KeyName DEVPKEY_Device_DriverInfPath).Data
Get-PublishedPackages | Where-Object { $_ -ne $boundInf } |
  ForEach-Object { pnputil /delete-driver $_ | Out-Null }

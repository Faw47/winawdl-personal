#Requires -RunAsAdministrator
# Removes the driver package so Windows returns the card to the vendor driver,
# then removes the certificate trust. -RemoveSigningKey also deletes the key.
param([switch]$RemoveSigningKey)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\driver-settings.ps1"

'Before:'
Show-WifiCardDriver

foreach ($package in Get-PublishedPackages) {
  pnputil /delete-driver $package /uninstall /force
}
pnputil /scan-devices | Out-Null

$trusted = foreach ($store in $DriverSettings.TrustStores) {
  Get-ChildItem "Cert:\LocalMachine\$store" |
    Where-Object { $_.Subject -eq $DriverSettings.CertSubject }
}
$trusted | Remove-Item

if ($RemoveSigningKey) {
  Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $DriverSettings.CertSubject } |
    Remove-Item -DeleteKey
}

'After:'
Show-WifiCardDriver

# Values shared by the driver packaging, install and uninstall scripts.
$repoRoot = Split-Path $PSScriptRoot -Parent

$DriverSettings = @{
  InfName      = 'winawdl_regproxy.inf'
  BuildDir     = "$repoRoot\driver\regproxy\build"
  PackageDir   = "$repoRoot\driver\regproxy\build\package"
  PackageFiles = @('winawdl_regproxy.inf', 'winawdl_regproxy.dll')
  CertSubject  = 'CN=winawdl driver signing (local test)'
  CertFile     = 'winawdl-signing.cer'
  HardwareId   = 'PCI\VEN_10EC&DEV_C852'
  TrustStores  = @('Root', 'TrustedPublisher')
}

# Published names (oemNN.inf) of every copy of our package in the driver store.
function Get-PublishedPackages {
  $store = [xml](pnputil /enum-drivers /format xml | Out-String)
  $store.PnpUtil.Driver |
    Where-Object { $_.OriginalName -eq $DriverSettings.InfName } |
    ForEach-Object { $_.DriverName }
}

function Get-WifiCard {
  Get-PnpDevice -PresentOnly |
    Where-Object { $_.InstanceId -like "$($DriverSettings.HardwareId)*" } |
    Select-Object -First 1
}

function Show-WifiCardDriver {
  $card = Get-WifiCard
  if (-not $card) { 'Wi-Fi card not found.'; return }
  $service = (Get-PnpDeviceProperty -InstanceId $card.InstanceId `
                -KeyName DEVPKEY_Device_Service).Data
  $inf = (Get-PnpDeviceProperty -InstanceId $card.InstanceId `
            -KeyName DEVPKEY_Device_DriverInfPath).Data
  "Wi-Fi card: $($card.FriendlyName)"
  "  status $($card.Status), problem code $($card.ConfigManagerErrorCode)"
  "  service $service, driver package $inf"
}

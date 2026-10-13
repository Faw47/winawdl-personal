# Gathers what the builds left into build\installer\winawdl-<version>, stamps
# the driver's version, makes its catalog, and zips the folder. Signs nothing:
# install.ps1 signs the driver on the PC it runs on, so nothing here is tied
# to the PC that built it. Needs the Windows Driver Kit, for the catalog.
# -Build runs the builds first. -AllowDirty takes uncommitted changes, for a
# trial; what is published is built from a commit.
param([switch]$Build, [switch]$AllowDirty)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\toolchain.ps1"

$repo = Split-Path $PSScriptRoot -Parent
$changed = git -C $repo status --porcelain
if ($changed -and -not $AllowDirty) {
  throw 'There are uncommitted changes. Commit them, or pass -AllowDirty for a trial that is not published.'
}

if ($Build) {
  & "$repo\driver\regproxy\build.ps1" | Out-Null
  & "$PSScriptRoot\build-radio.ps1" -Targets winawdl_radio | Out-Null
  & "$PSScriptRoot\build-awdl.ps1" | Out-Null
  & "$PSScriptRoot\build-app.ps1" | Out-Null
}

$version = ([xml](Get-Content "$repo\app\winawdl.csproj")).Project.PropertyGroup.Version | Select-Object -First 1
$stage = "$repo\build\installer\winawdl-$version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

# Where each file comes from, and the folder of the installed copy it goes in.
$files = @(
  @('build\app\winawdl\winawdl.exe', ''), @('build\app\winawdl\winawdl.dll', ''),
  @('build\app\winawdl\winawdl.deps.json', ''), @('build\app\winawdl\winawdl.runtimeconfig.json', ''),
  @('installer\install.ps1', ''), @('installer\install.cmd', ''), @('installer\uninstall.ps1', ''),
  @('installer\uninstall.cmd', ''), @('installer\README.txt', ''),
  @('airdrop\*.py', 'airdrop'), @('scripts\airdrop-beacon.ps1', 'scripts'),
  @('build\radio\Release\winawdl_radio.exe', 'radio'),
  @('build\awdl\awdld.exe', 'awdl'), @('build\awdl\wintun.dll', 'awdl'),
  @('driver\regproxy\winawdl_regproxy.inf', 'driver'), @('driver\regproxy\build\winawdl_regproxy.dll', 'driver')
)
foreach ($pair in $files) {
  $from = Join-Path $repo $pair[0]
  if (-not (Test-Path $from)) { throw "$($pair[0]) is missing: build first, or pass -Build." }
  $to = New-Item -ItemType Directory -Force (Join-Path $stage $pair[1])
  Copy-Item $from $to
}

# Each license under the name of what it covers.
$licenses = New-Item -ItemType Directory -Force "$stage\licenses"
Copy-Item "$repo\LICENSE" "$licenses\winawdl (MIT).txt"
Copy-Item "$repo\airdrop\LICENSE" "$licenses\airdrop, adapted from omdrop (MIT).txt"
Copy-Item "$repo\radio\LICENSE" "$licenses\radio daemon and devourer (GPL-2.0).txt"
Copy-Item "$repo\awdl\LICENSE" "$licenses\AWDL daemon and OWL (GPL-3.0).txt"
Copy-Item "$repo\deps\wintun\LICENSE.txt" "$licenses\wintun.dll (WireGuard, prebuilt binaries).txt"

$commit = git -C $repo rev-parse HEAD
$modules = git -C $repo submodule status | ForEach-Object { '  ' + $_.Trim() }
$source = @(
  "winawdl $version",
  '',
  'Every program in this folder was built from the source at',
  '  https://github.com/raph559/winawdl',
  "at commit $commit$(if ($changed) { ', with changes that were not committed: a trial build, not for publishing' }),",
  'with its submodules at:'
) + $modules + @(
  '',
  'radio\winawdl_radio.exe is under the GNU General Public License, version 2 only: its source is',
  'the radio folder of that repository and the devourer submodule. awdl\awdld.exe is under the GNU',
  'General Public License, version 3 or later: its source is the awdl folder and the OWL submodule.',
  'README.md there says how to build both.'
)
Set-Content "$licenses\SOURCE.txt" $source
Set-Content "$stage\VERSION" $version

# pnputil keeps a stored copy whose version it has seen, so each build of the
# installer gets its own: days since 2026-01-01, then minutes since midnight.
$tools = Initialize-Toolchain
$now = Get-Date
$major, $minor = $version.Split('.')[0..1]
$driverVersion = "$major.$minor.$(($now - [datetime]'2026-01-01').Days).$($now.Hour * 60 + $now.Minute)"
& $tools.StampInf -f "$stage\driver\winawdl_regproxy.inf" -d * -v $driverVersion | Out-Null
if ($LASTEXITCODE -ne 0) { throw "stampinf failed ($LASTEXITCODE)" }
& $tools.Inf2Cat "/driver:$stage\driver" /os:10_X64 /uselocaltime | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Inf2Cat failed ($LASTEXITCODE)" }

# What ships must not say who built it, or where.
$account = $env:USERNAME
$telling = Get-ChildItem $stage -Recurse -File | Where-Object {
  $bytes = [IO.File]::ReadAllBytes($_.FullName)
  foreach ($encoding in [Text.Encoding]::GetEncoding(28591), [Text.Encoding]::Unicode) {
    if ($encoding.GetString($bytes).IndexOf($account, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
  }
  $false
}
if ($telling) {
  throw "These hold the name of the account that built them: $($telling.Name -join ', ')"
}

$zip = "$repo\build\installer\winawdl-$version-windows-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $stage -DestinationPath $zip
Get-Item $zip | Select-Object Name, Length
"SHA-256 $((Get-FileHash $zip -Algorithm SHA256).Hash)"

# The same folder as one setup program, where Inno Setup 6 is installed.
$iscc = "${env:ProgramFiles(x86)}\Inno Setup 6", "$env:ProgramFiles\Inno Setup 6", "$env:LOCALAPPDATA\Programs\Inno Setup 6" |
  ForEach-Object { Join-Path $_ 'ISCC.exe' } | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
  & $iscc /Qp "/DVersion=$version" "/DStage=$stage" "/DOutput=$repo\build\installer" "$repo\installer\winawdl.iss"
  if ($LASTEXITCODE -ne 0) { throw "Inno Setup could not compile the setup program ($LASTEXITCODE)" }
  $setup = Get-Item "$repo\build\installer\winawdl-$version-setup.exe"
  $setup | Select-Object Name, Length
  "SHA-256 $((Get-FileHash $setup -Algorithm SHA256).Hash)"
} else {
  'No setup program was made: Inno Setup 6 is not on this PC ("winget install JRSoftware.InnoSetup").'
}
"Driver version $driverVersion, from commit $($commit.Substring(0, 7))$(if ($changed) { ' with uncommitted changes' })"

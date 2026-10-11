# winawdl

An attempt to run Apple's AWDL radio protocol natively on Windows, using the
built-in Realtek RTL8852CE, as the radio half of a Windows AirDrop client.

This is research code. The radio works: the card receives and transmits Wi-Fi
frames in monitor mode from an ordinary Windows program. On top of it the AWDL
daemon joins the AWDL channel, finds nearby Apple devices, synchronises with
them and carries IPv6 traffic to and from them. An AirDrop receiver on top of
that has received photos, a 28 MB video and a Live Photo from an iPhone, at
300 to 430 kB/s. All of it has run on one machine with one card. Read
[DESIGN-NOTES.md](DESIGN-NOTES.md) for the design, the
measurements, the open risks and the safety notes before installing anything.

## Before you run it

- Installing the driver takes the Wi-Fi card away from Windows. Wi-Fi is down
  until `scripts\uninstall-driver.ps1` is run.
- The driver package is signed with a certificate created on your machine,
  which the install script adds to the machine's trusted stores.
- While the AirDrop receiver runs, any Apple device in range that is set to
  send to everyone can put files into your `Downloads` folder without being
  asked. Nothing received is opened.
- The card answers frames without waiting for Bluetooth, which shares its
  radio. Bluetooth devices may lose an occasional packet meanwhile.
- This is an independent project, not affiliated with or endorsed by Apple.
  AirDrop is a trademark of Apple Inc.

## How it fits together

| Piece | What it does |
|---|---|
| `driver/regproxy` | User-mode (UMDF 2) driver bound to the card. Relays register accesses and stops the chip's DMA if its client dies |
| `upstream/devourer` | Submodule: a fork of [devourer](https://github.com/OpenIPC/devourer), the user-space Realtek driver that runs the chip, with a Windows PCIe backend on its `winawdl/pcie-windows` branch |
| `radio` | Programs built on that devourer, including the radio daemon `winawdl_radio` |
| `upstream/owl` | Submodule: [OWL](https://github.com/seemoo-lab/owl), unmodified |
| `awdl` | The AWDL daemon `awdld`: OWL's protocol core with a Windows I/O layer |
| `airdrop` | The AirDrop receiver, adapted from [omdrop](https://github.com/brentkearney/omdrop-plugin): service announcement and the HTTPS endpoints senders talk to |
| `tools`, `probes` | Small clients of the driver and one-off measurements |
| `scripts` | Build, packaging, install and test helpers |
| `deps` | Pinned vcpkg manifest for devourer's dependencies, and Wintun once fetched |

## Building

Clone with the submodules:

```powershell
git clone --recurse-submodules https://github.com/raph559/winawdl.git
```

Requires Visual Studio Build Tools 2022 with its bundled vcpkg, CMake, the
Windows SDK, the Windows Driver Kit 10.0.26100, and MinGW-w64 GCC for the AWDL
daemon (the build script expects the one in `C:\Strawberry\c\bin`).

```powershell
.\driver\regproxy\build.ps1   # the driver
.\tools\build.ps1             # driver test clients and probes
.\scripts\build-radio.ps1     # devourer, its demos, and radio\
.\scripts\build-awdl.ps1      # the AWDL daemon
```

All four only compile. `awdl\tests\fake-radio.ps1` then exercises the AWDL
daemon against a stand-in radio, with no hardware.

The AirDrop receiver is Python 3.10 or newer with no packages to install. It
needs OpenSSL once, to create its certificate; the one in Git for Windows is
found by itself. Its tests run without hardware:

```powershell
py -m unittest discover -s airdrop\tests
```

C and C++ sources follow the `.clang-format` files, and the Python is checked
with `ruff check airdrop` against `airdrop\ruff.toml`.

The AWDL daemon hands traffic to Windows through
[Wintun](https://www.wintun.net/), WireGuard's virtual network adapter.
`scripts\fetch-wintun.ps1` downloads version 0.14.1, checks its published hash
and signature, and leaves `wintun.dll` in `deps\wintun`. Without it the daemon
still runs but drops AWDL data.

## Installing the driver

```powershell
.\scripts\package-driver.ps1     # build, stage and sign; no elevation
.\scripts\install-driver.ps1     # elevated: trust the certificate, bind the card
.\scripts\uninstall-driver.ps1   # elevated: back to the vendor driver
```

Installing adds a self-signed code-signing certificate to the machine's trusted
stores and takes Wi-Fi down until the uninstall script is run.

## Running

Everything below needs an elevated prompt and the driver bound to the card.

```powershell
.\scripts\rx-test.ps1 -Channel 6              # monitor-mode receive, Ctrl+C to stop
.\scripts\tx-test.ps1 -Channel 6              # 500 broadcast test frames
.\build\radio\Release\awdl_sniff.exe 6 40     # list AWDL devices heard for 40 s
.\scripts\awdl-run.ps1 -Channel 6             # radio daemon plus AWDL daemon
```

While `awdl-run.ps1` runs with Wintun present, Windows has a network adapter
named `awdl0`. The daemon prints the address of each peer it finds, which can
be pinged from another elevated or ordinary prompt. The first run installs
Wintun's kernel driver, which is signed by WireGuard LLC.

To receive files over AirDrop, keep `awdl-run.ps1` running and then:

```powershell
.\scripts\airdrop-firewall.ps1   # elevated, once: two inbound rules, link-local only
.\scripts\airdrop-receive.ps1    # no elevation; Ctrl+C to stop
```

While the receiver runs, any Apple device in range set to send to everyone can
put files into `Downloads` without being asked. Nothing received is opened, and
each file is marked as downloaded from the Internet. Contacts Only and sending
are not implemented. `.\scripts\airdrop-firewall.ps1 -Remove` deletes the
rules.

## Licensing

Everything is MIT-licensed ([LICENSE](LICENSE)) except two folders that
build on copyleft projects:

- `radio` links devourer and is GPL-2.0-only. `radio/radio_link.h`, the
  protocol between the two daemons, is MIT so that both sides may include it.
- `awdl` builds on OWL and is GPL-3.0-or-later.

`airdrop` is adapted from omdrop and carries omdrop's own MIT notice in
`airdrop/LICENSE`. Wintun's prebuilt DLL comes under its own license, copied
next to it by the fetch script; it is loaded at run time and is not part of
this repository. The submodules keep their own licenses: devourer GPL-2.0,
OWL GPL-3.0.

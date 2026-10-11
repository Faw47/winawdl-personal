# Native Windows radio for AirDrop on the built-in RTL8852CE

Status: 2026-10-11. The register proxy driver builds and its INF validates. It
is not signed or installed, and nothing here has driven the card yet.

## Goal

Run Apple's AWDL radio protocol natively on Windows 11 using the motherboard's
built-in Realtek RTL8852CE (PCI `10ec:c852`, bus 7 device 0), with no Linux VM
and no USB adapter, as the radio half of a Windows AirDrop implementation.

## Why the stock driver is out

`netsh wlan show wirelesscapabilities` on this PC reports network monitor mode,
IBSS and promiscuous mode as not supported. AWDL needs raw frame receive and
injection, so Realtek's driver has to be bypassed.

## What devourer needs from the operating system

devourer (`OpenIPC/devourer`, GPL-2.0, read at revision `5be2b72`)
already drives this chip from user space on Linux through `vfio-pci`. Its
README reports monitor receive and injection validated on air at 5 and 6 GHz.
Reading `src/PcieTransport.cpp` and `src/PcieDmaAx.cpp`, the Linux layer
provides five services:

| Service | Linux mechanism | Needed for the 8852CE |
|---|---|---|
| Map BAR2, 1 MiB of registers | `mmap` on the vfio device | Yes |
| PCI config space read and write | vfio config region | Bus master enable, ASPM off, LTR read |
| One DMA slab, addresses below 4 GiB | anonymous memory mapped at a chosen IOMMU address | Yes, about 8.3 MiB |
| Interrupt delivery | MSI through eventfd | No. The 8852CE path polls |
| Taking the device from the vendor driver | bind to `vfio-pci` | Yes |

The slab holds the transmit and receive rings and buffers. All descriptor
address fields are 32-bit in devourer's current code.

## Candidate Windows design, no kernel code of ours

1. A user-mode (UMDF 2) function driver bound to the card in place of
   Realtek's. It maps BAR2 and forwards register reads and writes from the
   application over device I/O control calls. Microsoft documents register
   access and interrupts for UMDF drivers; it does not offer DMA to them.
2. The application allocates DMA memory itself with the documented AWE call
   `AllocateUserPhysicalPages2` and the `MEM_EXTENDED_PARAMETER_NONPAGED_LARGE`
   flag. That returns locked 2 MiB blocks together with their physical frame
   numbers.
3. devourer's AX DMA plane is changed to place its areas in separate 2 MiB
   blocks and to compute each receive buffer's address individually, since the
   receive buffer areas are 2.9 MiB each.
4. A new `PcieTransport` backend replaces the vfio calls with 1 and 2.

## Measurements on this PC

Run with the first version of `probes/awe_probe.c`, after 302 hours of uptime
with 10.9 GB free:

| Question | Result |
|---|---|
| Can the session enable `SeLockMemoryPrivilege` | Yes, when elevated |
| Ordinary 4 KiB locked pages | 512 MiB obtained, longest contiguous run 15 pages, 3 pages below 4 GiB. Not usable |
| 2 MiB locked blocks | 17 obtained, each 2 MiB aligned with a known physical address |
| 2 MiB blocks below 4 GiB | 4 of 17. The design needs 4 to 6 |

The current probe also maps a block and writes to it. Build it with
`tools/build.ps1` and run `tools/build/awe_probe.exe` from an elevated prompt.

A second run on 2026-10-11, shortly after the first, returned 38 to 40 blocks
of 2 MiB with 24 to 25 below 4 GiB, so low memory is not the constraint the
first run suggested. Mapping such a block into the process failed with the
one method tried; `probes/awe_probe.c` now tries six combinations, because
Microsoft documents the allocation but not the mapping.

One register round trip through the proxy costs about 31 microseconds. Polling
the receive ring at devourer's default 200 microsecond interval is affordable.

## Open risks, in the order they would be hit

1. Resolved 2026-10-11, both directions: with Kernel DMA Protection On and
   virtualization-based security running, the chip fetched its 414 KiB
   firmware from the AWE slab and booted it, then delivered 3458 receive
   buffers into it in monitor mode. Physical addresses work as bus addresses.
2. Resolved 2026-10-11: a user-mode driver package signed with a self-issued
   certificate installs and starts with Secure Boot on, once the certificate is
   in the machine's Root and TrustedPublisher stores. The card bound to it with
   status OK and the 1 MiB register window mapped.
3. Resolved 2026-10-11: PCI config space stays out of reach, but Windows
   enables bus mastering when it starts the device, which is all the firmware
   download needed. ASPM stays on the power plan setting, and devourer reads
   LTR as disabled and silences it.
4. Low memory turned out not to be scarce: later runs found 24 to 26 low
   2 MiB blocks free, and the slab needs 6.
5. Security: this design lets an administrator process aim a bus-mastering
   device at physical memory. That is inherent to user-space DMA.

## Safety of user-space DMA here

Nothing fences the chip's DMA: no IOMMU domain is ours. If the process died
with DMA live, the chip would keep writing received frames into pages Windows
had already reused. Three layers guard against that:

1. devourer stops DMA when the radio object is destroyed.
2. The transport replays the DMA plane's stop sequence before freeing the slab.
3. The driver stores the same sequence (`IOCTL_REGPROXY_SET_CLOSE_OPS`) and
   replays it when the handle closes, which the kernel does for a killed
   process before it tears down the address space. The transport refuses to
   enable DMA if this cannot be armed.

Not covered: a crash of the driver host process itself, and sleep or
hibernation with the radio running.

## Acknowledging frames

A monitor-mode radio does not acknowledge what it receives, and devourer
reports no ACK responder for this chip generation. A sender that gets no ACK
repeats every frame up to its retry limit, falls back to its slowest rate and
may stop sending to the peer. The first AirDrop transfers showed the cost: a
280 kB photo took 30 s, and three of four attempts stalled or timed out.

The radio daemon now measures this for every session: of the data frames
addressed to its client, the share carrying the retry flag. With nothing
acknowledged that share sits at 85%, which is each frame sent seven times.

What it takes on the RTL8852CE was found by trying setups against an iPhone
and reading that share, 2026-10-11:

- The address CAM entry of macid 0 must name our address. rtw89 keeps no
  receiver address in a register on this chip. devourer built the entry as
  for the 8852B; the 8852C wants a 32-byte entry and a BSSID byte mask of
  0x3f. Read back from CAM memory, the corrected entry was stored as sent.
- The network type of the entry and port, an AP role and a join command made
  no difference. Neither did sniffer mode, nor naming the sender as the
  entry's peer.
- Response frames must not wait for Bluetooth. Before a SIFS-spaced
  transmission the MAC checks, among others, that Bluetooth is idle
  (`SIFS_CHK_BTCCA` in `R_AX_CCA_CONTROL`, `RSP_CHK_BTCCA` in
  `R_AX_TRXPTCL_RESP_0`). With both set, the retry share was 81 to 86% in
  every setup. With both cleared it was 0%, 4% and 0% in three trials. This is
  a combined Wi-Fi and Bluetooth card whose Bluetooth half Windows keeps
  using, and devourer does not set up coexistence on the 8852C. Which of the
  two bits is the one that matters was not separated.

`HalKestrel::set_station_identity` in the devourer patch does these three
things, behind `IRadio::SetStationIdentity`, and the radio daemon calls it as
soon as its client has given its address and BSSID. Reception then narrows
from every frame to those for the client, broadcast and multicast, which is
all AWDL needs. The card may now answer while Bluetooth is transmitting.

Also noted: the "LTE interface not ready" error devourer logs twice per
bring-up is benign here. The register it polls, 0xDAF0, reads back the chip's
unimplemented-register pattern on this card, and rtw89 never touches it on
the 8852C.

## The AWDL layer

OWL is reused, not rewritten. Its protocol library (`src/`) and its event core
(`daemon/core.c`) compile unmodified with MinGW-w64 GCC against small shims in
`awdl/compat`: the BSD network headers it includes, a libev subset, and the
libpcap calls `core.c` makes. Only OWL's `io.c` and `netutils.c`, which are
Linux and macOS code, are replaced (`awdl/win`).

It runs as its own process, `awdld`, for two reasons. OWL is GPL-3.0 and
devourer is GPL-2.0, which cannot be linked into one program. And OWL is GNU C,
which Microsoft's compiler rejects, while devourer builds with it. The two
talk over a named pipe whose protocol is `radio/radio_link.h`, MIT-licensed so
both sides may include it.

OWL timestamps frames with the host clock at arrival, not the chip's clock, so
the pipe adds its latency to AWDL's synchronisation. The event loop waits on a
high-resolution timer because AWDL slots are 16 ms and an ordinary Windows
wait can overshoot by that much.

Each run uses a fresh random locally administered address, as Apple devices
do. The Arpa TLV carries the computer name; Apple devices send a random UUID
there instead.

### Host network interface

OWL gives the host an Ethernet tap device. Windows has no built-in equivalent,
so `awdl/win/host_if.c` creates a Wintun adapter named `awdl0` for the life of
the daemon. Wintun carries bare IP packets, not Ethernet frames, and
`awdl/win/host_frame.c` converts between the two. No neighbor table is needed
for that: an AWDL peer's IPv6 address is fe80:: plus the EUI-64 of its MAC, so
the destination MAC is read back out of the destination address. Apple devices
do the same and do not run neighbor discovery on AWDL.

The adapter gets the link-local address derived from the AWDL MAC and an MTU
of 1450, OWL's value. Windows also assigns every adapter a link-local address
with a random interface identifier. A peer cannot answer that one, so the
daemon removes it, and removes it again whenever Windows adds it back.

Windows treats `awdl0` like any other network. Its own multicast chatter
(mDNS, LLMNR, SSDP) therefore goes out as AWDL data to nearby Apple devices,
and Windows Firewall applies its public-network rules to what comes in.
Neither is filtered by the daemon yet.

Wintun's DLL is loaded only from the daemon's own directory, because the
daemon runs elevated. Creating the adapter installs Wintun's kernel driver on
first use.

## The AirDrop layer

`airdrop/` is the receiving half of omdrop, adapted for Windows. omdrop's
receiver was kept for what it knows about real senders and for its limits on
everything a stranger in radio range controls: body size, decoded size, member
count, read rate and free disk space. What changed:

- omdrop leaves the service announcement to its radio helper, which builds
  raw frames. Here `mdns.py` announces `_airdrop._tcp` with ordinary sockets
  on `awdl0`, repeating every two seconds because AWDL drops multicast sent
  outside the right slot. The records are OpenDrop's, plus the NSEC record
  omdrop found Apple devices wait for.
- The archive reader is `cpio.py`, so no native library is needed.
- File names are also cleared of what Windows treats specially: reserved
  characters, the colon that selects an alternate data stream, device names
  and trailing dots. Stored files get the Internet zone mark.
- A received link is stored as a `.url` file only if it has no control
  characters. A line break would let a sender add keys such as an icon path
  on a remote share, which Windows fetches when it draws the folder.
- The identity is a self-signed certificate only. Contacts Only, the Apple
  identity and omdrop's configuration file and panel are left out for now.

The receiver listens on every interface, because the adapter exists only while
`awdld` runs, and drops any connection that did not arrive on `awdl0`.

An iPhone ends the upload of a plain photo properly. For a video or a Live
Photo it sends every chunk of the body and then waits, without the
zero-length chunk that ends it: three such uploads on 2026-10-11 stalled with
all their data received and the reader waiting for the next chunk header,
while the phone sent nothing more. So when a body stalls between two chunks,
the receiver accepts what arrived if it is a complete archive down to its
trailer. Confirmed 2026-10-11 with a Live Photo, which was stored.

Waiting out the 30-second idle timeout for that is not acceptable, so the
body is also decoded as it arrives, keeping none of it, by a watcher that
knows when the archive is whole: the cpio trailer read and the last 512-byte
record filled, which is how every upload seen so far ended. From then on the
receiver waits two seconds for the end marker, not thirty. A plain photo's
marker comes at once and the connection stays usable; after a video the wait
runs out and the sender is answered. A body the watcher cannot follow just
falls back to the idle timeout.

The upload's `TotalBytes` header is the sum of the file sizes, which for a
Live Photo is the only place the size appears: its request to send declares
0 bytes, because the item is a folder (`.pvt`) holding the still, the video
and a metadata file. Those three are stored side by side for now.

## Fallback: a small kernel driver

A KMDF driver of a few hundred lines would do all five services properly,
including a 32-bit common buffer. With Secure Boot on it must be signed through
Microsoft's hardware portal, which needs an EV certificate. The alternative is
turning Secure Boot off and enabling test signing, which is a system security
change for the owner to make, not this project.

## Cheapest de-risking step

Boot a Linux live USB and run devourer's `pcieprobe` against this card. It
separates "this card works with devourer" from "the Windows layer works", and
needs no adapter.

## Progress

| Step | State |
|---|---|
| Windows Driver Kit 10.0.26100 installed through winget | Done |
| `driver/regproxy`: UMDF 2 register proxy, built by `build.ps1` | Builds, INF passes `infverif` |
| Sign the driver package (`scripts/package-driver.ps1`) | Done, with a local non-exportable code-signing certificate |
| Trust the certificate and bind the driver (`scripts/install-driver.ps1`) | Done 2026-10-11, from an elevated prompt. `scripts/uninstall-driver.ps1` reverts |
| `tools/regproxy_test`: open the proxy, check the chip ID | Passed 2026-10-11: die id 0x52, cut 1, register space readable |
| Windows backend for devourer's `PcieTransport` (`patches/devourer`) | Written, builds with MSVC, untested on hardware |
| DMA under Kernel DMA Protection (`pcieprobe win fw`) | Passed 2026-10-11: firmware downloaded and booted on the first attempt |
| Receive frames in monitor mode (`scripts/rx-test.ps1`) | Passed 2026-10-11: 1140 frames on channel 6, first frame 981 ms after launch, clean shutdown |
| Transmit frames (`scripts/tx-test.ps1`) | Passed 2026-10-11: 500 submitted, 0 failed, 499 release reports with status done, while receiving 1364 frames |
| Hear AWDL devices (`radio/awdl_sniff`) | Passed 2026-10-11: the owner's iPhone heard on channel 6 (97 PSF, 50 MIF, 14 data frames in 40 s) and on channel 44 (38 MIF), same AWDL address on both |
| Hardware ACK of unicast frames addressed to us | Found 2026-10-11 by sweep: 0% repeated frames once responses stop waiting for Bluetooth, against 85% before. Now applied by the radio daemon in every session; that permanent form has not run on the card yet |
| Radio daemon (`radio/winawdl_radio`) serving frames over a named pipe | Passed 2026-10-11: 827 frames transmitted for its client, none failed |
| AWDL daemon (`awdl/`, OWL core on Windows) | Passed on air 2026-10-11 on channel 6: added the owner's iPhone as a peer, elected it sync master, received its action and data frames while transmitting 24 action frames per second |
| Host network interface for AWDL data (Wintun, `awdl/win/host_if.c`) | Passed 2026-10-11: `awdl0` came up with the MAC-derived link-local address as its only one, and the owner's iPhone answered 6 of 8 pings over AWDL. Round trips took 0.5 to 0.9 s and the first ping of each run was lost |
| AirDrop receiver (`airdrop/`, adapted from omdrop) | First file received 2026-10-11: the owner's iPhone found the PC in its AirDrop sheet and one 280 kB photo arrived intact. Three other attempts failed, and all ran at about 10 kB/s |

The INF matches only the generic `PCI\VEN_10EC&DEV_C852` ID, so adding the
package to the driver store does not take over the card on its own.

## Practical notes

- Reference clones live in `upstream/` and are not tracked: devourer, OWL and
  omdrop-plugin.
- Installed: Visual Studio Build Tools 2019 and 2022, Windows SDK and WDK
  10.0.26100, CMake, Ninja.
- Build scripts do not rely on `vcvars64.bat`. They call `cl.exe` with
  explicit `INCLUDE` and `LIB`.
- OWL is GPL-3.0 and devourer GPL-2.0. Keep them in separate processes.
- The radio needs six free 2 MiB blocks of physical memory. They used to have
  to lie below 4 GiB, and on 2026-10-11, after 13 days of uptime, Windows had
  one left there and the radio refused to start. A probe in that state found
  61 such blocks below 64 GiB, and only 3 usable 16 KiB runs of ordinary
  pages below 4 GiB against 768 needed, so smaller pieces were no way out.
  The DMA plane now writes 36-bit addresses, as rtw89 does on the 8852C
  without any enabling step: the high bits go into each buffer descriptor,
  each WD-page address entry and the ring base registers. Low blocks are
  still preferred. `radio/tests/dma_address_test` checks the encodings and
  the block choice. On hardware, 2026-10-11, with every block above 4 GiB
  (between 15 and 22 GiB): firmware downloaded and booted first try, 33,120
  packets were received in an AWDL session, and 500 broadcast frames were all
  reported sent. But in three AWDL sessions run that way the iPhone answered
  nothing, against four out of four the day before from low memory. The one
  address field those sessions used that the other checks do not prove is
  the address entry of a WD page, which points at the frame payload. With
  the payload area laid out first and required below 4 GiB, the iPhone
  answered again at once. So the radio needs one low block, not six, and the
  chip does not appear to honour the high bits of that entry as rtw89 writes
  them.

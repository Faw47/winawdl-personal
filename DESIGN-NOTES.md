# Native Windows radio for AirDrop on the built-in RTL8852CE

Status: 2026-10-11. All of this has run on one machine with one card: the
driver binds to the card, the card receives and transmits, AWDL carries IPv6
to an iPhone, the AirDrop receiver has stored photos, a video and a Live
Photo, and the sender has delivered a photo. The Progress table gives the
dates and numbers.

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
address fields are 32-bit in devourer at that revision. The fork writes 36-bit
ones (see Physical memory for DMA).

## Windows design, no kernel code of ours

1. A user-mode (UMDF 2) function driver bound to the card in place of
   Realtek's. It maps BAR2 and forwards register reads and writes from the
   application over device I/O control calls. Microsoft documents register
   access and interrupts for UMDF drivers; it does not offer DMA to them.
2. The application allocates DMA memory itself with the documented AWE call
   `AllocateUserPhysicalPages2` and the `MEM_EXTENDED_PARAMETER_NONPAGED_LARGE`
   flag. That returns locked 2 MiB blocks together with their physical frame
   numbers.
3. devourer's AX DMA plane lays out its rings and buffers so that none
   straddles a 2 MiB block, and computes each one's bus address from the block
   it falls in.
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
of 2 MiB with 24 to 25 below 4 GiB, so low memory was not the constraint that
day. It became one later (see Physical memory for DMA). Mapping such a block
into the process failed with the one method tried; `probes/awe_probe.c` now
tries six combinations, because Microsoft documents the allocation but not
the mapping.

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
4. Low memory comes and goes: some runs found 24 to 26 low 2 MiB blocks
   free, and after 13 days of uptime there was one. The radio now needs one
   low block and five more anywhere below 64 GiB.
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
  for the 8852B; the 8852C takes a 32-byte entry and a BSSID byte mask of
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

`HalKestrel::set_station_identity` in the devourer fork does these three
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

### Channel and rate

Measured 2026-10-11 from captures of one iPhone sending a file
(`awdl-run.ps1 -Capture`):

| | Channel 6 | Channel 44 |
|---|---|---|
| Slots of 16 the iPhone lists for the channel while it sends | 5 | 4 to 11 |
| Data frames | HT MCS 7, 20 MHz | the same |
| Throughput | 188 kB/s | 971 kB/s (30.5 MB in 31.4 s) |

Idle, the iPhone lists its access point's channel, 149, 6 and 44 for one slot
each. Once it sends, the slots it adds are on 44, so that is the daemon's
default channel.

Channel 44 needed two corrections first, made in `awdl/win/frame_fixup.c` as
frames pass between OWL and the radio:

- OWL announces 40 MHz reception in its HT capabilities, and the radio daemon
  listens on 20 MHz. Of 838 data frames, 837 arrived only as a retry at
  MCS 0, 5.2 ms apart, for 125 kB/s. With the 40 MHz bits cleared the first
  tries arrive, at MCS 7 and 0.64 ms apart.
- The iPhone ended its MIFs there with eight zero bytes. OWL refuses a frame
  with one or two bytes left after the last TLV, so the peer never became
  valid and the daemon never synchronised to it. Such leftover zero bytes are
  now cut off.

### Block ack

While it sends, the iPhone asks about once a second for a block-ack agreement
on TID 0 with a window of 64 frames, which lets it aggregate. The radio
daemon answers unless started with `-NoBlockAck`. It first has the card open
a BA CAM entry for that sender (rtw89's `rtw89_fw_h2c_ba_cam`, the sender
named as the peer in the address CAM entry of macid 0), then sends the ADDBA
response, without A-MSDU.

Measured 2026-10-11 on channel 44, one transfer of 30.5 MB:

| | Without | With |
|---|---|---|
| Block-ack requests from the iPhone | 49, unanswered | 1, answered within 1 ms |
| Frames per transmission | 1 | 9.4 on average, 30 at most, each aggregate behind an RTS |
| Frames with the retry flag | 904 of 20,410 | 47 of 20,414 |
| Throughput | 971 kB/s | 1,710 kB/s |

No frame arrived twice. Later runs had up to 159 frames in 20,000 arrive
behind a later sequence number, which TCP reads as loss, so the radio daemon
holds the frames after a missing one for up to 100 ms (`radio/reorder_buffer.h`).

One sender is served at a time. A second iPhone nearby, with about one frame
a second to send, once asked first and kept the session while the one sending
a file was refused 46 times. A sender now takes the session over from a holder
that has sent fewer than 16 frames since it last asked.

### Acknowledgements

With block ack alone, a second spatial stream changed nothing: 1.2, 2.3 and
1.6 MB/s in three runs against 1.9 with one. Captures showed why. Windows
acknowledges every second TCP segment, each acknowledgement is a frame of
its own that takes 0.4 to 0.5 ms to leave, and the iPhone sends what those
release as it gets them:

| | One stream | Two streams |
|---|---|---|
| One aggregate and its acknowledgements | 4.4 ms, 11.7 frames | 2.2 ms, 6.4 frames |
| Share of that spent after the last data frame | 41% | 69% |

So the daemon joins the TCP segments of a burst into one IPv6 packet before
Windows sees them, as a network card's receive coalescing does
(`awdl/win/tcp_merge.c`), and Windows acknowledges once per packet. A burst
ends when the radio has delivered nothing for 400 us. Windows takes these
packets although they exceed the adapter's MTU of 1450.

| Measured 2026-10-11, channel 44 | Throughput |
|---|---|
| Block ack, one stream | 1.9 MB/s |
| With joining | 3.0 MB/s, one acknowledgement per 7 segments, 21 frames per aggregate |
| With joining, two streams and the short guard interval | 6.7 MB/s for 30 MB, 5.9 MB/s for 131 MB, about 30 frames per aggregate |
| The same on a 40 MHz channel | 12.3 MB/s for 30 MB and 13.7 MB/s for 103 MB, 45 frames per aggregate, 37 us per frame against 90 |
| Tuned to 80 MHz, with VHT capabilities announced | 15.9 MB/s for 103 MB, about 100 frames per RTS, 31 us per frame |

Tried on the way and dropped:

- Holding acknowledgements back in the daemon until the radio was quiet for
  300 us: 1.3 MB/s, with 9% of them replaced by a later one. They are
  generated too far apart for that.
- Asking Windows for fewer acknowledgements with `SIO_TCP_SET_ACK_FREQUENCY`
  on the receiver's socket: it refuses every value above 1.

OWL passes on its whole 1518-byte buffer for each received packet, and up to
68 bytes of whatever lay behind the packet went to the adapter with it.
`host_frame_ip6` now cuts at the length the IPv6 header gives.

On 5 GHz the radio daemon tunes the card wider than 20 MHz, and the daemon
then leaves OWL's 40 MHz capabilities as they are. For that the
MAC has to be told the width and where the primary 20 MHz lies, which
devourer's `set_channel` did not do (see `set_channel_mac` in the fork). The
capture labels these frames 20 MHz all the same: the width it records from
the receive descriptor is not to be trusted.

At 40 MHz an aggregate stops at about 64 KiB, the most an HT station can
announce. Tuned to 80 MHz, the daemon therefore appends VHT capabilities to
the frames that carry OWL's HT ones, in the TLV an iPhone uses for its own
(`frame_fixup.c`): two streams up to MCS 9 and A-MPDUs of up to 1 MiB. The
iPhone then sends VHT MCS 9 in bursts of about 100 frames, at 31 us a frame.
That is the 40 MHz rate: it does not send 80 MHz frames, for a reason not
found. The card is tuned to 80 MHz all the same, so that it could take them.

What still separates this from two Apple devices: the iPhone keeps to
40 MHz, about 30% of each cycle goes to the acknowledgement, and the iPhone
is on the channel about half the time. Its schedule keeps slots for
channels 6 and 149 and for its access point's channel.

### Retries

devourer sends a frame once unless told otherwise (`tx.retry_limit` in its
`DeviceConfig`), which suits the broadcast video links it was written for.
Here it meant that a reply lost on the air stayed lost until Windows repeated
it, a second later at the earliest. In one session of five photos the answer
to the iPhone's connection request went missing for two of them: one upload
started 1.0 s late, and one never connected after three answers in a row were
lost. Windows sent 8 of its 46 segments twice.

The radio daemon now asks the card for 7 retries, a station's usual number
(`-Retries` on `awdl-run.ps1`). In the next session of five photos every
connection request was answered at the first try, and Windows sent none of
its 41 segments twice. That is one session each.

An iPhone repeats a connection request 33 ms after the first, and doubles
that wait from the sixth on, up to 2 s. Several copies in one aggregate
therefore mean a fraction of a second spent off the channel. The capture
keeps the whole TCP header for this: the timestamp option dates each copy.

## The AirDrop layer

`airdrop/` is the receiving half of omdrop, adapted for Windows. omdrop's
receiver was kept for the sender behaviour it already handles and for its
limits on everything a stranger in radio range controls: body size, decoded
size, member count, read rate and free disk space. What changed:

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
detects when the archive is whole: the cpio trailer read and the last 512-byte
record filled, which is how every upload seen so far ended. From then on the
receiver waits two seconds for the end marker, not thirty. A plain photo's
marker comes at once and the connection stays usable; after a video the wait
runs out and the sender is answered. A body the watcher cannot follow just
falls back to the idle timeout.

The upload's `TotalBytes` header is the sum of the file sizes, which for a
Live Photo is the only place the size appears: its request to send declares
0 bytes, because the item is a folder (`.pvt`) holding the still, the video
and a metadata file. The still and the video are stored under one name,
numbered together when an earlier file has it; the metadata file, which only
pairs the two for Photos, is left out.

### Sending

`airdrop/sender.py` sends files to a receiver that is visible to everyone.
Three things had to be found out, each from an attempt that failed on
2026-10-11.

- An iPhone that is not sharing does not announce AirDrop. It starts when it
  hears the Bluetooth advertisement an Apple device sends before an AirDrop:
  Apple's manufacturer data, type 5, with four two-byte hashes of the
  sender's contact details, which a receiver visible to everyone does not
  check. `scripts/airdrop-beacon.ps1` sends it with random hashes through
  Windows' own Bluetooth API. In four attempts the iPhone was announcing 3,
  9, 13 and 50 s after the beacon started.
- The iPhone does not announce in multicast DNS packets. Its records travel
  inside its AWDL action frames, in service response TLVs with their own
  shortening of names, and OWL does not read those.
  `awdl/win/service_bridge.c` turns the AirDrop records of each such frame
  into the multicast DNS response the iPhone would have sent and hands it to
  `awdl0`. A frame has no address record, so the bridge adds the link-local
  address the sender derives from its MAC. The other services in those
  frames are not passed on.
- The iPhone accepted the request and then closed the connection 14 ms into
  an upload shaped like OpenDrop's, a gzip archive sent as
  `application/x-cpio`. What it sends itself was read from our own receiver
  (`--trace`) and differs in four ways, all copied now: a transfer
  identifier in the request and again in the upload's headers,
  `application/x-dvzip` blocks, an archive that starts with its folder, and
  each file's size in the request. Which of the four it insists on was not
  separated. Left out are its Apple identity record, its pseudonym and push
  token, and the preview picture.

With those, one 1.0 MB photo reached Photos on the iPhone: 2.2 s for the
upload, 21 s after the command was started. The receiver now also takes the
classic upload form; only the loopback test has sent it one.

OWL gives every data frame 12 Mbit/s, which carried about 1 MB/s while the
phone listened and 0.5 MB/s over all. The radio daemon can put its own
radiotap header on data frames to one station (`-DataRate`). With one
24.5 MB video: 18.2 s at HT MCS 7, 10.8 s at VHT MCS 7 with two streams on
80 MHz, and at HT MCS 15 on 40 MHz the phone acknowledged nothing, for a
reason not found.

A rate the phone does not take means no connection at all, so the daemon
chooses (`radio/rate_ladder.h`). Each exchange starts at OWL's rate. It goes
up a level after four windows of 32 frames that were acknowledged, and back
down when half were given up on, as the card's release reports say
(`TxStats::reported` and `given_up` in the fork). A frame given up on
while the station had not been heard for 20 ms is not counted: the phone is
off the channel for part of every second. In the one run so far 191 frames
went at 12 Mbit/s, 223 at MCS 7 and 20,063 at the VHT rate, the rate never
went down, and the video took 11.2 s: 2.2 MB/s, with 1.3% of the segments
sent twice. Going down has only run in the test. Every frame is then still
its own transmission, where the iPhone sends a hundred in one.

The radio daemon asks for the agreement that allows more
(`radio/outgoing_session.h`): an ADDBA request once 16 data frames in a row
have gone to a station. The iPhone accepted every one. From then on data
frames at an HT or VHT rate go out as QoS data under numbers of the daemon's
own, and the fork marks them for the card to aggregate (`SetAmpduMode`: the
descriptor fields rtw89 sets, `AGG_EN`, `MAX_AGGNUM`, `AMPDU_DENSITY`, and
the frame's own number as the descriptor's).

The card aggregated at the first attempt, and the transfer stalled three
times all the same: each time the flow stopped, the iPhone ended the
agreement 1.2 s later, and after the third the daemon stopped asking. The
card gives up on the frames it holds when the phone leaves the channel, and
a receiver under block ack holds back everything behind a frame it misses.
Linux tells the receiver from the host, with a block-ack request that moves
its window; the daemon now does that when the card reports a frame given up
on. Sent once it changed nothing, three stalls again, since it goes out
while the phone is still away. It is now repeated each time the station is
heard again until one of its block acks starts at or after the number told
(`WindowNotice`).

With that, one agreement carried a whole 24.5 MB video: 18,729 frames,
6,568 block acks, 322 such notices, 5.2 s, 4.7 MB/s, with 1.3% of the TCP
segments sent twice. While the phone listened up to 1,568 frames went in
200 ms, about 11 MB/s. A block ack covered 2.6 frames on average, so the
aggregates were short, and 2,304 block acks covered no frame at all.

Both had one cause, in the notice itself. The card takes a block-ack
request for a frame that wants an acknowledgement, gets a block ack, and
repeats the request to its limit: eight times with growing pauses, about
8 ms, each answered by a block ack that covers nothing. It then reports the
request given up on, and the daemon read that as a lost data frame and sent
the next notice. After the first real loss this went round for the rest of
the transfer, 322 times in 5 s. The fork now sends a block-ack request once,
and the daemon expects that report. The next transfer had 44 notices, then
22, and no empty block acks.

It was no faster for that, 4.9 MB/s on the air, because the frames reach
the card too slowly. One register access through the proxy costs 31 us, and
devourer's send makes four for a frame: it reads whether the ring has room,
reads what the chip released, writes the ring's index and reads that the
chip fetched the frame. That is 8,000 frames a second at best, which is the
peak every capture until then shows, and it leaves the card about 4 frames
to build an aggregate from. With `tx_quick_submit` in the fork's transport
configuration a frame costs the one write: the ring's
room is read only once half of it may be in use, releases are left to the
receive loop, and nothing waits for the fetch.

With both, the same video took 2.3 s on the air and 2.5 s for the
sender, 9.9 MB/s: 18,828 frames under one agreement, 1,321 block acks of
13.9 frames on average, 6 notices, 0.8% of the TCP segments sent twice, and
up to 3,545 frames in 200 ms while the phone listened, about 25 MB/s. One
run each.

Both are now on unless `-NoAggregate` or `-NoQuickSubmit` says otherwise,
after one session of receiving with them: the daemon's own acknowledgements
to the phone then go out under an agreement as well, 1,812 frames of them.
A 151.5 MB video arrived in 9.7 s, 15.6 MB/s as before, at 23 to 25 MB/s
in its steady seconds, and a 28 MB one in 4.1 s.

The one write left for a frame was the next limit: 17,000 frames a second
at the peak, with 14 frames to an aggregate. The radio daemon now leaves
frames in the card's ring and writes the index once the AWDL daemon has no
more ready, or after 32 (`tx_kick` in the fork), and a frame crosses the
pipe between the two daemons in one system call a side, not two. That came
to 7 to 9 frames a write and 33 to 35 to a block ack.

The first long transfer that way showed two things. The rate went down for
the first time on the card, twice, and wrongly: a phone that leaves the
channel takes a whole aggregate or two with it, which made two windows in a
row bad, and each fall cost 2 s at 2 to 5 MB/s before the faster rate was
tried again. A level is now left when half the frames of eight windows
failed, and a window in which nothing got through is not counted: a rate
that is too fast still gets some frames across. And the AWDL daemon dropped
2,566 received frames unread, its queue of 256 full while it was sending;
the queue is 2,048 now.

After both, a 144.5 MB video went to the iPhone in 10.0 s, 14.5 MB/s,
16.3 MB/s on the air, without the rate going down, with 19 notices and
0.36% of the TCP segments sent twice. The same session sent the 24.5 MB one
at 9.3 MB/s, and the session before at 11.7. In a 200 ms span with the
phone on the channel throughout up to 5,502 frames went, about 40 MB/s; the
spans in which it was away for part of the time carry a third of that. What
the card holds when the phone leaves is still lost and sent again by TCP,
which then slows down for a while.

## The app

`app/` is a WPF program on .NET 10 with no package from outside the SDK, so
that building it downloads nothing. It is three processes at most.

The window runs without administrator rights. That is forced: Explorer runs
without them, and Windows does not let a program drop files on a window that
has more rights than it.

The receiver and the sender stay the Python they were. `airdrop/engine.py`
runs both in one process that the window starts and talks to over its
standard streams, one JSON object a line each way: events out, commands in.
The end of its input is its cue to leave, so it cannot outlive the window.
The receiver and the sender each gained a small interface for a program that
watches a transfer: the receiver asks it whether to accept, and tells it how
many bytes of the archive have come in, counted by the same pass that
already looked for the archive's end. The engine holds a sender's ask open
until the window answers, and notices a sender that stops waiting by its
connection becoming readable while it is owed an answer. With asking on, an
upload that follows no accepted ask is refused: without that, the question
could be skipped by not asking it. Declining answers status 401, which is
what this project's own sender takes as a no; what an iPhone shows for it has
not been tried.

A receiver that announces itself in AWDL frames says no goodbye when it
stops, so the engine drops one that has not been announced for 20 s. It
looks for receivers, and advertises over Bluetooth, only while the window
can be seen.

The radio daemon and the AWDL daemon need administrator rights. The switch
starts the app a second time with `--radio`, through the Windows prompt, and
that copy runs the two daemons as `awdl-run.ps1` does and waits for one of
four things: an event the window signals, the window's process ending, or
either daemon ending. It then interrupts the AWDL daemon with Ctrl+C, which
is the stop both daemons were written for, and the radio daemon if it has
not already left with its client. Two details make that work from a program
with no console. Each daemon is given a console of its own that is never
shown, because a process without one cannot be sent Ctrl+C. And the copy
first says that it does not ignore Ctrl+C itself: a process inherits that
from whatever started it, a daemon would inherit it in turn, and the
interrupt would be sent and ignored. Both daemons are also in a job that
ends them if the copy itself dies.

None of that needs a radio to try. `--check` runs the supervisor with `ping`
standing in for both daemons, and each state of the radio and of the engine
against the screen it should put up. `--drive` opens the real window off
screen, never activated, starts the real engine with `--loopback` and a
second one as the other device, and presses each control through its
automation peer, which is the interface a screen reader uses. 51 and 59
checks pass.

The first run with the radio was the owner's, on 2026-10-12, and what
follows is read from the logs it left. The switch started both daemons, and
the engine was receiving on `awdl0` three seconds later. A 0.2 MB file went
to the owner's iPhone, which accepted it and confirmed the upload. The
iPhone then asked to send a 71 kB picture, and it was stored 15 s later,
the time it took to press Accept in the window. Stopping interrupted the
AWDL daemon with Ctrl+C and the radio daemon left with its client; neither
had to be killed. Three things showed up:

- What the daemons print was being read in the console's code page and
  stored damaged wherever a line held a dash or a multiplication sign. It is
  read as UTF-8 now.
- While looking, one receiver nearby refused the TLS handshake with
  "certificate unknown" each time it was asked for its name, every 2 s, and
  the window never showed it. Such a receiver is now counted with those that
  answer without a name, the window says that a device nearby only answers
  devices it knows and to set its AirDrop to Everyone for 10 Minutes, and it
  is asked again every 10 s. That it is what a device visible to its contacts
  only does is likely and not established: nothing vouches for a certificate
  made here. The notice has not been seen with a phone yet.
- A folder listing shows a log's size as it was when the file was opened.
  The logs do fill as the daemons run.

The second run, on 2026-10-13, was of the installed copy: a Live Photo
received and a 0.7 MB file sent. The owner saw two things, both fixed and
neither tried on the air since:

- The iPhone was listed twice for a moment around the send. The window keeps
  one plate for each identifier the engine gives, and the engine gave a
  receiver's mDNS instance name. The AWDL daemon's log has one peer for the
  whole of that minute, so the phone was announced under two instance names
  at once, from one address; the announcements themselves were not recorded.
  A device that takes a new name says no goodbye for the old one, which then
  stays for the 20 s above, and for a whole transfer if one is running. The
  engine now names a receiver by its address, and of two instance names at
  one address keeps the one announced last. A device announced again during
  a transfer to it keeps its plate, which used to be replaced by a new one.
- The Live Photo was two things on the shelf, its still and its video. They
  are the two files it is stored as, and the window showed one for each
  file. The engine now says which stored files are one Live Photo, from the
  `.pvt` folder they came in and not from their names, so a photo and a video
  that only share a name stay two. The window shows the still, which says it
  is a Live Photo and can play its video. The request to send one, which
  names that folder, reads as a photo and no longer as a file.

Still untried through the window: a large transfer, and what a phone shows
when its request is declined.

### The installer

A driver signed by a certificate made on one PC is trusted only where that
certificate is installed, and nobody should install a stranger's. So the
programs are built once and the driver is signed where it is installed.
Three things make that possible without the Driver Kit on that PC:

- A catalog lists hashes, not a signer. `scripts\build-installer.ps1` stamps
  the INF and runs Inf2Cat on the build machine, and ships the catalog
  unsigned.
- `Set-AuthenticodeSignature`, which every Windows has, signs a catalog. The
  installer makes a code-signing certificate, adds it to the machine's
  trusted roots and trusted publishers, signs the catalog and the driver's
  DLL, and deletes the private key before it goes on. What the machine then
  trusts can sign nothing more. Its subject is not the one the from-source
  scripts use, so each leaves the other's certificate alone.
- The vendor's driver outranks ours, and `devcon update` is what forced the
  change. That is one call, `UpdateDriverForPlugAndPlayDevices` with the
  force flag, which the installer makes itself.

An installed copy has no repository around it. The app looks for the two
daemons beside itself before it looks in `build`. The daemons' output goes to
`C:\ProgramData\winawdl\logs`: the elevated copy and the window agree on that
place whoever each runs as, and the window cannot talk the elevated copy into
writing anywhere else. The app makes the certificate the engine presents,
which used to need OpenSSL. And the Bluetooth script is started with the
execution policy set aside for that one process, because a PC that never ran
a script of its own refuses script files.

What is shipped must not say who built it. GCC wrote the path of every source
file into the AWDL daemon, the linker wrote the path of the debug file into
the driver, and so did the C# compiler into the app, each with the name of
the account. All three are told to start those paths at the repository, and
the build of the installer reads every file it gathered and stops if the
name is in one.

Not bundled: the .NET Desktop Runtime 10, Python, and the Visual C++ runtime
the radio daemon links to. The installer checks for each and offers `winget`.
It also says when an elevated prompt lacks the right to lock pages in memory,
and does not grant it.

On 2026-10-12 the installer ran in full on the development PC, over a
from-source setup: files copied, certificate made and trusted, both files
signed, package added, card moved from the old package to the new one with
status OK, old package removed, rules, shortcut and uninstall entry written.
That run was not meant. A check that it refuses without administrator rights
was made from a prompt that turned out to have them, and it did what an
installer does. Its lesson is in CONTRIBUTING.md. What it proves is that the
catalog signed by PowerShell is accepted and that the forced change of driver
works. Not yet tried: the radio on the driver it bound, the uninstaller for
real, the `winget` path, and any other PC.

## Fallback: a small kernel driver

A KMDF driver of a few hundred lines would do all five services properly,
including a 32-bit common buffer. With Secure Boot on it must be signed through
Microsoft's hardware portal, which needs an EV certificate. The alternative is
turning Secure Boot off and enabling test signing, which is a system security
change for the owner to make, not this project.

## Telling the card from the Windows layer

Booting a Linux live USB and running devourer's `pcieprobe` against the card
shows whether devourer drives that card at all, apart from anything in the
Windows layer. It was not needed here: the Windows path booted the firmware
on the first attempt.

## Progress

| Step | State |
|---|---|
| Windows Driver Kit 10.0.26100 installed through winget | Done |
| `driver/regproxy`: UMDF 2 register proxy, built by `build.ps1` | Builds, INF passes `infverif` |
| Sign the driver package (`scripts/package-driver.ps1`) | Done, with a local non-exportable code-signing certificate |
| Trust the certificate and bind the driver (`scripts/install-driver.ps1`) | Done 2026-10-11, from an elevated prompt. `scripts/uninstall-driver.ps1` reverts |
| `tools/regproxy_test`: open the proxy, check the chip ID | Passed 2026-10-11: die id 0x52, cut 1, register space readable |
| Windows backend for devourer's `PcieTransport` (`upstream/devourer`, branch `winawdl/pcie-windows`) | Written, builds with MSVC. The rows below are its hardware tests |
| DMA under Kernel DMA Protection (`pcieprobe win fw`) | Passed 2026-10-11: firmware downloaded and booted on the first attempt |
| Receive frames in monitor mode (`scripts/rx-test.ps1`) | Passed 2026-10-11: 1140 frames on channel 6, first frame 981 ms after launch, clean shutdown |
| Transmit frames (`scripts/tx-test.ps1`) | Passed 2026-10-11: 500 submitted, 0 failed, 499 release reports with status done, while receiving 1364 frames |
| Hear AWDL devices (`radio/awdl_sniff`) | Passed 2026-10-11: the owner's iPhone heard on channel 6 (97 PSF, 50 MIF, 14 data frames in 40 s) and on channel 44 (38 MIF), same AWDL address on both |
| Hardware ACK of unicast frames addressed to us | Found 2026-10-11 by sweep: 0% repeated frames once responses stop waiting for Bluetooth, against 85% before. Now applied by the radio daemon in every session: one such session received 41,968 data frames, 4% of them repeats |
| Radio daemon (`radio/winawdl_radio`) serving frames over a named pipe | Passed 2026-10-11: 827 frames transmitted for its client, none failed |
| AWDL daemon (`awdl/`, OWL core on Windows) | Passed on air 2026-10-11 on channel 6: added the owner's iPhone as a peer, elected it sync master, received its action and data frames while transmitting 24 action frames per second |
| Host network interface for AWDL data (Wintun, `awdl/win/host_if.c`) | Passed 2026-10-11: `awdl0` came up with the MAC-derived link-local address as its only one, and the owner's iPhone answered 6 of 8 pings over AWDL. Round trips took 0.5 to 0.9 s and the first ping of each run was lost |
| AirDrop receiver (`airdrop/`, adapted from omdrop) | First file received 2026-10-11: the owner's iPhone found the PC in its AirDrop sheet and one 280 kB photo arrived intact. Three other attempts failed, and all ran at about 10 kB/s. With frames acknowledged, photos, a 28 MB video and a Live Photo arrived at 300 to 430 kB/s on channel 6, and 30.5 MB arrived at 971 kB/s on channel 44, 1,710 kB/s with block ack, and 6.7 MB/s with joined segments and two streams |
| AirDrop sender (`airdrop/sender.py`) | First file sent 2026-10-11: a 1.0 MB photo reached the owner's iPhone's Photos, after two attempts that failed at different steps. A 24.5 MB video then went at 2.2 MB/s with the rate chosen by the radio daemon, then at 9.9 MB/s in aggregates, and a 144.5 MB one at 14.5 MB/s |
| The Windows app (`app/`, `airdrop/engine.py`) | Written 2026-10-12. Its window has sent and received files over loopback against a second engine, 96 MB each way intact. On air the same day: its switch started and stopped both daemons, a 0.2 MB file went to the owner's iPhone and a 71 kB picture came back after Accept in the window |

The INF matches only the generic `PCI\VEN_10EC&DEV_C852` ID, so adding the
package to the driver store does not take over the card on its own.

## Practical notes

- devourer and OWL are submodules under `upstream/`. A reference clone of
  omdrop-plugin may sit beside them; it is not tracked.
- Installed: Visual Studio Build Tools 2019 and 2022, Windows SDK and WDK
  10.0.26100, CMake, Ninja.
- Build scripts do not rely on `vcvars64.bat`. They call `cl.exe` with
  explicit `INCLUDE` and `LIB`.
- OWL is GPL-3.0 and devourer GPL-2.0. Keep them in separate processes.

## Physical memory for DMA

The radio needs six free 2 MiB blocks of physical memory. They used to have
to lie below 4 GiB, and on 2026-10-11, after 13 days of uptime, Windows had
one left there and the radio refused to start. A probe in that state found
61 such blocks below 64 GiB, and only 3 usable 16 KiB runs of ordinary
pages below 4 GiB against 768 needed, so smaller pieces were no way out.
The DMA plane now writes 36-bit addresses, as rtw89 does on the 8852C
without any enabling step: the high bits go into each buffer descriptor,
each WD-page address entry and the ring base registers. Low blocks are
still preferred. `radio/tests/dma_address_test` checks the encodings and
the block choice.

On hardware, 2026-10-11, with every block above 4 GiB (between 15 and
22 GiB): firmware downloaded and booted first try, 33,120
packets were received in an AWDL session, and 500 broadcast frames were all
reported sent. But in three AWDL sessions run that way the iPhone answered
nothing, against four out of four the day before from low memory. The one
address field those sessions used that the other checks do not prove is
the address entry of a WD page, which points at the frame payload. With
the payload area laid out first and required below 4 GiB, the iPhone
answered again at once. So the radio needs one low block, not six, and the
chip does not appear to honour the high bits of that entry as rtw89 writes
them.

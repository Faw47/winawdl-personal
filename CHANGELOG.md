# Changelog

What each tagged state of the project holds. The measurements behind the
numbers are in [DESIGN-NOTES.md](DESIGN-NOTES.md).

## 0.1.0, 2026-10-12

The first tagged state. All of it has run on one machine, with one RTL8852CE,
against one iPhone.

- **Driver.** A user-mode (UMDF 2) driver that takes the card from the vendor
  driver and relays register accesses, built and signed on the machine that
  uses it.
- **Radio.** devourer drives the chip from an ordinary program over a new
  Windows PCIe backend: firmware boot, monitor-mode receive and transmit,
  hardware acknowledgement, block ack in both directions, 80 MHz.
- **AWDL.** OWL's protocol core with a Windows I/O layer and a Wintun
  adapter, `awdl0`, that carries IPv6 to and from nearby Apple devices.
- **Receiving AirDrop.** Photos, videos and Live Photos from an iPhone, at
  about 16 MB/s.
- **Sending AirDrop.** Files into an iPhone's Photos at 9 to 14 MB/s, with
  the Bluetooth advertisement that wakes the receiver.
- **The app.** One window over both, which also starts and stops the two
  daemons. Tried over the air for starting and stopping the radio and one
  small file each way.

Not in it: Contacts Only in either direction, any other card, and a driver
signed for machines other than the one that built it.

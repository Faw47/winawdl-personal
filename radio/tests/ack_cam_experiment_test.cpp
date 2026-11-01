// SPDX-License-Identifier: GPL-2.0-only
#include <array>
#include <cstdint>
#include <cstdio>
#include <map>
#include "../ack_cam_experiment.h"

int main() {
  using namespace ack_cam_experiment;
  constexpr Mac own{0x02, 0x12, 0x34, 0x56, 0x78, 0x9a};
  constexpr Mac bssid{0x02, 0, 0, 0, 0, 1};
  const auto entry = encode(own, bssid);
  if ((entry[0] & 0x7u) != 5 || entry[6] != (uint32_t{Slot} | (1u << 14))
      || entry[2] != 0x56341202u || entry[1] != 1u) return 1;
  std::map<uint32_t, uint32_t> mem;
  int writes = 0;
  auto write = [&](uint32_t addr, uint32_t value) {
    ++writes;
    mem[addr] = value;
    return true;
  };
  auto read = [&](uint32_t addr) { return mem[addr]; };
  if (!arm(write, read, own, bssid) || writes != 9
      || mem[CamAddress] != entry[0]) return 2;
  if (!clear(write) || mem[CamAddress] != 0) return 3;
  Mac multicast = own; multicast[0] = 0x01;
  if (arm(write, read, multicast, bssid)) return 4;
  auto failed_write = [&](uint32_t addr, uint32_t value) {
    if (addr == CamAddress + 8) return false;
    mem[addr] = value;
    return true;
  };
  if (arm(failed_write, read, own, bssid) || mem[CamAddress] != 0) return 5;
  auto stale_read = [&](uint32_t addr) { return mem[addr] ^ 1; };
  if (arm(write, stale_read, own, bssid) || mem[CamAddress] != 0) return 6;
  std::puts("CAM63 encoding, ordered writes, readback and rollback passed");
  return 0;
}

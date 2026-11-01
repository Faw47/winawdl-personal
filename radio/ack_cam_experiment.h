// SPDX-License-Identifier: GPL-2.0-only
#pragma once
// Optional RTL8852C host-written ADDR_CAM slot 63 experiment.
// From OpenIPC/devourer's KestrelAddrCam.h encoder and indirect SRAM write
// sequence. Never used without --experimental-ack-cam63.
#include <array>
#include <cstdint>

namespace ack_cam_experiment {

using Mac = std::array<uint8_t, 6>;
constexpr uint8_t Slot = 63;
constexpr uint32_t CamAddress = 0x18850000u + Slot * 0x20u;
constexpr uint16_t Selector = 0x0C04;
constexpr uint32_t Window = 0x40000u;

inline std::array<uint32_t, 8> encode(const Mac &own, const Mac &bssid) {
  uint32_t sh = 0, th = 0;
  for (size_t i = 0; i < 6; ++i) {
    sh ^= own[i];
    th ^= bssid[i];
  }
  const auto part = [](const Mac &mac, int index, int shift) {
    return uint32_t{mac[index]} << shift;
  };
  // VALID, INFRA type 2, no mask, SMA/TMA hash, BSSID CAM 1, macid 63.
  return {0x1u | (2u << 1) | (sh << 16) | (th << 24),
          1u,
          part(own, 0, 0) | part(own, 1, 8) | part(own, 2, 16) | part(own, 3, 24),
          part(own, 4, 0) | part(own, 5, 8) | part(bssid, 0, 16) | part(bssid, 1, 24),
          part(bssid, 2, 0) | part(bssid, 3, 8) | part(bssid, 4, 16) | part(bssid, 5, 24),
          0u, uint32_t{Slot} | (1u << 14), 0u};
}

template <class Write>
bool clear(Write write) { return write(CamAddress, 0u); }

template <class Write, class Read>
bool arm(Write write, Read read, const Mac &own, const Mac &bssid) {
  if ((own[0] & 1u) != 0) return false; // never arm a multicast identity
  const auto words = encode(own, bssid);
  if (!clear(write)) return false; // VALID off before touching any other word
  for (int i = 7; i >= 1; --i) {
    if (!write(CamAddress + 4u * static_cast<uint32_t>(i), words[i])) {
      clear(write);
      return false;
    }
  }
  if (!write(CamAddress, words[0])) { clear(write); return false; }
  for (uint32_t i = 0; i < words.size(); ++i) {
    // On RTL8852C dword 5 reads back 0xdeadbeef, even if the write worked.
    if (i != 5 && read(CamAddress + 4u * i) != words[i]) {
      clear(write);
      return false;
    }
  }
  return true;
}

} // namespace ack_cam_experiment

// SPDX-License-Identifier: GPL-2.0-only
/* The two places a wrong bus address would come from, checked without
 * hardware: how the DMA blocks are chosen and how an address is written into
 * the descriptors the chip reads. Expected bytes follow rtw89's pci.h. */
#include <cstdio>
#include <cstring>
#include <vector>

#include "PcieDmaAxDesc.h"
#include "PhysicalBlocks.h"

namespace {

using devourer::take_lowest_blocks;
namespace ax = devourer::pcie_ax;

constexpr uintptr_t kBlockFrames = 512;
constexpr uintptr_t kFrameAt4GiB = uintptr_t{1} << 20;
constexpr uintptr_t kFrameAt64GiB = uintptr_t{1} << 24;
/* The blocks one fragmented machine offered: one below 4 GiB, the rest above. */
const std::vector<uintptr_t> kOffered = {0x35bc00, 0x104c00, 0x33000,
                                         0x21d800, 0x106c00, 0x30f000};
constexpr uint16_t kLastSegment = 1u << 14;

int failures = 0;

void expect(bool condition, const char *what) {
  std::printf("%s  %s\n", condition ? "ok  " : "FAIL", what);
  if (!condition)
    failures++;
}

template <size_t N>
bool same_bytes(const uint8_t *actual, const uint8_t (&expected)[N]) {
  return std::memcmp(actual, expected, N) == 0;
}

void check_block_choice() {
  std::vector<uintptr_t> offered = kOffered;
  expect(take_lowest_blocks(offered, 2, kBlockFrames, kFrameAt4GiB).empty() &&
             offered.size() == kOffered.size(),
         "too few blocks under the limit takes none");

  const std::vector<uintptr_t> taken =
      take_lowest_blocks(offered, 3, kBlockFrames, kFrameAt64GiB);
  expect(taken == std::vector<uintptr_t>({0x33000, 0x104c00, 0x106c00}),
         "the lowest blocks are taken, in ascending order");
  expect(offered == std::vector<uintptr_t>({0x21d800, 0x30f000, 0x35bc00}),
         "the others are left to be freed");

  std::vector<uintptr_t> mixed = kOffered;
  expect(take_lowest_blocks(mixed, 3, kBlockFrames, kFrameAt64GiB, 1,
                            kFrameAt4GiB)
                 .front() == 0x33000,
         "one block required low is the first one");
  mixed = kOffered;
  expect(take_lowest_blocks(mixed, 3, kBlockFrames, kFrameAt64GiB, 2,
                            kFrameAt4GiB)
                     .empty() &&
             mixed.size() == kOffered.size(),
         "two required low, with one on offer, takes none");

  std::vector<uintptr_t> at_limit = {kFrameAt4GiB - kBlockFrames,
                                     kFrameAt4GiB - kBlockFrames + 1};
  expect(take_lowest_blocks(at_limit, 1, kBlockFrames, kFrameAt4GiB).size() == 1 &&
             take_lowest_blocks(at_limit, 1, kBlockFrames, kFrameAt4GiB).empty(),
         "a block may end at the limit but not cross it");
}

void check_buffer_descriptor() {
  uint8_t bd[8] = {};
  ax::bd_write(bd, 0x2CE8, 0, 0x12345678);
  const uint8_t low[] = {0xE8, 0x2C, 0x00, 0x00, 0x78, 0x56, 0x34, 0x12};
  expect(same_bytes(bd, low), "an address below 4 GiB leaves the option word");

  ax::bd_write(bd, 0x0080, kLastSegment, 0x9'1234'5678);
  const uint8_t high[] = {0x80, 0x00, 0x40, 0x42, 0x78, 0x56, 0x34, 0x12};
  expect(same_bytes(bd, high),
         "bits 35:32 go to option bits 9:6 beside the last-segment flag");

  ax::bd_write(bd, 0, 0, ax::kAddressLimit - 0x1000);
  const uint8_t top[] = {0x00, 0x00, 0xC0, 0x03, 0x00, 0xF0, 0xFF, 0xFF};
  expect(same_bytes(bd, top), "the highest address keeps all four high bits");
}

void check_address_entry() {
  uint8_t entry[6] = {};
  ax::addr_info_write(entry, 1500, /*last=*/false, 0x0ABC'D000);
  const uint8_t low[] = {0xDC, 0x05, 0x00, 0xD0, 0xBC, 0x0A};
  expect(same_bytes(entry, low), "a low address entry is length then address");

  ax::addr_info_write(entry, 2044, /*last=*/true, 0x9'1234'5678);
  const uint8_t high[] = {0xFC, 0xCF, 0x78, 0x56, 0x34, 0x12};
  expect(same_bytes(entry, high),
         "bits 35:32 go to bits 14:11 beside the length and last flag");
}

} /* namespace */

int main() {
  check_block_choice();
  check_buffer_descriptor();
  check_address_entry();
  return failures ? 1 : 0;
}

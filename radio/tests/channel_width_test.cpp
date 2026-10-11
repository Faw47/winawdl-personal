// SPDX-License-Identifier: GPL-2.0-only
/* Checks, without hardware, where devourer tells the MAC a narrow frame sits
 * in a wide channel. The expected codes are rtw89_phy_get_txsc's. */
#include <cstdio>

#include "kestrel/TxSubChannel.h"

namespace {

int failures = 0;

void expect(bool condition, const char *what) {
  std::printf("%s  %s\n", condition ? "ok  " : "FAIL", what);
  if (!condition) failures++;
}

} // namespace

int main() {
  using kestrel::tx_sub_channel;
  using kestrel::tx_sub_channels;

  expect(tx_sub_channel(44, 44, 20, 20) == 0 &&
             tx_sub_channels(44, 44, 20) == 0,
         "a 20 MHz channel needs no code");
  expect(tx_sub_channel(44, 46, 40, 20) == 2 &&
             tx_sub_channel(48, 46, 40, 20) == 1,
         "in 40 MHz the primary is below or above the centre");
  expect(tx_sub_channel(44, 46, 40, 40) == 0,
         "a frame as wide as the channel needs none");
  expect(tx_sub_channel(36, 42, 80, 20) == 4 &&
             tx_sub_channel(40, 42, 80, 20) == 2 &&
             tx_sub_channel(44, 42, 80, 20) == 1 &&
             tx_sub_channel(48, 42, 80, 20) == 3,
         "in 80 MHz each of the four positions has its code");
  expect(tx_sub_channel(44, 42, 80, 40) == 9 &&
             tx_sub_channel(36, 42, 80, 40) == 10,
         "a 40 MHz frame in 80 MHz sits in the upper or lower half");
  expect(tx_sub_channels(44, 46, 40) == 0x02 &&
             tx_sub_channels(44, 42, 80) == 0x91 &&
             tx_sub_channels(149, 151, 40) == 0x02,
         "the register carries the 20 MHz code, then the 40 MHz one");

  return failures ? 1 : 0;
}

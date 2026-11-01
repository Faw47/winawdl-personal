/* SPDX-License-Identifier: GPL-3.0-or-later */
/* PSFs advertise HT Capabilities TLV 7 like MIFs (Owly AWDL-5). */
#include <stdio.h>
#include <stdint.h>
#include <string.h>
#include "state.h"
#include "tx.h"
#include "peers.h"
#include "channel.h"

static int has_ht(const uint8_t *frame, int length) {
  int pos = (int)frame[2] + ((int)frame[3] << 8) + 24 + 16;
  while (pos + 3 <= length) {
    int type = frame[pos];
    int bytes = frame[pos + 1] | (frame[pos + 2] << 8);
    if (bytes > length - pos - 3) return 0;
    if (type == 7 && bytes >= 8) return 1;
    pos += 3 + bytes;
  }
  return 0;
}

int main(void) {
  struct awdl_state state = {0};
  struct ieee80211_state ieee = {0};
  const struct ether_addr own = {{0x02, 0x11, 0x22, 0x33, 0x44, 0x55}};
  awdl_init_state(&state, "winawdl", &own, CHAN_OPCLASS_44, clock_time_us());
  ieee80211_init_state(&ieee);
  uint8_t frame[4096] = {0};
  int psf = awdl_init_full_action_frame(frame, &state, &ieee, AWDL_ACTION_PSF);
  int ok = psf > 0 && psf <= (int)sizeof(frame) && has_ht(frame, psf);
  int mif = awdl_init_full_action_frame(frame, &state, &ieee, AWDL_ACTION_MIF);
  ok = ok && mif > 0 && mif <= (int)sizeof(frame) && has_ht(frame, mif);
  awdl_peers_free(state.peers.peers);
  printf("%s PSF and MIF both contain HT Capabilities TLV 7\n", ok ? "ok" : "FAIL");
  return ok ? 0 : 1;
}

/* SPDX-License-Identifier: GPL-3.0-or-later */
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include "rx.h"
#include "state.h"
#include "peers.h"
#include "wire.h"
#include "log.h"

static struct awdl_state state;
static int ready;

/* A radiotap header with the flags field, as the radio daemon writes it,
 * then the fuzzed 802.11 frame. State is kept from one input to the next,
 * as it is between frames on the air. */
int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size) {
	if (!ready) {
		static const struct ether_addr self = {{2, 0, 0, 0, 0, 1}};
		log_set_level(LOG_EMERG); log_set_quiet(1);
		awdl_init_state(&state, "fuzz", &self, CHAN_OPCLASS_149, clock_time_us());
		state.filter_rssi = 0;
		/* A peer already heard, so its data frames are parsed too. */
		static const struct ether_addr peer = {{2, 0, 0, 0, 0, 2}};
		awdl_peer_add(state.peers.peers, &peer, clock_time_us(), NULL, NULL);
		ready = 1;
	}
	if (size > 4096) return 0;
	uint8_t *wire = malloc(9 + size);
	memset(wire, 0, 9);
	wire[2] = 9;
	wire[4] = 0x02;
	memcpy(wire + 9, data, size);
	const struct buf *frame = buf_new_const(wire, (int) (9 + size));
	struct buf *out[AWDL_MAX_AMSDU_SUBFRAMES];
	struct buf **cursor = out;
	int result = awdl_rx(frame, &cursor, &state);
	if (cursor - out > AWDL_MAX_AMSDU_SUBFRAMES) abort();
	if (result != RX_OK && cursor != out) abort(); /* frames left behind leak */
	for (struct buf **p = out; p < cursor; p++) buf_free(*p);
	buf_free(frame);
	free(wire);
	return 0;
}

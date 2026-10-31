/* SPDX-License-Identifier: GPL-3.0-or-later */
/* Regression for patches/0003: a data frame from a known peer keeps its entry
 * from expiring, as an action frame does. One from a stranger adds nothing. */
#include <stdio.h>
#include <string.h>

#include "rx.h"
#include "peers.h"
#include "wire.h"

static const struct ether_addr self = {{2, 0, 0, 0, 0, 1}};
static const struct ether_addr known = {{2, 0, 0, 0, 0, 2}};
static const struct ether_addr stranger = {{2, 0, 0, 0, 0, 3}};
static int failures = 0;

static void check(int condition, const char *label) {
	printf("%s  %s\n", condition ? "ok  " : "FAIL", label);
	if (!condition)
		++failures;
}

/* An empty radiotap header, then a plain data frame from `from` to us, cut
 * off after its MAC header: what follows does not matter here. */
static void receive_data(struct awdl_state *state, const struct ether_addr *from) {
	uint8_t wire[8 + 24] = {0};
	struct buf *out[AWDL_MAX_AMSDU_SUBFRAMES];
	struct buf **cursor = out;
	wire[2] = 8; /* radiotap length */
	wire[8] = 0x08; /* frame control: data */
	memcpy(wire + 8 + 4, &self, 6);
	memcpy(wire + 8 + 10, from, 6);
	const struct buf *frame = buf_new_const(wire, sizeof(wire));
	awdl_rx(frame, &cursor, state);
	buf_free(frame);
	for (struct buf **p = out; p < cursor; p++)
		buf_free(*p);
}

int main(void) {
	struct awdl_state state;
	struct awdl_peer *peer = NULL;
	memset(&state, 0, sizeof(state));
	state.self_address = self;
	state.peers.peers = awdl_peers_init();
	awdl_peer_add(state.peers.peers, &known, 0, NULL, NULL);

	receive_data(&state, &known);
	awdl_peer_get(state.peers.peers, &known, &peer);
	check(peer && peer->last_update > 0, "a known peer's data frame counts as hearing it");
	awdl_peers_remove(state.peers.peers, 1, NULL, NULL);
	check(awdl_peer_get(state.peers.peers, &known, NULL) == PEERS_OK,
	      "so it survives an expiry that would have dropped it");

	receive_data(&state, &stranger);
	check(awdl_peer_get(state.peers.peers, &stranger, NULL) != PEERS_OK,
	      "a stranger's data frame adds no peer");

	awdl_peers_free(state.peers.peers);
	return failures ? 1 : 0;
}

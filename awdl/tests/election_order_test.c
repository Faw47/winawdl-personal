/* SPDX-License-Identifier: GPL-3.0-or-later */
/* Regression for patches/0002: which of two masters a node follows, ranked by
 * counter first as OWL does and by metric first as Apple devices do. The two
 * masters are the ones Owly saw an Apple device choose between. */
#include <stdio.h>

#include "election.h"
#include "peers.h"

static const struct ether_addr self = {{2, 0, 0, 0, 0, 1}};
static const struct ether_addr old_master = {{2, 0, 0, 0, 0, 2}};
static const struct ether_addr new_master = {{2, 0, 0, 0, 0, 3}};
static int failures = 0;

static void check(int condition, const char *label) {
	printf("%s  %s\n", condition ? "ok  " : "FAIL", label);
	if (!condition)
		++failures;
}

static void add_master(struct awdl_peer_state *peers, const struct ether_addr *addr,
		       uint32_t metric, uint32_t counter) {
	struct awdl_peer *peer;
	awdl_peer_add(peers->peers, addr, 0, NULL, NULL);
	awdl_peer_get(peers->peers, addr, &peer);
	peer->is_valid = 1;
	peer->election.height = 0;
	peer->election.master_addr = *addr;
	peer->election.sync_addr = *addr;
	peer->election.master_metric = metric;
	peer->election.master_counter = counter;
}

static struct ether_addr followed(int metric_first) {
	struct awdl_peer_state peers = {0};
	struct awdl_election_state state;
	peers.peers = awdl_peers_init();
	add_master(&peers, &old_master, 510, 3214);
	add_master(&peers, &new_master, 522, 393);
	awdl_election_state_init(&state, &self);
	state.metric_first = (uint8_t) metric_first;
	awdl_election_run(&state, &peers);
	awdl_peers_free(peers.peers);
	return state.sync_addr;
}

int main(void) {
	struct ether_addr by_counter = followed(0);
	struct ether_addr by_metric = followed(1);
	check(!compare_ether_addr(&by_counter, &old_master),
	      "counter first: the master with counter 3214 is followed");
	check(!compare_ether_addr(&by_metric, &new_master),
	      "metric first: the master with metric 522 is followed");
	return failures ? 1 : 0;
}

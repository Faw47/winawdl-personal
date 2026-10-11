/* SPDX-License-Identifier: GPL-3.0-or-later */
/* AWDL daemon for Windows: OWL's protocol core, driven by the radio daemon.
 * Usage: awdld [-c 6|44|149] [-n] [-v] [-v], -n meaning no host adapter. */
#include <getopt.h>
#include <stdlib.h>

#include "core.h"
#include "host_if.h"
#include "log.h"
#include "radio_client.h"
#include "win_internal.h"

#define STATUS_PERIOD_S 2.0
#define DEFAULT_CHANNEL 6

static int channel_from_number(int number, struct awdl_chan *chan) {
	switch (number) {
		case 6:
			*chan = CHAN_OPCLASS_6;
			return 0;
		case 44:
			*chan = CHAN_OPCLASS_44;
			return 0;
		case 149:
			*chan = CHAN_OPCLASS_149;
			return 0;
		default:
			return -1;
	}
}

static void print_status(struct ev_loop *loop, ev_timer *timer, int revents) {
	(void) loop;
	(void) revents;
	struct daemon_state *state = timer->data;
	const struct awdl_stats *stats = &state->awdl_state.stats;
	char tree[256] = "";
	awdl_election_tree_print(&state->awdl_state.election, tree, sizeof(tree));
	log_info("peers %d | rx action %llu, data %llu | tx action %llu, data %llu | "
		 "sync tree %s",
		 awdl_peers_length(state->awdl_state.peers.peers), stats->rx_action, stats->rx_data,
		 stats->tx_action, stats->tx_data, tree);
}

int main(int argc, char *argv[]) {
	int channel = DEFAULT_CHANNEL;
	int log_level = LOG_INFO;
	const char *host_name = HOST_IF_NAME;
	int option;
	while ((option = getopt(argc, argv, "c:nv")) != -1) {
		if (option == 'c') {
			channel = atoi(optarg);
		} else if (option == 'n') {
			host_name = "";
		} else if (option == 'v') {
			if (log_level < LOG_TRACE)
				log_level++;
		} else {
			fprintf(stderr, "usage: %s [-c 6|44|149] [-n] [-v] [-v]\n", argv[0]);
			return EXIT_FAILURE;
		}
	}
	log_set_level(log_level);

	struct awdl_chan chan;
	if (channel_from_number(channel, &chan) < 0) {
		log_error("unsupported channel %d (use 6, 44 or 149)", channel);
		return EXIT_FAILURE;
	}

	static struct daemon_state state;
	if (awdl_init(&state, "radio", host_name, chan, NULL) < 0) {
		log_error("could not initialize: is winawdl_radio running?");
		return EXIT_FAILURE;
	}
	if (radio_client_channel(winawdl_radio()) != channel) {
		log_error("the radio daemon is on channel %d, not %d",
			  radio_client_channel(winawdl_radio()), channel);
		awdl_free(&state);
		return EXIT_FAILURE;
	}
	/* The radio's RSSI scale is not calibrated, so do not gate peers on it. */
	state.awdl_state.filter_rssi = 0;
	log_info("AWDL address %s on channel %d", ether_ntoa(&state.io.if_ether_addr), channel);
	if (state.io.host_ifindex)
		log_info("host interface %s, index %d", state.io.host_ifname,
			 state.io.host_ifindex);

	struct ev_loop *loop = EV_DEFAULT;
	awdl_schedule(loop, &state);

	ev_timer status;
	status.data = &state;
	ev_timer_init(&status, print_status, STATUS_PERIOD_S, STATUS_PERIOD_S);
	ev_timer_start(loop, &status);

	ev_run(loop, 0);

	awdl_free(&state);
	return EXIT_SUCCESS;
}

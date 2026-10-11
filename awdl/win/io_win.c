/* SPDX-License-Identifier: GPL-3.0-or-later */
/* Windows replacement for OWL's daemon/io.c. The "WLAN device" is the radio
 * daemon reached over its pipe, and the host device is a Wintun adapter. */
#include "io.h"

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <bcrypt.h>
#include <errno.h>
#include <string.h>

#include <ev.h>

#include "ack_thin.h"
#include "core.h"
#include "frame_fixup.h"
#include "host_frame.h"
#include "host_if.h"
#include "log.h"
#include "netutils.h"
#include "radio_client.h"
#include "radio_link.h"
#include "win_internal.h"

/* core.c hands io_state.wlan_handle to pcap_dispatch, so that is where the
 * radio connection lives. */
struct pcap {
	struct radio_client *radio;
};

static struct pcap wlan;
/* NULL when the adapter could not be created; data frames are then dropped. */
static struct host_if *host;

struct radio_client *winawdl_radio(void) {
	return wlan.radio;
}

/* However busy the radio stays, an acknowledgement waits no longer. */
#define ACK_MAX_HOLD_US 4000

/* One TCP acknowledgement from the host, kept back while the frames of an
 * aggregate are still coming in, so that a later one can take its place. */
static struct {
	int quiet_us; /* 0: acknowledgements are not held */
	uint8_t packet[HOST_IF_MAX_PACKET];
	int length;
	uint64_t since_us;
	uint64_t last_frame_us; /* when the radio last delivered a frame */
	unsigned long long replaced;
} ack_hold;
/* A packet read from the host that has to follow the held acknowledgement. */
static uint8_t read_ahead[HOST_IF_MAX_PACKET];
static int read_ahead_len;

void winawdl_hold_acks(int quiet_us) {
	ack_hold.quiet_us = quiet_us;
}

unsigned long long winawdl_acks_replaced(void) {
	return ack_hold.replaced;
}

static uint64_t now_us(void) {
	static LARGE_INTEGER frequency;
	LARGE_INTEGER ticks;
	if (frequency.QuadPart == 0)
		QueryPerformanceFrequency(&frequency);
	QueryPerformanceCounter(&ticks);
	return (uint64_t) (ticks.QuadPart / frequency.QuadPart) * 1000000u +
	       (uint64_t) (ticks.QuadPart % frequency.QuadPart) * 1000000u /
		       (uint64_t) frequency.QuadPart;
}

static uint64_t ack_due_us(void) {
	uint64_t quiet = ack_hold.last_frame_us + (uint64_t) ack_hold.quiet_us;
	uint64_t latest = ack_hold.since_us + ACK_MAX_HOLD_US;
	return quiet < latest ? quiet : latest;
}

/* Spins until the radio has a frame or the held acknowledgement is due, and
 * returns which: no timer on Windows is this fine. */
static int frame_before_ack_is_due(void) {
	for (;;) {
		if (radio_client_has_frame(wlan.radio))
			return 1;
		if (now_us() >= ack_due_us())
			return 0;
		YieldProcessor();
	}
}

static int take_held_ack(uint8_t *packet) {
	int length = ack_hold.length;
	memcpy(packet, ack_hold.packet, (size_t) length);
	ack_hold.length = 0;
	return length;
}

/* The next packet to send for the host: 0 when there is none yet. */
static int next_host_packet(uint8_t *packet) {
	for (;;) {
		if (ack_hold.length && now_us() >= ack_due_us())
			return take_held_ack(packet);
		int length = read_ahead_len;
		if (length) {
			memcpy(packet, read_ahead, (size_t) length);
			read_ahead_len = 0;
		} else {
			length = host_if_read(host, packet);
		}
		if (length == 0) {
			if (!ack_hold.length || frame_before_ack_is_due())
				return 0;
			continue;
		}
		if (!ack_hold.quiet_us)
			return length;
		if (ack_hold.length) {
			if (ack_thin_supersedes(packet, length, ack_hold.packet, ack_hold.length)) {
				memcpy(ack_hold.packet, packet, (size_t) length);
				ack_hold.length = length;
				ack_hold.replaced++;
				continue;
			}
			/* Nothing may overtake the acknowledgement held before it. */
			memcpy(read_ahead, packet, (size_t) length);
			read_ahead_len = length;
			return take_held_ack(packet);
		}
		uint64_t now = now_us();
		if (!ack_thin_is_bare(packet, length) ||
		    now - ack_hold.last_frame_us >= (uint64_t) ack_hold.quiet_us)
			return length;
		memcpy(ack_hold.packet, packet, (size_t) length);
		ack_hold.length = length;
		ack_hold.since_us = now;
	}
}

static void on_radio_disconnect(void) {
	ev_win_stop(ev_default_loop());
}

/* A fresh locally administered unicast address per run, as Apple devices use
 * for AWDL. */
static int random_ether_addr(struct ether_addr *addr) {
	if (BCryptGenRandom(NULL, addr->ether_addr_octet, ETHER_ADDR_LEN,
			    BCRYPT_USE_SYSTEM_PREFERRED_RNG) != 0)
		return -EIO;
	addr->ether_addr_octet[0] = (uint8_t) ((addr->ether_addr_octet[0] | 0x02) & 0xFE);
	return 0;
}

static void open_host_interface(struct io_state *state) {
	struct in6_addr address;
	char error[128];
	rfc4291_addr(&state->if_ether_addr, &address);
	host = host_if_open(address.s6_addr, error, sizeof(error));
	if (!host) {
		log_warn("no host interface, AWDL data will be dropped: %s", error);
		return;
	}
	state->host_fd = ev_win_fd_for_handle(host_if_read_event(host));
	state->host_ifindex = (int) host_if_index(host);
	strncpy(state->host_ifname, HOST_IF_NAME, sizeof(state->host_ifname) - 1);
}

int io_state_init(struct io_state *state, const char *wlan_name, const char *host_name,
		  const struct ether_addr *bssid_filter) {
	memset(state, 0, sizeof(*state));

	wlan.radio = radio_client_open(on_radio_disconnect);
	if (!wlan.radio)
		return -ENODEV;
	state->wlan_handle = &wlan;
	state->wlan_fd = ev_win_fd_for_handle(radio_client_rx_event(wlan.radio));
	state->wlan_ifindex = 1;
	strncpy(state->wlan_ifname, wlan_name, sizeof(state->wlan_ifname) - 1);

	if (radio_client_send(wlan.radio, RADIO_LINK_SET_BSSID_FILTER,
			      bssid_filter->ether_addr_octet, ETHER_ADDR_LEN) < 0)
		return -EIO;
	if (random_ether_addr(&state->if_ether_addr) < 0 ||
	    radio_client_send(wlan.radio, RADIO_LINK_SET_ADDRESS,
			      state->if_ether_addr.ether_addr_octet, ETHER_ADDR_LEN) < 0)
		return -EIO;
	if (host_name[0])
		open_host_interface(state);
	return 0;
}

void io_state_free(struct io_state *state) {
	host_if_close(host);
	host = NULL;
	radio_client_close(wlan.radio);
	wlan.radio = NULL;
	state->wlan_handle = NULL;
}

int pcap_dispatch(pcap_t *handle, int count, pcap_handler callback, unsigned char *user) {
	(void) count;
	static uint8_t frame[RADIO_CLIENT_MAX_FRAME];
	int length = radio_client_pop(handle->radio, frame);
	if (length == 0 && ack_hold.length) {
		/* The burst is over. A held acknowledgement waits a little for
		 * the next frame, then the host side is run to send it. */
		if (frame_before_ack_is_due())
			length = radio_client_pop(handle->radio, frame);
		else
			ev_feed_event(ev_default_loop(),
				      &((struct daemon_state *) user)->ev_state.read_host, EV_READ);
	}
	if (length == 0)
		return 0;
	ack_hold.last_frame_us = now_us();
	length = frame_fixup_received(frame, length);
	struct pcap_pkthdr header = {.caplen = (uint32_t) length, .len = (uint32_t) length};
	callback(user, &header, frame);
	return 1;
}

int wlan_send(const struct io_state *state, const uint8_t *buf, int len) {
	static uint8_t frame[RADIO_CLIENT_MAX_FRAME];
	if (!state || !state->wlan_handle || len < 0 || len > (int) sizeof(frame))
		return -EINVAL;
	memcpy(frame, buf, (size_t) len);
	frame_fixup_sent(frame, len);
	return radio_client_send(state->wlan_handle->radio, RADIO_LINK_TX, frame, (uint32_t) len);
}

int host_send(const struct io_state *state, const uint8_t *buf, int len) {
	(void) state;
	int packet_len = 0;
	const uint8_t *packet = host_frame_ip6(buf, len, &packet_len);
	if (!host || !packet)
		return 0;
	return host_if_write(host, packet, packet_len) < 0 ? -ENOBUFS : 0;
}

int host_recv(const struct io_state *state, uint8_t *buf, int *len) {
	static uint8_t packet[HOST_IF_MAX_PACKET];
	if (!host)
		return -EWOULDBLOCK;
	/* Skip what AWDL cannot carry instead of reporting it: the read event
	 * fires again only once the adapter's queue has been seen empty. */
	for (;;) {
		int packet_len = next_host_packet(packet);
		if (packet_len == 0)
			return -EWOULDBLOCK;
		int frame_len = host_frame_from_ip6(
			packet, packet_len, state->if_ether_addr.ether_addr_octet, buf, *len);
		if (frame_len > 0) {
			*len = frame_len;
			return 0;
		}
	}
}

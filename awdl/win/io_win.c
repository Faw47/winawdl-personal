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
	if (length == 0)
		return 0;
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
		int packet_len = host_if_read(host, packet);
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

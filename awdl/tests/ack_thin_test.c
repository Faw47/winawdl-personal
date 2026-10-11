/* SPDX-License-Identifier: GPL-3.0-or-later */
/* Which TCP acknowledgements a later one makes redundant. */
#include <stdio.h>
#include <string.h>

#include "ack_thin.h"

#define IP6_HEADER_LEN 40
#define TCP_ACK 0x10
#define TCP_PSH 0x08
#define TCP_FIN 0x01

static int failures;

static void expect(int condition, const char *what) {
	printf("%s  %s\n", condition ? "ok  " : "FAIL", what);
	if (!condition)
		failures++;
}

/* An IPv6 TCP segment from port 8771 with these fields; returns its length. */
static int segment(uint8_t *packet, uint32_t ack, uint8_t flags, const uint8_t *options,
		   int options_len, int data_len) {
	int tcp_len = 20 + options_len;
	memset(packet, 0, (size_t) (IP6_HEADER_LEN + tcp_len + data_len));
	packet[0] = 0x60;
	packet[4] = (uint8_t) ((tcp_len + data_len) >> 8);
	packet[5] = (uint8_t) (tcp_len + data_len);
	packet[6] = 6;
	packet[8] = 0xFE;
	packet[9] = 0x80;
	packet[23] = 1;
	packet[24] = 0xFE;
	packet[25] = 0x80;
	packet[39] = 2;
	uint8_t *tcp = packet + IP6_HEADER_LEN;
	tcp[0] = 0x22;
	tcp[1] = 0x43;
	tcp[2] = 0xC0;
	tcp[3] = 0x01;
	tcp[8] = (uint8_t) (ack >> 24);
	tcp[9] = (uint8_t) (ack >> 16);
	tcp[10] = (uint8_t) (ack >> 8);
	tcp[11] = (uint8_t) ack;
	tcp[12] = (uint8_t) (tcp_len / 4 << 4);
	tcp[13] = flags;
	if (options_len)
		memcpy(tcp + 20, options, (size_t) options_len);
	return IP6_HEADER_LEN + tcp_len + data_len;
}

int main(void) {
	static const uint8_t timestamps[12] = {1, 1, 8, 10, 0, 0, 0, 1, 0, 0, 0, 2};
	static const uint8_t sack[12] = {1, 1, 5, 10, 0, 0, 0, 1, 0, 0, 0, 2};
	uint8_t older[128], newer[128];

	int older_len = segment(older, 1000, TCP_ACK, timestamps, 12, 0);
	int newer_len = segment(newer, 3896, TCP_ACK, timestamps, 12, 0);
	expect(ack_thin_is_bare(older, older_len), "an acknowledgement with timestamps is bare");
	expect(ack_thin_supersedes(newer, newer_len, older, older_len),
	       "a later acknowledgement of the same connection replaces an earlier one");
	expect(!ack_thin_supersedes(older, older_len, newer, newer_len),
	       "an earlier one does not replace a later one");

	newer_len = segment(newer, 1000, TCP_ACK, timestamps, 12, 0);
	expect(!ack_thin_supersedes(newer, newer_len, older, older_len),
	       "a repeated acknowledgement is kept: it reports a missing segment");

	older_len = segment(older, 0xFFFFFF00u, TCP_ACK, NULL, 0, 0);
	newer_len = segment(newer, 0x00000100u, TCP_ACK, NULL, 0, 0);
	expect(ack_thin_supersedes(newer, newer_len, older, older_len),
	       "numbers that wrap around still count as later");

	older_len = segment(older, 1000, TCP_ACK, timestamps, 12, 0);
	newer_len = segment(newer, 3896, TCP_ACK, sack, 12, 0);
	expect(!ack_thin_is_bare(newer, newer_len) &&
		       !ack_thin_supersedes(newer, newer_len, older, older_len) &&
		       !ack_thin_supersedes(older, older_len, newer, newer_len),
	       "selective acknowledgements are left alone");

	newer_len = segment(newer, 3896, TCP_ACK | TCP_PSH, timestamps, 12, 5);
	expect(!ack_thin_is_bare(newer, newer_len) &&
		       !ack_thin_supersedes(newer, newer_len, older, older_len),
	       "a segment that carries data replaces nothing");
	newer_len = segment(newer, 3896, TCP_ACK, timestamps, 12, 0);
	older_len = segment(older, 1000, TCP_ACK | TCP_FIN, timestamps, 12, 0);
	expect(!ack_thin_is_bare(older, older_len) &&
		       !ack_thin_supersedes(newer, newer_len, older, older_len),
	       "a segment that closes the connection is never dropped");

	older_len = segment(older, 1000, TCP_ACK, timestamps, 12, 0);
	newer[IP6_HEADER_LEN + 1] ^= 1;
	expect(!ack_thin_supersedes(newer, newer_len, older, older_len),
	       "another connection's acknowledgement replaces nothing");
	newer[IP6_HEADER_LEN + 1] ^= 1;
	newer[39] = 3;
	expect(!ack_thin_supersedes(newer, newer_len, older, older_len),
	       "nor does one to another address");
	newer[39] = 2;
	newer[6] = 17;
	expect(!ack_thin_is_bare(newer, newer_len) &&
		       !ack_thin_supersedes(newer, newer_len, older, older_len),
	       "nor does a UDP packet");
	expect(!ack_thin_is_bare(older, 30), "a cut-off packet is not an acknowledgement");

	return failures ? 1 : 0;
}

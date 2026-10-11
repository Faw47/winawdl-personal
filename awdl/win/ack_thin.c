/* SPDX-License-Identifier: GPL-3.0-or-later */
#include "ack_thin.h"

#include <stddef.h>
#include <string.h>

#define IP6_HEADER_LEN 40
#define IP6_PAYLOAD_LEN_OFFSET 4
#define IP6_NEXT_HEADER_OFFSET 6
#define IP6_ADDRESSES_OFFSET 8
#define IP6_ADDRESSES_LEN 32
#define IP6_NEXT_HEADER_TCP 6

#define TCP_MIN_HEADER_LEN 20
#define TCP_PORTS_LEN 4
#define TCP_ACK_NUMBER_OFFSET 8
#define TCP_DATA_OFFSET_OFFSET 12
#define TCP_FLAGS_OFFSET 13
#define TCP_FLAG_ACK 0x10
#define TCP_OPTION_END 0
#define TCP_OPTION_NOP 1
#define TCP_OPTION_TIMESTAMPS 8

static uint32_t read_be32(const uint8_t *bytes) {
	return (uint32_t) bytes[0] << 24 | (uint32_t) bytes[1] << 16 | (uint32_t) bytes[2] << 8 |
	       bytes[3];
}

/* Returns the TCP header of a bare acknowledgement, NULL for anything else. */
static const uint8_t *bare_ack(const uint8_t *packet, int length) {
	if (length < IP6_HEADER_LEN + TCP_MIN_HEADER_LEN || packet[0] >> 4 != 6 ||
	    packet[IP6_NEXT_HEADER_OFFSET] != IP6_NEXT_HEADER_TCP)
		return NULL;
	const uint8_t *tcp = packet + IP6_HEADER_LEN;
	int payload_len = packet[IP6_PAYLOAD_LEN_OFFSET] << 8 | packet[IP6_PAYLOAD_LEN_OFFSET + 1];
	int header_len = (tcp[TCP_DATA_OFFSET_OFFSET] >> 4) * 4;
	if (header_len < TCP_MIN_HEADER_LEN || payload_len != header_len ||
	    length != IP6_HEADER_LEN + payload_len || tcp[TCP_FLAGS_OFFSET] != TCP_FLAG_ACK)
		return NULL;
	for (int at = TCP_MIN_HEADER_LEN; at < header_len;) {
		uint8_t kind = tcp[at];
		if (kind == TCP_OPTION_END)
			break;
		if (kind == TCP_OPTION_NOP) {
			at++;
			continue;
		}
		if (kind != TCP_OPTION_TIMESTAMPS || at + 1 >= header_len || tcp[at + 1] < 2)
			return NULL;
		at += tcp[at + 1];
	}
	return tcp;
}

int ack_thin_is_bare(const uint8_t *packet, int length) {
	return bare_ack(packet, length) != NULL;
}

int ack_thin_supersedes(const uint8_t *newer, int newer_len, const uint8_t *older, int older_len) {
	const uint8_t *newer_tcp = bare_ack(newer, newer_len);
	const uint8_t *older_tcp = bare_ack(older, older_len);
	if (!newer_tcp || !older_tcp ||
	    memcmp(newer + IP6_ADDRESSES_OFFSET, older + IP6_ADDRESSES_OFFSET, IP6_ADDRESSES_LEN) !=
		    0 ||
	    memcmp(newer_tcp, older_tcp, TCP_PORTS_LEN) != 0)
		return 0;
	/* Sequence numbers wrap: later means ahead by less than half the range. */
	return (int32_t) (read_be32(newer_tcp + TCP_ACK_NUMBER_OFFSET) -
			  read_be32(older_tcp + TCP_ACK_NUMBER_OFFSET)) > 0;
}

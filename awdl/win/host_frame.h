/* SPDX-License-Identifier: GPL-3.0-or-later */
/* OWL's core exchanges Ethernet frames with the host, while the Windows
 * adapter carries bare IPv6 packets. These convert between the two. */
#pragma once
#include <stdint.h>

#define HOST_FRAME_HEADER_LEN 14

/* Wraps an IPv6 packet from the host in an Ethernet header. The destination
 * MAC is read out of the destination address: AWDL peers sit at fe80:: plus
 * the EUI-64 of their MAC. Returns the frame length, or -1 for a packet with
 * no AWDL destination or one that does not fit. */
int host_frame_from_ip6(const uint8_t *packet, int packet_len,
                        const uint8_t self[6], uint8_t *frame, int frame_cap);

/* Returns the IPv6 packet inside an Ethernet frame, or NULL when the frame
 * carries another protocol. */
const uint8_t *host_frame_ip6(const uint8_t *frame, int frame_len,
                              int *packet_len);

/* SPDX-License-Identifier: GPL-3.0-or-later */
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include "frame_fixup.h"
#include "service_bridge.h"
#include "host_frame.h"

int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size) {
	if (size < 2 || size > 4096) return 0;
	frame_fixup_announce(data[0] & 1, (data[0] >> 1) & 1, (data[0] & 4) ? 80 : 40);
	data++; size--;
	uint8_t *frame = malloc(size);
	memcpy(frame, data, size);
	int end = 0;
	awdl_action_tlvs(frame, (int) size, &end);
	uint8_t packet[SERVICE_BRIDGE_MAX_PACKET];
	service_bridge_packet(frame, (int) size, packet);
	frame_fixup_received(frame, (int) size);
	free(frame);

	/* What OWL hands out to send, with room for what fixup appends. */
	uint8_t *out = malloc(size + 256);
	memcpy(out, data, size);
	frame_fixup_sent(out, (int) size, (int) size + 256);
	free(out);

	uint8_t *eth = malloc(size);
	memcpy(eth, data, size);
	int packet_len = 0;
	const uint8_t *ip6 = host_frame_ip6(eth, (int) size, &packet_len);
	if (ip6 && (ip6 < eth || ip6 + packet_len > eth + size)) abort();
	free(eth);
	return 0;
}

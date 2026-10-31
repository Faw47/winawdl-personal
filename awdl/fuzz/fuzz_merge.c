/* SPDX-License-Identifier: GPL-3.0-or-later */
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include "tcp_merge.h"

static struct tcp_merge merge;
static uint8_t out[TCP_MERGE_MAX_PACKET];

/* Packets in a row, each a 2-byte length then its bytes. */
int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size) {
	memset(&merge, 0, sizeof(merge));
	while (size >= 2) {
		size_t length = data[0] | (data[1] << 8);
		data += 2; size -= 2;
		if (length > size) length = size;
		uint8_t *packet = malloc(length ? length : 1);
		memcpy(packet, data, length);
		if (!tcp_merge_offer(&merge, packet, (int) length)) tcp_merge_take(&merge, out);
		free(packet);
		data += length; size -= length;
	}
	tcp_merge_take(&merge, out);
	return 0;
}

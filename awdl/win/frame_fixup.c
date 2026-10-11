/* SPDX-License-Identifier: GPL-3.0-or-later */
#include "frame_fixup.h"

#include <string.h>

#define RADIOTAP_MIN_LEN 8
#define RADIOTAP_PRESENT_FLAGS (1u << 1)
#define RADIOTAP_PRESENT_TSFT (1u << 0)
#define RADIOTAP_FLAGS_OFFSET 8
#define RADIOTAP_FLAG_FCS 0x10

#define IEEE80211_HEADER_LEN 24
#define FRAME_CONTROL_ACTION 0xD0
#define FCS_LEN 4

/* Category 127, Apple's OUI, type 8: an AWDL action frame. */
static const uint8_t awdl_action[] = {0x7F, 0x00, 0x17, 0xF2, 0x08};
#define AWDL_FIXED_LEN 16
#define TLV_HEADER_LEN 3
#define TLV_HT_CAPABILITIES 7
#define HT_CAPABILITIES_OFFSET 2
/* Supported channel width, short guard interval at 40 MHz, CCK at 40 MHz. */
#define HT_CAPABILITIES_40MHZ 0x1042

static unsigned read_le16(const uint8_t *bytes) {
	return bytes[0] | bytes[1] << 8;
}

/* Returns where the TLVs of an AWDL action frame start, or -1 for any other
 * frame. `*fcs_len` is how many checksum bytes follow the frame. */
static int tlv_offset(const uint8_t *frame, int length, int *fcs_len) {
	if (length < RADIOTAP_MIN_LEN)
		return -1;
	int radiotap_len = (int) read_le16(frame + 2);
	unsigned present = read_le16(frame + 4) | read_le16(frame + 6) << 16;
	int start = radiotap_len + IEEE80211_HEADER_LEN + AWDL_FIXED_LEN;
	if (radiotap_len < RADIOTAP_MIN_LEN || length < start ||
	    frame[radiotap_len] != FRAME_CONTROL_ACTION ||
	    memcmp(frame + radiotap_len + IEEE80211_HEADER_LEN, awdl_action, sizeof(awdl_action)) !=
		    0)
		return -1;
	/* The flags field comes first unless a timestamp precedes it. */
	int flagged = (present & RADIOTAP_PRESENT_FLAGS) && !(present & RADIOTAP_PRESENT_TSFT) &&
		      radiotap_len > RADIOTAP_FLAGS_OFFSET &&
		      (frame[RADIOTAP_FLAGS_OFFSET] & RADIOTAP_FLAG_FCS);
	*fcs_len = flagged ? FCS_LEN : 0;
	return length - *fcs_len < start ? -1 : start;
}

/* Walks the TLVs from `offset` and returns where the walk stops: at `end`, or
 * where fewer bytes are left than the next TLV needs. */
static int walk_tlvs(const uint8_t *frame, int offset, int end, int wanted, int *found) {
	while (end - offset >= TLV_HEADER_LEN) {
		int tlv_len = (int) read_le16(frame + offset + 1);
		if (tlv_len > end - offset - TLV_HEADER_LEN)
			break;
		if (found && frame[offset] == wanted)
			*found = offset;
		offset += TLV_HEADER_LEN + tlv_len;
	}
	return offset;
}

int frame_fixup_received(uint8_t *frame, int length) {
	int fcs_len;
	int offset = tlv_offset(frame, length, &fcs_len);
	if (offset < 0)
		return length;
	int end = length - fcs_len;
	int stop = walk_tlvs(frame, offset, end, 0, NULL);
	int left = end - stop;
	if (left == 0 || left >= TLV_HEADER_LEN)
		return length;
	for (int i = stop; i < end; i++)
		if (frame[i] != 0)
			return length;
	memmove(frame + stop, frame + end, (size_t) fcs_len);
	return length - left;
}

void frame_fixup_sent(uint8_t *frame, int length) {
	int fcs_len;
	int offset = tlv_offset(frame, length, &fcs_len);
	if (offset < 0)
		return;
	int found = -1;
	walk_tlvs(frame, offset, length - fcs_len, TLV_HT_CAPABILITIES, &found);
	if (found < 0)
		return;
	uint8_t *caps = frame + found + TLV_HEADER_LEN + HT_CAPABILITIES_OFFSET;
	if (caps + 2 > frame + length)
		return;
	unsigned cleared = read_le16(caps) & ~HT_CAPABILITIES_40MHZ;
	caps[0] = (uint8_t) cleared;
	caps[1] = (uint8_t) (cleared >> 8);
}

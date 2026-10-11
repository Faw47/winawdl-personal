/* SPDX-License-Identifier: GPL-3.0-or-later */
/* Two places where OWL and the frames on the air disagree, corrected as AWDL
 * action frames pass between it and the radio. Both take a radiotap header
 * with the 802.11 frame behind it. */
#pragma once
#include <stdint.h>

/* An iPhone can end an action frame with zero padding, and OWL refuses the
 * frame when a byte or two of it is left over. Returns the length without
 * those bytes. */
int frame_fixup_received(uint8_t *frame, int length);

/* OWL announces that it receives 40 MHz frames, which the radio daemon does
 * not: takes that out of the capabilities of an outgoing frame, and puts in
 * what frame_fixup_announce asked for. */
void frame_fixup_sent(uint8_t *frame, int length);

/* What outgoing frames say this station can receive, beyond OWL's single
 * stream with the long guard interval. */
void frame_fixup_announce(int two_streams, int short_guard_interval);

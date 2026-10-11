/* SPDX-License-Identifier: GPL-3.0-or-later */
/* Which TCP acknowledgements need not go out. A sender waits for one after
 * some ten segments, Windows sends one for every two it receives, and over
 * AWDL each costs about as much time on the channel as the data it answers. */
#pragma once
#include <stdint.h>

/* Whether an IPv6 packet is a TCP acknowledgement and nothing else: no data,
 * no flag but ACK, no option but timestamps. A selective acknowledgement is
 * not one. */
int ack_thin_is_bare(const uint8_t *packet, int length);

/* Whether `newer` says everything `older` does: both are bare, of one
 * connection, and `newer` acknowledges more. A repeated acknowledgement
 * reports a missing segment and is never redundant. */
int ack_thin_supersedes(const uint8_t *newer, int newer_len, const uint8_t *older, int older_len);

/* SPDX-License-Identifier: GPL-3.0-or-later */
/* Shared between the Windows replacements for OWL's io.c and netutils.c. */
#pragma once

struct radio_client;

/* The radio connection opened by io_state_init; NULL before and after. */
struct radio_client *winawdl_radio(void);

/* Holds each TCP acknowledgement from the host back until the radio has
 * delivered nothing for `quiet_us`, so that one answers a whole aggregate.
 * 0 turns it off. */
void winawdl_hold_acks(int quiet_us);
/* How many acknowledgements a later one has replaced. */
unsigned long long winawdl_acks_replaced(void);

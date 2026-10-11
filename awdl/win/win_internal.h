/* SPDX-License-Identifier: GPL-3.0-or-later */
/* Shared between the Windows replacements for OWL's io.c and netutils.c. */
#pragma once

struct radio_client;

/* The radio connection opened by io_state_init; NULL before and after. */
struct radio_client *winawdl_radio(void);

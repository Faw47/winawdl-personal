/* SPDX-License-Identifier: GPL-3.0-or-later */
/* Regression for patches/0001: A-MSDUs of up to 40 subframes must not overrun
 * the caller's array of AWDL_MAX_AMSDU_SUBFRAMES frames. */
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "rx.h"
#include "peers.h"
#include "wire.h"

static const struct ether_addr peer = {{2, 1, 2, 3, 4, 5}};
static const struct ether_addr self = {{2, 5, 4, 3, 2, 1}};
static int failures = 0;

static void check(int condition, const char *label) {
    printf("%s  %s\n", condition ? "ok  " : "FAIL", label);
    if (!condition) ++failures;
}

static void probe(int n) {
    /* Header 14 + LLC 8 + AWDL 8 + payload 4 + 2 padding = 36. */
    uint8_t wire[40 * 36] = {0};
    size_t length = 0;
    struct awdl_state state = {0};
    state.peers.peers = awdl_peers_init();
    if (!state.peers.peers) { check(0, "peer allocation"); return; }
    if (awdl_peer_add(state.peers.peers, &peer, 0, NULL, NULL) != PEERS_OK) {
        check(0, "add known peer");
        awdl_peers_free(state.peers.peers);
        return;
    }
    for (int i = 0; i < n; i++) {
        uint8_t *part = wire + length;
        memcpy(part, &self, 6);
        memcpy(part + 6, &peer, 6);
        part[12] = 0;
        part[13] = 20; /* LLC + AWDL + 4 payload bytes */
        const uint8_t llc[] = {0xaa, 0xaa, 3, 0, 0x17, 0xf2, 0x08, 0};
        memcpy(part + 14, llc, sizeof(llc));
        const uint8_t awdl[] = {0, 0, 0, 0, 0, 0, 0x86, 0xdd};
        memcpy(part + 22, awdl, sizeof(awdl));
        part[30] = 0x60; /* beginning of an IPv6 header */
        length += 36;
    }
    const struct buf *frame = buf_new_const(wire, (int)length);
    struct buf *out[AWDL_MAX_AMSDU_SUBFRAMES + 1] = {0};
    struct buf *canary = (struct buf *)(uintptr_t)1;
    out[AWDL_MAX_AMSDU_SUBFRAMES] = canary;
    struct buf **cursor = out;
    int result = awdl_rx_data_amsdu(frame, &cursor, &self, &peer, &state);
    int expected = n <= AWDL_MAX_AMSDU_SUBFRAMES ? RX_OK : RX_UNEXPECTED_FORMAT;
    /* A refused A-MSDU gives back the frames it had made, so the caller,
     * which frees only after RX_OK, leaks none. */
    if (result != expected || cursor != out + (n <= AWDL_MAX_AMSDU_SUBFRAMES ? n : 0)
                           || out[AWDL_MAX_AMSDU_SUBFRAMES] != canary) {
        fprintf(stderr, "n=%d: result=%d cursor=%ld canary=%d\n", n, result,
                (long)(cursor - out), out[AWDL_MAX_AMSDU_SUBFRAMES] == canary);
        ++failures;
    }
    for (struct buf **p = out; p < cursor && p < out + AWDL_MAX_AMSDU_SUBFRAMES; ++p)
        buf_free(*p);
    if (out[AWDL_MAX_AMSDU_SUBFRAMES] != canary)
        buf_free(out[AWDL_MAX_AMSDU_SUBFRAMES]);
    buf_free(frame);
    awdl_peers_free(state.peers.peers);
}

int main(void) {
    for (int n = 1; n <= 40; ++n) probe(n);
    check(failures == 0, "A-MSDU 1..40, including 16->17 boundary");
    return failures ? 1 : 0;
}

/* SPDX-License-Identifier: MIT */
/* First contact with the card through the register proxy: reports the mapping
 * and checks the chip identification registers. Reads only. */
#include "regproxy_client.h"
#include <stdio.h>

/* Names and expected values follow devourer's src/kestrel/MacRegAx.h. */
#define R_AX_SYS_CFG1 0x00F0u
#define R_AX_SYS_CHIPINFO 0x00FCu
#define RTL8852C_DIE_ID 0x52u
#define TIMING_ROUNDS 2000

static BOOL print_mapping(HANDLE proxy) {
  REGPROXY_INFO info = {0};
  if (!regproxy_get_info(proxy, &info)) return FALSE;
  printf("proxy version %u, BAR %u KiB at 0x%llx, %s\n", info.version,
         info.bar_len / 1024, info.bar_phys,
         info.direct_mapped ? "mapped into the driver host"
                            : "accessed through system calls");
  return TRUE;
}

static BOOL check_chip_id(HANDLE proxy) {
  unsigned die_id = 0, sys_cfg1 = 0;
  if (!regproxy_read(proxy, R_AX_SYS_CHIPINFO, 1, &die_id) ||
      !regproxy_read(proxy, R_AX_SYS_CFG1, 4, &sys_cfg1))
    return FALSE;

  /* All-ones means the device did not answer the read. */
  printf("die id 0x%02x (want 0x%02x), SYS_CFG1 0x%08x, cut %u\n", die_id,
         RTL8852C_DIE_ID, sys_cfg1, (sys_cfg1 >> 12) & 0xF);
  return die_id == RTL8852C_DIE_ID;
}

/* The receive path polls a register, so the round-trip cost bounds its rate. */
static void print_round_trip_cost(HANDLE proxy) {
  LARGE_INTEGER frequency, start, end;
  unsigned value = 0;
  QueryPerformanceFrequency(&frequency);
  QueryPerformanceCounter(&start);
  for (int i = 0; i < TIMING_ROUNDS; i++)
    regproxy_read(proxy, R_AX_SYS_CFG1, 4, &value);
  QueryPerformanceCounter(&end);

  double micros = (double)(end.QuadPart - start.QuadPart) * 1e6 /
                  (double)frequency.QuadPart / TIMING_ROUNDS;
  printf("register round trip: %.1f microseconds average\n", micros);
}

int main(void) {
  HANDLE proxy = regproxy_open();
  if (proxy == INVALID_HANDLE_VALUE) {
    printf("register proxy not found or not openable (error %lu).\n"
           "Is the winawdl driver bound to the card, and is this prompt "
           "elevated?\n",
           GetLastError());
    return 1;
  }

  int exit_code = 0;
  if (!print_mapping(proxy)) {
    printf("GET_INFO failed: %lu\n", GetLastError());
    exit_code = 2;
  } else if (!check_chip_id(proxy)) {
    printf("chip identification failed\n");
    exit_code = 3;
  }
  if (exit_code != 2) print_round_trip_cost(proxy);

  CloseHandle(proxy);
  return exit_code;
}

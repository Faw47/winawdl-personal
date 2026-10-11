/* SPDX-License-Identifier: MIT */
/* Dumps a range of 32-bit registers through the register proxy. Reads only.
 * Usage: regdump <start-hex> <length-hex> */
#include "regproxy_client.h"
#include <stdio.h>
#include <stdlib.h>

#define WORD_BYTES 4u
#define WORDS_PER_LINE 4u

int main(int argc, char **argv) {
  if (argc != 3) {
    printf("usage: regdump <start-hex> <length-hex>\n");
    return 1;
  }
  unsigned start = strtoul(argv[1], NULL, 16) & ~(WORD_BYTES - 1);
  unsigned words = strtoul(argv[2], NULL, 16) / WORD_BYTES;
  if (words == 0 || words > REGPROXY_MAX_OPS) {
    printf("length must be between 4 and 0x%x bytes\n",
           REGPROXY_MAX_OPS * WORD_BYTES);
    return 1;
  }

  HANDLE proxy = regproxy_open();
  if (proxy == INVALID_HANDLE_VALUE) {
    printf("register proxy not openable (error %lu)\n", GetLastError());
    return 2;
  }

  REGPROXY_OP *ops = calloc(words, sizeof(*ops));
  if (!ops) return 3;
  for (unsigned i = 0; i < words; i++) {
    ops[i].offset = start + i * WORD_BYTES;
    ops[i].width = WORD_BYTES;
  }

  int exit_code = 0;
  if (!regproxy_batch(proxy, ops, words)) {
    printf("batch read failed (error %lu)\n", GetLastError());
    exit_code = 4;
  } else {
    unsigned nonzero = 0;
    for (unsigned i = 0; i < words; i++) {
      if (i % WORDS_PER_LINE == 0) printf("%05x:", ops[i].offset);
      printf(" %08x", ops[i].value);
      if (i % WORDS_PER_LINE == WORDS_PER_LINE - 1 || i == words - 1)
        printf("\n");
      if (ops[i].value) nonzero++;
    }
    printf("%u of %u words are non-zero\n", nonzero, words);
  }

  free(ops);
  CloseHandle(proxy);
  return exit_code;
}

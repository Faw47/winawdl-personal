/* SPDX-License-Identifier: MIT */
/* Shared between the register-proxy driver and its clients. */
#pragma once
#include <initguid.h>

/* {b6ac5fd6-9648-4794-b0a7-2c5f9cd2b33e} */
DEFINE_GUID(GUID_DEVINTERFACE_WINAWDL_REGPROXY, 0xb6ac5fd6, 0x9648, 0x4794,
            0xb0, 0xa7, 0x2c, 0x5f, 0x9c, 0xd2, 0xb3, 0x3e);

#define REGPROXY_VERSION 2u
#define REGPROXY_MAX_OPS 4096u
#define REGPROXY_MAX_CLOSE_OPS 32u

#define IOCTL_REGPROXY_GET_INFO \
  CTL_CODE(FILE_DEVICE_UNKNOWN, 0x800, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_REGPROXY_BATCH \
  CTL_CODE(FILE_DEVICE_UNKNOWN, 0x801, METHOD_BUFFERED, FILE_ANY_ACCESS)
/* Stores register writes the driver replays when the handle closes, however
 * the client dies. A client that enables DMA into its own memory must register
 * the writes that stop it, or the chip keeps writing to pages Windows reuses.
 * An empty input clears the list. */
#define IOCTL_REGPROXY_SET_CLOSE_OPS \
  CTL_CODE(FILE_DEVICE_UNKNOWN, 0x802, METHOD_BUFFERED, FILE_ANY_ACCESS)

enum {
  REGPROXY_READ = 0,
  REGPROXY_WRITE = 1,
  REGPROXY_SET_BITS = 2,   /* register |= value */
  REGPROXY_CLEAR_BITS = 3, /* register &= ~value */
};

#pragma pack(push, 1)
typedef struct _REGPROXY_INFO {
  unsigned int version;
  unsigned int bar_len;
  unsigned long long bar_phys; /* translated, as the CPU sees it */
  unsigned int direct_mapped;  /* 1 = host maps the BAR, 0 = system calls */
} REGPROXY_INFO;

/* Offsets must be naturally aligned for their width; clients split the rest. */
typedef struct _REGPROXY_OP {
  unsigned int offset;
  unsigned int value;  /* in for writes and bit ops, out for reads */
  unsigned char width; /* 1, 2 or 4 */
  unsigned char kind;  /* REGPROXY_READ and friends */
  unsigned short reserved;
} REGPROXY_OP;
#pragma pack(pop)

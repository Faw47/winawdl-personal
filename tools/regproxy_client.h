/* SPDX-License-Identifier: MIT */
/* Client side of the register proxy, shared by the tools in this folder. */
#pragma once
#define WIN32_LEAN_AND_MEAN
#include "../driver/regproxy/regproxy_ioctl.h"
#include <windows.h>
#include <winioctl.h>

/* Returns INVALID_HANDLE_VALUE when the driver is not bound or not openable. */
HANDLE regproxy_open(void);
BOOL regproxy_get_info(HANDLE proxy, REGPROXY_INFO *info);
/* Runs up to REGPROXY_MAX_OPS accesses in one call; reads are filled in place.
 */
BOOL regproxy_batch(HANDLE proxy, REGPROXY_OP *ops, size_t count);
BOOL regproxy_read(HANDLE proxy, unsigned offset, unsigned char width,
                   unsigned *value);
BOOL regproxy_write(HANDLE proxy, unsigned offset, unsigned char width,
                    unsigned value);

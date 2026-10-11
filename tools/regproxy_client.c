/* SPDX-License-Identifier: MIT */
#include "regproxy_client.h"
#include <setupapi.h>
#include <stdlib.h>

HANDLE regproxy_open(void) {
  const GUID *iface = &GUID_DEVINTERFACE_WINAWDL_REGPROXY;
  HDEVINFO set = SetupDiGetClassDevsW(iface, NULL, NULL,
                                      DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
  if (set == INVALID_HANDLE_VALUE) return INVALID_HANDLE_VALUE;

  HANDLE proxy = INVALID_HANDLE_VALUE;
  SP_DEVICE_INTERFACE_DATA data = {sizeof(data)};
  if (SetupDiEnumDeviceInterfaces(set, NULL, iface, 0, &data)) {
    DWORD size = 0;
    SetupDiGetDeviceInterfaceDetailW(set, &data, NULL, 0, &size, NULL);
    SP_DEVICE_INTERFACE_DETAIL_DATA_W *detail = malloc(size);
    if (detail) {
      detail->cbSize = sizeof(*detail);
      if (SetupDiGetDeviceInterfaceDetailW(set, &data, detail, size, NULL, NULL))
        proxy = CreateFileW(detail->DevicePath, GENERIC_READ | GENERIC_WRITE, 0,
                            NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
      free(detail);
    }
  }
  SetupDiDestroyDeviceInfoList(set);
  return proxy;
}

BOOL regproxy_get_info(HANDLE proxy, REGPROXY_INFO *info) {
  DWORD returned = 0;
  return DeviceIoControl(proxy, IOCTL_REGPROXY_GET_INFO, NULL, 0, info,
                         sizeof(*info), &returned, NULL);
}

BOOL regproxy_batch(HANDLE proxy, REGPROXY_OP *ops, size_t count) {
  DWORD bytes = (DWORD)(count * sizeof(*ops)), returned = 0;
  return DeviceIoControl(proxy, IOCTL_REGPROXY_BATCH, ops, bytes, ops, bytes,
                         &returned, NULL);
}

BOOL regproxy_read(HANDLE proxy, unsigned offset, unsigned char width,
                   unsigned *value) {
  REGPROXY_OP op = {.offset = offset, .width = width};
  if (!regproxy_batch(proxy, &op, 1)) return FALSE;
  *value = op.value;
  return TRUE;
}

BOOL regproxy_write(HANDLE proxy, unsigned offset, unsigned char width,
                    unsigned value) {
  REGPROXY_OP op = {.offset = offset,
                    .value = value,
                    .width = width,
                    .kind = REGPROXY_WRITE};
  return regproxy_batch(proxy, &op, 1);
}

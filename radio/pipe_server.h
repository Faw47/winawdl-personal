// SPDX-License-Identifier: GPL-2.0-only
/* Server end of the radio link: one overlapped named pipe instance, one client
 * at a time. Reads happen on one thread and writes on another, which a
 * non-overlapped handle would serialize against each other. */
#pragma once

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include <atomic>
#include <cstdint>
#include <functional>
#include <mutex>
#include <span>
#include <string>

#include "radio_link.h"

class PipeServer {
public:
  /* `stopping` is polled while an operation waits, so a blocked read or
   * connect ends soon after the daemon is asked to stop. */
  PipeServer(const std::string &name, std::function<bool()> stopping)
      : _stopping(std::move(stopping)) {
    _handle = CreateNamedPipeA(
        name.c_str(),
        PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_REJECT_REMOTE_CLIENTS, 1,
        kBufferBytes, kBufferBytes, 0, nullptr);
    _read_event = CreateEventA(nullptr, TRUE, FALSE, nullptr);
    _write_event = CreateEventA(nullptr, TRUE, FALSE, nullptr);
  }
  ~PipeServer() {
    if (_handle != INVALID_HANDLE_VALUE)
      CloseHandle(_handle);
    CloseHandle(_read_event);
    CloseHandle(_write_event);
  }
  PipeServer(const PipeServer &) = delete;
  PipeServer &operator=(const PipeServer &) = delete;

  bool valid() const { return _handle != INVALID_HANDLE_VALUE; }
  bool connected() const { return _connected; }

  /* A client that connected and already left still counts as connected here:
   * its departure shows up as a failed first read. */
  bool wait_for_client() {
    OVERLAPPED io{};
    io.hEvent = _read_event;
    if (!ConnectNamedPipe(_handle, &io)) {
      const DWORD error = GetLastError();
      if (error == ERROR_IO_PENDING) {
        DWORD ignored = 0;
        if (!finish(io, ignored))
          return false;
      } else if (error != ERROR_PIPE_CONNECTED && error != ERROR_NO_DATA) {
        return false;
      }
    }
    _connected = true;
    return true;
  }

  /* Frees the single pipe instance for the next client. */
  void drop_client() {
    _connected = false;
    DisconnectNamedPipe(_handle);
  }

  bool read_exact(void *data, DWORD length) {
    auto *bytes = static_cast<uint8_t *>(data);
    while (length > 0) {
      OVERLAPPED io{};
      io.hEvent = _read_event;
      DWORD got = 0;
      if (!ReadFile(_handle, bytes, length, &got, &io) &&
          (GetLastError() != ERROR_IO_PENDING || !finish(io, got)))
        return disconnect();
      if (got == 0)
        return disconnect();
      bytes += got;
      length -= got;
    }
    return true;
  }

  bool send(radio_link_type type, std::span<const uint8_t> payload) {
    if (!_connected)
      return false;
    std::lock_guard<std::mutex> lock(_write_mutex);
    const radio_link_header header{static_cast<uint16_t>(type), 0,
                                   static_cast<uint32_t>(payload.size())};
    return write_all(&header, sizeof(header)) &&
           write_all(payload.data(), static_cast<DWORD>(payload.size()));
  }

private:
  static constexpr DWORD kBufferBytes = 1 << 16;
  static constexpr DWORD kStopPollMs = 100;

  bool disconnect() {
    _connected = false;
    return false;
  }

  bool finish(OVERLAPPED &io, DWORD &transferred) {
    while (WaitForSingleObject(io.hEvent, kStopPollMs) == WAIT_TIMEOUT) {
      if (_stopping()) {
        CancelIoEx(_handle, &io);
        GetOverlappedResult(_handle, &io, &transferred, TRUE);
        return false;
      }
    }
    return GetOverlappedResult(_handle, &io, &transferred, FALSE) != 0;
  }

  bool write_all(const void *data, DWORD length) {
    auto *bytes = static_cast<const uint8_t *>(data);
    while (length > 0) {
      OVERLAPPED io{};
      io.hEvent = _write_event;
      DWORD put = 0;
      if (!WriteFile(_handle, bytes, length, &put, &io) &&
          (GetLastError() != ERROR_IO_PENDING || !finish(io, put)))
        return disconnect();
      bytes += put;
      length -= put;
    }
    return true;
  }

  std::function<bool()> _stopping;
  HANDLE _handle = INVALID_HANDLE_VALUE;
  HANDLE _read_event = nullptr;
  HANDLE _write_event = nullptr;
  std::atomic<bool> _connected{false};
  std::mutex _write_mutex;
};

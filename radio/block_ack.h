// SPDX-License-Identifier: GPL-2.0-only
/* The frame side of receiving under a block-ack agreement: reading the
 * request a sender makes and writing the answer. The card does the rest. */
#pragma once

#include <algorithm>
#include <array>
#include <cstdint>
#include <cstring>
#include <optional>
#include <span>
#include <utility>
#include <vector>

namespace block_ack {

using Mac = std::array<uint8_t, 6>;

/* The widest window accepted, which is also what an iPhone asks for. */
constexpr uint16_t kMaxBufferFrames = 64;

struct Request {
  Mac peer;
  uint8_t dialog_token;
  uint8_t tid;
  uint16_t buffer_frames;
  uint16_t start_sequence;
};

namespace detail {
constexpr size_t kHeaderBytes = 24;
constexpr size_t kReceiverOffset = 4;
constexpr size_t kSenderOffset = 10;
constexpr size_t kBssidOffset = 16;
constexpr uint8_t kFrameControlAction = 0xD0;
constexpr uint8_t kCategoryBlockAck = 3;
constexpr uint8_t kActionAddbaRequest = 0;
constexpr uint8_t kActionAddbaResponse = 1;
constexpr uint8_t kActionDelba = 2;
/* Category, action, token, parameters, timeout, starting sequence. */
constexpr size_t kAddbaRequestBytes = 9;
constexpr size_t kDelbaBytes = 6;
constexpr uint16_t kParamImmediate = 1u << 1;
constexpr uint8_t kParamTidShift = 2;
constexpr uint8_t kParamBufferShift = 6;
constexpr uint8_t kDelbaTidShift = 12;
/* Radiotap with a rate field only: 12 Mbit/s, as the client's frames use. */
constexpr uint8_t kRadiotap[] = {0, 0, 9, 0, 0x04, 0, 0, 0, 24};

inline uint16_t read16(std::span<const uint8_t> bytes, size_t offset) {
  return static_cast<uint16_t>(bytes[offset] | bytes[offset + 1] << 8);
}

/* The body of a block-ack action frame sent by one station to `own`. */
inline std::span<const uint8_t> body_for(std::span<const uint8_t> frame,
                                         const Mac &own, uint8_t action,
                                         size_t body_bytes) {
  if (frame.size() < kHeaderBytes + body_bytes ||
      frame[0] != kFrameControlAction ||
      std::memcmp(frame.data() + kReceiverOffset, own.data(), own.size()) != 0)
    return {};
  const auto body = frame.subspan(kHeaderBytes);
  if (body[0] != kCategoryBlockAck || body[1] != action) return {};
  return body;
}

inline Mac sender_of(std::span<const uint8_t> frame) {
  Mac sender;
  std::memcpy(sender.data(), frame.data() + kSenderOffset, sender.size());
  return sender;
}
} // namespace detail

/* Reads `frame` as an ADDBA request addressed to `own`. */
inline std::optional<Request> parse_request(std::span<const uint8_t> frame,
                                            const Mac &own) {
  using namespace detail;
  const auto body =
      body_for(frame, own, kActionAddbaRequest, kAddbaRequestBytes);
  if (body.empty()) return std::nullopt;
  const uint16_t params = read16(body, 3);
  return Request{.peer = sender_of(frame),
                 .dialog_token = body[2],
                 .tid = static_cast<uint8_t>((params >> kParamTidShift) & 0xF),
                 .buffer_frames =
                     static_cast<uint16_t>(params >> kParamBufferShift),
                 .start_sequence = static_cast<uint16_t>(read16(body, 7) >> 4)};
}

/* The sender and TID of a DELBA addressed to `own`. */
inline std::optional<std::pair<Mac, uint8_t>>
parse_delba(std::span<const uint8_t> frame, const Mac &own) {
  using namespace detail;
  const auto body = body_for(frame, own, kActionDelba, kDelbaBytes);
  if (body.empty()) return std::nullopt;
  return std::pair{sender_of(frame),
                   static_cast<uint8_t>(read16(body, 2) >> kDelbaTidShift)};
}

/* How many frames of window to grant for a request. */
inline uint16_t granted_frames(const Request &request) {
  return request.buffer_frames == 0
             ? kMaxBufferFrames
             : std::min(request.buffer_frames, kMaxBufferFrames);
}

/* A radiotap header and the ADDBA response that accepts `request`: immediate
 * block ack, no A-MSDU inside an aggregate. */
inline std::vector<uint8_t> response(const Request &request, const Mac &own,
                                     const Mac &bssid) {
  using namespace detail;
  std::vector<uint8_t> message(std::begin(kRadiotap), std::end(kRadiotap));
  const size_t frame = message.size();
  message.resize(frame + kHeaderBytes, 0);
  message[frame] = kFrameControlAction;
  std::copy(request.peer.begin(), request.peer.end(),
            message.begin() + frame + kReceiverOffset);
  std::copy(own.begin(), own.end(), message.begin() + frame + kSenderOffset);
  std::copy(bssid.begin(), bssid.end(), message.begin() + frame + kBssidOffset);
  const uint16_t params =
      static_cast<uint16_t>(kParamImmediate | (request.tid << kParamTidShift) |
                            (granted_frames(request) << kParamBufferShift));
  const uint8_t body[] = {kCategoryBlockAck,
                          kActionAddbaResponse,
                          request.dialog_token,
                          0,
                          0, /* status: success */
                          static_cast<uint8_t>(params),
                          static_cast<uint8_t>(params >> 8),
                          0,
                          0 /* no timeout */};
  message.insert(message.end(), std::begin(body), std::end(body));
  return message;
}

} // namespace block_ack

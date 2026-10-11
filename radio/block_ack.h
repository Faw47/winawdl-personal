// SPDX-License-Identifier: GPL-2.0-only
/* The frame side of a block-ack agreement. Receiving: reading the request a
 * sender makes and writing the answer. Sending: writing the request and
 * reading the answer. The card does the rest. */
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

/* What a station answered to a request of ours. */
struct Answer {
  Mac peer;
  uint8_t dialog_token;
  uint8_t tid;
  bool accepted;
  uint16_t buffer_frames;
};

/* A session a station ended, and which side of it that station is. */
struct Ended {
  Mac peer;
  uint8_t tid;
  bool by_sender;
};

namespace detail {
constexpr size_t kHeaderBytes = 24;
constexpr size_t kReceiverOffset = 4;
constexpr size_t kSenderOffset = 10;
constexpr size_t kBssidOffset = 16;
constexpr uint8_t kFrameControlAction = 0xD0;
constexpr uint8_t kFrameControlBlockAckRequest = 0x84;
/* In a block-ack request: the compressed form, which the answer then has. */
constexpr uint16_t kBarCompressed = 1u << 2;
constexpr uint8_t kBarTidShift = 12;
constexpr uint8_t kCategoryBlockAck = 3;
constexpr uint8_t kActionAddbaRequest = 0;
constexpr uint8_t kActionAddbaResponse = 1;
constexpr uint8_t kActionDelba = 2;
/* Category, action, token, parameters, timeout, starting sequence. */
constexpr size_t kAddbaRequestBytes = 9;
/* Category, action, token, status, parameters, timeout. */
constexpr size_t kAddbaResponseBytes = 9;
constexpr size_t kDelbaBytes = 6;
/* In a DELBA: set by the side that sends the data. */
constexpr uint16_t kDelbaInitiator = 1u << 11;
constexpr uint16_t kParamImmediate = 1u << 1;
constexpr uint8_t kParamTidShift = 2;
constexpr uint8_t kParamBufferShift = 6;
constexpr uint8_t kDelbaTidShift = 12;
/* "The station is not using the mechanism", as Linux says when it ends the
 * receiving side of a session. */
constexpr uint8_t kReasonNotUsed = 37;
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

/* Reads `frame` as a DELBA addressed to `own`. */
inline std::optional<Ended> parse_delba(std::span<const uint8_t> frame,
                                        const Mac &own) {
  using namespace detail;
  const auto body = body_for(frame, own, kActionDelba, kDelbaBytes);
  if (body.empty()) return std::nullopt;
  const uint16_t params = read16(body, 2);
  return Ended{.peer = sender_of(frame),
               .tid = static_cast<uint8_t>(params >> kDelbaTidShift),
               .by_sender = (params & kDelbaInitiator) != 0};
}

/* Reads `frame` as an ADDBA response addressed to `own`. */
inline std::optional<Answer> parse_response(std::span<const uint8_t> frame,
                                            const Mac &own) {
  using namespace detail;
  const auto body =
      body_for(frame, own, kActionAddbaResponse, kAddbaResponseBytes);
  if (body.empty()) return std::nullopt;
  const uint16_t params = read16(body, 5);
  return Answer{.peer = sender_of(frame),
                .dialog_token = body[2],
                .tid = static_cast<uint8_t>((params >> kParamTidShift) & 0xF),
                .accepted = read16(body, 3) == 0,
                .buffer_frames =
                    static_cast<uint16_t>(params >> kParamBufferShift)};
}

/* How many frames of window to grant for a request. */
inline uint16_t granted_frames(const Request &request) {
  return request.buffer_frames == 0
             ? kMaxBufferFrames
             : std::min(request.buffer_frames, kMaxBufferFrames);
}

namespace detail {
/* A radiotap header and an action frame from `own` to `peer` with this body. */
inline std::vector<uint8_t> action_to(const Mac &peer, const Mac &own,
                                      const Mac &bssid,
                                      std::span<const uint8_t> body) {
  std::vector<uint8_t> message(std::begin(kRadiotap), std::end(kRadiotap));
  const size_t frame = message.size();
  message.resize(frame + kHeaderBytes, 0);
  message[frame] = kFrameControlAction;
  std::copy(peer.begin(), peer.end(),
            message.begin() + frame + kReceiverOffset);
  std::copy(own.begin(), own.end(), message.begin() + frame + kSenderOffset);
  std::copy(bssid.begin(), bssid.end(), message.begin() + frame + kBssidOffset);
  message.insert(message.end(), body.begin(), body.end());
  return message;
}
} // namespace detail

/* A radiotap header and the ADDBA response that accepts `request`: immediate
 * block ack, no A-MSDU inside an aggregate. */
inline std::vector<uint8_t> response(const Request &request, const Mac &own,
                                     const Mac &bssid) {
  using namespace detail;
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
  return action_to(request.peer, own, bssid, body);
}

/* A radiotap header and the ADDBA request that asks `peer` to take
 * aggregates of `tid`: immediate block ack, no A-MSDU, the widest window,
 * the first frame numbered `start_sequence`. */
inline std::vector<uint8_t> request(const Mac &peer, const Mac &own,
                                    const Mac &bssid, uint8_t tid,
                                    uint8_t dialog_token,
                                    uint16_t start_sequence) {
  using namespace detail;
  const uint16_t params =
      static_cast<uint16_t>(kParamImmediate | (tid << kParamTidShift) |
                            (kMaxBufferFrames << kParamBufferShift));
  const uint16_t start = static_cast<uint16_t>(start_sequence << 4);
  const uint8_t body[] = {kCategoryBlockAck,
                          kActionAddbaRequest,
                          dialog_token,
                          static_cast<uint8_t>(params),
                          static_cast<uint8_t>(params >> 8),
                          0,
                          0, /* no timeout */
                          static_cast<uint8_t>(start),
                          static_cast<uint8_t>(start >> 8)};
  return action_to(peer, own, bssid, body);
}

/* A radiotap header and the block-ack request that tells `peer` to stop
 * waiting for frames of `tid` numbered before `start_sequence`: the sender
 * has given up on those it did not get. */
inline std::vector<uint8_t> window_start(const Mac &peer, const Mac &own,
                                         uint8_t tid, uint16_t start_sequence) {
  using namespace detail;
  const uint16_t control =
      static_cast<uint16_t>(kBarCompressed | (tid << kBarTidShift));
  const uint16_t start = static_cast<uint16_t>(start_sequence << 4);
  std::vector<uint8_t> message(std::begin(kRadiotap), std::end(kRadiotap));
  message.insert(message.end(), {kFrameControlBlockAckRequest, 0, 0, 0});
  message.insert(message.end(), peer.begin(), peer.end());
  message.insert(message.end(), own.begin(), own.end());
  message.insert(message.end(), {static_cast<uint8_t>(control),
                                 static_cast<uint8_t>(control >> 8),
                                 static_cast<uint8_t>(start),
                                 static_cast<uint8_t>(start >> 8)});
  return message;
}

/* A radiotap header and the DELBA by which the receiving side ends the
 * session `request` opened. */
inline std::vector<uint8_t> delba(const Request &request, const Mac &own,
                                  const Mac &bssid) {
  using namespace detail;
  const uint8_t body[] = {kCategoryBlockAck,
                          kActionDelba,
                          0,
                          static_cast<uint8_t>(request.tid << 4),
                          kReasonNotUsed,
                          0};
  return action_to(request.peer, own, bssid, body);
}

} // namespace block_ack

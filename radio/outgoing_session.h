// SPDX-License-Identifier: GPL-2.0-only
/* The sending side of a block-ack agreement: when to ask a station for one,
 * and how the data frames of an open one are numbered. One station at a
 * time. */
#pragma once

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <optional>
#include <span>
#include <utility>
#include <vector>

#include "block_ack.h"

class OutgoingSession {
public:
  using Clock = std::chrono::steady_clock;
  using Mac = block_ack::Mac;

  static constexpr uint8_t kTid = 0;
  /* A station is asked once this many data frames in a row went to it: a
   * handful is not worth an agreement. */
  static constexpr unsigned kFramesBeforeAsking = 16;
  static constexpr std::chrono::seconds kAskAgainAfter{1};
  static constexpr unsigned kMostAsks = 3;
  static constexpr uint16_t kSequenceMask = 0x0FFF;

  struct Ask {
    Mac station;
    uint8_t dialog_token;
    uint16_t start_sequence;
  };

  bool open_with(const Mac &station) const {
    return _state == State::Open && station == _station;
  }
  uint64_t asks() const { return _all_asks; }
  uint64_t numbered() const { return _numbered; }

  /* A data frame is about to go to `station`. Returns the request to send
   * ahead of it when it is time to ask. */
  std::optional<Ask> before_frame(const Mac &station, Clock::time_point now) {
    if (_state == State::Open) return std::nullopt;
    if (station != _station) {
      _station = station;
      _state = State::Idle;
      _frames = _asks = 0;
    }
    if (++_frames < kFramesBeforeAsking || _asks >= kMostAsks) return {};
    if (_state == State::Asked && now - _asked_at < kAskAgainAfter) return {};
    _state = State::Asked;
    _asked_at = now;
    _asks++;
    _all_asks++;
    return Ask{station, ++_token, _next_sequence};
  }

  /* The number for a data frame to `station` if the session with it is open. */
  std::optional<uint16_t> number(const Mac &station) {
    if (!open_with(station)) return std::nullopt;
    const uint16_t sequence = _next_sequence;
    _next_sequence = (_next_sequence + 1) & kSequenceMask;
    _numbered++;
    return sequence;
  }

  /* The station of the open session and the number its window has to start
   * at once frames were given up on: `oldest_unfinished`, the oldest
   * numbered frame the card still holds, or the next number if it holds
   * none. Nothing when no session is open. */
  std::optional<std::pair<Mac, uint16_t>>
  window_start(std::optional<uint16_t> oldest_unfinished) const {
    if (_state != State::Open) return std::nullopt;
    return std::pair{_station, oldest_unfinished.value_or(_next_sequence)};
  }

  /* A station's answer to a request. Returns how many frames it takes in
   * one aggregate when the answer opens the session. */
  std::optional<uint16_t> answered(const block_ack::Answer &answer) {
    if (_state != State::Asked || answer.peer != _station ||
        answer.dialog_token != _token || answer.tid != kTid)
      return std::nullopt;
    if (!answer.accepted) {
      _state = State::Idle;
      _asks = kMostAsks;
      return std::nullopt;
    }
    _state = State::Open;
    return answer.buffer_frames
               ? std::min(answer.buffer_frames, block_ack::kMaxBufferFrames)
               : block_ack::kMaxBufferFrames;
  }

  /* A station ended a session. Returns whether it was this one. */
  bool ended(const block_ack::Ended &ended) {
    if (_state != State::Open || ended.by_sender || ended.peer != _station ||
        ended.tid != kTid)
      return false;
    close();
    return true;
  }

  /* Ends the session without a word; the station may be asked again. */
  void close() {
    _state = State::Idle;
    _frames = 0;
  }

private:
  enum class State { Idle, Asked, Open };

  State _state = State::Idle;
  Mac _station{};
  Clock::time_point _asked_at{};
  unsigned _frames = 0, _asks = 0;
  uint8_t _token = 0;
  uint16_t _next_sequence = 0;
  uint64_t _all_asks = 0, _numbered = 0;
};

/* Keeps telling a station where its window starts until it shows that it
 * heard. The frames it has to be told about were mostly lost because it was
 * off the channel, and then the first telling is lost the same way. */
class WindowNotice {
public:
  using Clock = std::chrono::steady_clock;

  static constexpr std::chrono::milliseconds kPause{5};
  static constexpr std::chrono::seconds kGiveUpAfter{3};
  static constexpr unsigned kMostTellings = 30;
  static constexpr uint16_t kSequenceMask = 0x0FFF;

  /* What the receive side knows of the station: when it was last heard,
   * when its last block ack came and where that one's window started. */
  struct Heard {
    Clock::time_point station;
    Clock::time_point block_ack;
    uint16_t block_ack_start;
  };

  uint64_t tellings() const { return _all_tellings; }

  /* The card gave up on a frame the station is waiting for. */
  void needed(Clock::time_point now) {
    if (!_pending) _since = now;
    _pending = true;
    _tellings = 0;
  }

  /* Whether the station should be told now. A telling is repeated once the
   * station has been heard again without a block ack that starts at or
   * after what it was told. */
  bool due(const Heard &heard, Clock::time_point now) {
    if (!_pending) return false;
    if (_tellings == 0) return true;
    const bool moved =
        heard.block_ack > _told_at &&
        ((heard.block_ack_start - _start) & kSequenceMask) <= kSequenceMask / 2;
    if (moved || now - _since > kGiveUpAfter || _tellings >= kMostTellings) {
      _pending = false;
      return false;
    }
    return heard.station > _told_at && now - _told_at >= kPause;
  }

  void told(uint16_t start, Clock::time_point now) {
    _start = start;
    _told_at = now;
    _tellings++;
    _all_tellings++;
  }

  void clear() { _pending = false; }

private:
  bool _pending = false;
  Clock::time_point _since{}, _told_at{};
  uint16_t _start = 0;
  unsigned _tellings = 0;
  uint64_t _all_tellings = 0;
};

/* `frame`, a plain data frame between two stations, as a QoS data frame of
 * `tid` numbered `sequence`: only such a frame can be part of an aggregate.
 * Empty for any other frame. */
inline std::vector<uint8_t> as_qos_data(std::span<const uint8_t> frame,
                                        uint8_t tid, uint16_t sequence) {
  constexpr size_t kHeaderBytes = 24;
  constexpr size_t kSequenceOffset = 22;
  constexpr uint8_t kFrameControlData = 0x08;
  constexpr uint8_t kSubtypeQos = 0x80;
  constexpr uint8_t kDistributionBits = 0x03;
  if (frame.size() < kHeaderBytes || frame[0] != kFrameControlData ||
      (frame[1] & kDistributionBits))
    return {};
  std::vector<uint8_t> qos(frame.begin(), frame.begin() + kHeaderBytes);
  qos[0] |= kSubtypeQos;
  qos[kSequenceOffset] = static_cast<uint8_t>(sequence << 4);
  qos[kSequenceOffset + 1] = static_cast<uint8_t>(sequence >> 4);
  /* QoS control: the TID, and an acknowledgement asked for as usual, which
   * inside an aggregate means by block ack. */
  qos.push_back(tid & 0x0F);
  qos.push_back(0);
  qos.insert(qos.end(), frame.begin() + kHeaderBytes, frame.end());
  return qos;
}

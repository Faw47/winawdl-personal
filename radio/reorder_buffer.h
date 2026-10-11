// SPDX-License-Identifier: GPL-2.0-only
/* Puts the frames of a block-ack session back in order. A sender repeats a
 * lost frame after the ones that followed it, and TCP reads frames out of
 * order as loss and slows down. */
#pragma once

#include <array>
#include <chrono>
#include <cstdint>
#include <vector>

class ReorderBuffer {
public:
  using Message = std::vector<uint8_t>;
  using Clock = std::chrono::steady_clock;

  /* The window agreed with the sender, in frames. */
  static constexpr uint16_t kWindow = 64;
  /* How long a missing frame holds back the ones after it; Linux waits the
   * same. */
  static constexpr std::chrono::milliseconds kHold{100};

  bool active() const { return _active; }
  /* Frames that had to wait for an earlier one. */
  uint64_t waited() const { return _waited; }

  /* Starts a session at `first_sequence`; anything held is released first. */
  void start(uint16_t first_sequence, std::vector<Message> &out) {
    stop(out);
    _active = true;
    _head = first_sequence & kSequenceMask;
  }

  /* Ends the session, releasing what is held. */
  void stop(std::vector<Message> &out) {
    if (_active) release_until(static_cast<uint16_t>(_head + kWindow), out);
    _active = false;
  }

  /* Takes one frame of the session and appends to `out` what can now be
   * delivered, in order. */
  void push(uint16_t sequence, Message message, Clock::time_point now,
            std::vector<Message> &out) {
    uint16_t ahead = distance(sequence);
    if (ahead >= kBehind) {
      /* Before the window: late or repeated, and nothing waits for it. */
      out.push_back(std::move(message));
      return;
    }
    if (ahead >= kWindow) {
      release_until(static_cast<uint16_t>(sequence - kWindow + 1), out);
      ahead = distance(sequence);
    }
    Slot &slot = _slots[sequence % kWindow];
    if (slot.filled) return;
    if (ahead == 0) {
      out.push_back(std::move(message));
      _head = (_head + 1) & kSequenceMask;
    } else {
      slot = {std::move(message), now, true};
      _held++;
      _waited++;
    }
    release_in_order(out);
  }

  /* The sender has given up on everything before `sequence`. */
  void advance_to(uint16_t sequence, std::vector<Message> &out) {
    if (distance(sequence) >= kBehind) return;
    release_until(sequence, out);
    release_in_order(out);
  }

  /* Stops waiting for frames that have been missing longer than kHold. */
  void expire(Clock::time_point now, std::vector<Message> &out) {
    while (_held > 0) {
      uint16_t oldest = _head;
      while (!_slots[oldest % kWindow].filled)
        oldest = (oldest + 1) & kSequenceMask;
      if (now - _slots[oldest % kWindow].since < kHold) return;
      release_until(oldest, out);
      release_in_order(out);
    }
  }

private:
  static constexpr uint16_t kSequenceMask = 0x0FFF;
  static constexpr uint16_t kBehind = 0x0800;

  struct Slot {
    Message message;
    Clock::time_point since;
    bool filled = false;
  };

  uint16_t distance(uint16_t sequence) const {
    return static_cast<uint16_t>(sequence - _head) & kSequenceMask;
  }

  /* Delivers the frames that follow one another from the head. */
  void release_in_order(std::vector<Message> &out) {
    while (_slots[_head % kWindow].filled) take_head(out);
  }

  /* Moves the head to `sequence`, delivering what is held on the way and
   * giving up on what is not. */
  void release_until(uint16_t sequence, std::vector<Message> &out) {
    sequence &= kSequenceMask;
    while (_head != sequence) {
      if (_slots[_head % kWindow].filled)
        take_head(out);
      else
        _head = (_head + 1) & kSequenceMask;
    }
  }

  void take_head(std::vector<Message> &out) {
    Slot &slot = _slots[_head % kWindow];
    out.push_back(std::move(slot.message));
    slot.filled = false;
    _held--;
    _head = (_head + 1) & kSequenceMask;
  }

  std::array<Slot, kWindow> _slots;
  uint16_t _head = 0;
  uint16_t _held = 0;
  uint64_t _waited = 0;
  bool _active = false;
};

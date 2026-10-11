// SPDX-License-Identifier: GPL-2.0-only
/* Picks the rate at which data frames go to a station, from what the card
 * reports became of each. Level 0 is the slowest and is where every pause
 * ends, so a connection always opens at it. */
#pragma once

#include <algorithm>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <vector>

class RateLadder {
public:
  using Clock = std::chrono::steady_clock;

  /* Frames per verdict on the level in use. */
  static constexpr unsigned kWindowFrames = 32;
  static constexpr unsigned kCleanWindowsToClimb = 4;
  static constexpr unsigned kBadWindowsToFall = 2;
  /* A level just climbed to is left at once if this many frames in a row
   * fail on it from the start. */
  static constexpr unsigned kProbeFrames = 8;
  /* A level that failed is not tried again for this long, doubling each
   * time up to the longest, and from the start again once it has held for
   * kWindowsToForgive clean windows. */
  static constexpr std::chrono::seconds kFirstBlock{2};
  static constexpr std::chrono::seconds kLongestBlock{60};
  static constexpr unsigned kWindowsToForgive = 64;
  static constexpr std::chrono::milliseconds kPauseThatResets{1500};

  explicit RateLadder(size_t levels)
      : _blocks(levels, kFirstBlock), _blocked_until(levels),
        _delivered(levels) {}

  size_t levels() const { return _blocks.size(); }
  uint64_t climbs() const { return _climbs; }
  uint64_t falls() const { return _falls; }
  /* Frames each level got acknowledged. */
  const std::vector<uint64_t> &delivered() const { return _delivered; }

  /* The level for a frame that is about to be sent. */
  size_t next(Clock::time_point now) {
    if (_level && now - _last_sent > kPauseThatResets) enter(0);
    _last_sent = now;
    return _level;
  }

  /* The card is done with a frame that was sent at `level`. A frame it gave
   * up on says nothing unless the station was there to hear it. */
  void finished(size_t level, bool given_up, bool station_present,
                Clock::time_point now) {
    if (!given_up && level < _delivered.size()) _delivered[level]++;
    if (level != _level || (given_up && !station_present)) return;
    if (_probing) {
      if (!given_up) {
        _probing = 0;
      } else if (--_probing == 0) {
        fall(now);
        return;
      }
    }
    _failed += given_up;
    if (++_counted < kWindowFrames) return;
    const bool bad = _failed * 2 >= kWindowFrames;
    const bool clean = _failed * 10 <= kWindowFrames;
    _counted = _failed = 0;
    _bad_windows = bad ? _bad_windows + 1 : 0;
    _clean_windows = clean ? _clean_windows + 1 : 0;
    if (_clean_windows == kWindowsToForgive) _blocks[_level] = kFirstBlock;
    if (_bad_windows >= kBadWindowsToFall && _level > 0) {
      fall(now);
    } else if (_clean_windows >= kCleanWindowsToClimb &&
               _level + 1 < levels() && now >= _blocked_until[_level + 1]) {
      _climbs++;
      enter(_level + 1);
      _probing = kProbeFrames;
    }
  }

private:
  void enter(size_t level) {
    _level = level;
    _counted = _failed = _bad_windows = _clean_windows = _probing = 0;
  }

  void fall(Clock::time_point now) {
    _blocked_until[_level] = now + _blocks[_level];
    _blocks[_level] =
        std::min<std::chrono::seconds>(_blocks[_level] * 2, kLongestBlock);
    _falls++;
    enter(_level - 1);
  }

  std::vector<std::chrono::seconds> _blocks;
  std::vector<Clock::time_point> _blocked_until;
  std::vector<uint64_t> _delivered;
  Clock::time_point _last_sent{};
  size_t _level = 0;
  unsigned _counted = 0, _failed = 0;
  unsigned _bad_windows = 0, _clean_windows = 0, _probing = 0;
  uint64_t _climbs = 0, _falls = 0;
};

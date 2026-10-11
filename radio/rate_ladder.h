// SPDX-License-Identifier: GPL-2.0-only
/* Picks the rate at which data frames go to a station, from what the card
 * reports became of each. Level 0 is the slowest and is where every pause
 * ends, so a connection always opens at it. */
#pragma once

#include <algorithm>
#include <array>
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
  /* A level is left when half the frames of this many windows failed. One
   * window is not enough: a station that leaves the channel and comes back
   * spoils the window on either side of its absence. */
  static constexpr unsigned kWindowsJudged = 8;
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
    const unsigned failed = _failed;
    _counted = _failed = 0;
    /* A rate that is too fast still gets some frames across; a window in
     * which none did is the station away from the channel with an aggregate
     * or two in the air, whenever it was last heard. */
    if (failed == kWindowFrames) return;
    _recent[_windows++ % kWindowsJudged] = failed;
    unsigned lately = 0;
    for (const unsigned in_window : _recent) lately += in_window;
    const bool clean = failed * 10 <= kWindowFrames;
    _clean_windows = clean ? _clean_windows + 1 : 0;
    if (_clean_windows == kWindowsToForgive) _blocks[_level] = kFirstBlock;
    if (lately * 2 >= kWindowsJudged * kWindowFrames && _level > 0) {
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
    _counted = _failed = _clean_windows = _probing = _windows = 0;
    _recent.fill(0);
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
  unsigned _clean_windows = 0, _probing = 0, _windows = 0;
  std::array<unsigned, kWindowsJudged> _recent{}; /* failures per window */
  uint64_t _climbs = 0, _falls = 0;
};

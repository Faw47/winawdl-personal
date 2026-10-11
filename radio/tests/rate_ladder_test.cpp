// SPDX-License-Identifier: GPL-2.0-only
/* RateLadder without hardware: frames are only their outcomes. */
#include <cstdio>

#include "rate_ladder.h"

namespace {

using Clock = RateLadder::Clock;
using std::chrono::milliseconds;
using std::chrono::seconds;

int failures = 0;

void expect(bool condition, const char *what) {
  std::printf("%s  %s\n", condition ? "ok  " : "FAIL", what);
  if (!condition) failures++;
}

/* Sends `count` frames at whatever level the ladder says and reports each as
 * acknowledged or given up on, with the station there. */
void run(RateLadder &ladder, unsigned count, bool given_up,
         Clock::time_point now) {
  for (unsigned i = 0; i < count; i++)
    ladder.finished(ladder.next(now), given_up, true, now);
}

constexpr unsigned kClimb =
    RateLadder::kWindowFrames * RateLadder::kCleanWindowsToClimb;

} // namespace

int main() {
  const Clock::time_point start = Clock::time_point{} + seconds(100);

  {
    RateLadder ladder(3);
    expect(ladder.next(start) == 0, "the first frame goes at the slowest rate");
    run(ladder, kClimb - 1, false, start);
    expect(ladder.next(start) == 0, "one frame short of the windows it stays");
    run(ladder, 1, false, start);
    expect(ladder.next(start) == 1, "acknowledged windows climb one level");
    run(ladder, kClimb, false, start);
    expect(ladder.next(start) == 2, "and another");
    run(ladder, 10 * kClimb, false, start);
    expect(ladder.next(start) == 2 && ladder.climbs() == 2,
           "the top level is where it stops");
    expect(ladder.delivered()[0] == kClimb && ladder.delivered()[1] == kClimb,
           "delivered frames are counted per level");
  }
  {
    RateLadder ladder(2);
    run(ladder, kClimb, false, start);
    run(ladder, RateLadder::kProbeFrames, true, start);
    expect(ladder.next(start) == 0 && ladder.falls() == 1,
           "a level whose first frames all fail is left at once");
    run(ladder, 4 * kClimb, false, start + seconds(1));
    expect(ladder.next(start + seconds(1)) == 0,
           "and is not tried again while it is blocked");
    const auto later = start + RateLadder::kFirstBlock;
    run(ladder, RateLadder::kWindowFrames, false, later);
    expect(ladder.next(later) == 1, "after the block it is tried again");
    run(ladder, RateLadder::kProbeFrames, true, later);
    run(ladder, 4 * kClimb, false, later + RateLadder::kFirstBlock);
    expect(ladder.next(later + RateLadder::kFirstBlock) == 0,
           "a second failure blocks it for longer");
  }
  {
    RateLadder ladder(2);
    run(ladder, kClimb + 1, false, start);
    run(ladder, RateLadder::kWindowFrames, true, start);
    expect(ladder.next(start) == 1, "one bad window is not enough to fall");
    run(ladder, RateLadder::kWindowFrames, false, start);
    run(ladder, RateLadder::kWindowFrames, true, start);
    expect(ladder.next(start) == 1, "nor two with a good one between them");
    run(ladder, RateLadder::kWindowFrames, true, start);
    expect(ladder.next(start) == 0, "two in a row are");
  }
  {
    RateLadder ladder(2);
    run(ladder, kClimb + 1, false, start);
    for (unsigned i = 0; i < 20 * RateLadder::kWindowFrames; i++)
      ladder.finished(1, true, false, start);
    expect(ladder.next(start) == 1 && ladder.falls() == 0,
           "frames lost while the station was away change nothing");
    for (unsigned i = 0; i < 20 * RateLadder::kWindowFrames; i++)
      ladder.finished(0, true, true, start);
    expect(ladder.next(start) == 1,
           "nor do late reports about frames sent at another level");
  }
  {
    RateLadder ladder(2);
    run(ladder, kClimb, false, start);
    const auto resumed = start + RateLadder::kPauseThatResets + milliseconds(1);
    expect(ladder.next(start + milliseconds(500)) == 1,
           "a short pause keeps the level");
    expect(ladder.next(resumed + milliseconds(500)) == 0,
           "a long one goes back to the slowest rate");
  }
  {
    RateLadder ladder(1);
    run(ladder, 10 * kClimb, true, start);
    expect(ladder.next(start) == 0 && ladder.falls() == 0,
           "a ladder of one level has nowhere to go");
  }
  return failures ? 1 : 0;
}

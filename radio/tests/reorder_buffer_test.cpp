// SPDX-License-Identifier: GPL-2.0-only
/* ReorderBuffer without hardware. Each frame is one byte, its number. */
#include <cstdio>
#include <vector>

#include "reorder_buffer.h"

namespace {

using Clock = ReorderBuffer::Clock;
using std::chrono::milliseconds;

int failures = 0;

void expect(bool condition, const char *what) {
  std::printf("%s  %s\n", condition ? "ok  " : "FAIL", what);
  if (!condition) failures++;
}

/* Feeds `sequences` at one instant and returns the numbers delivered. */
std::vector<uint8_t> feed(ReorderBuffer &buffer,
                          const std::vector<uint16_t> &sequences,
                          Clock::time_point now) {
  std::vector<ReorderBuffer::Message> out;
  for (const uint16_t sequence : sequences)
    buffer.push(sequence, {static_cast<uint8_t>(sequence)}, now, out);
  std::vector<uint8_t> numbers;
  for (const auto &message : out) numbers.push_back(message[0]);
  return numbers;
}

std::vector<uint8_t>
numbers_of(const std::vector<ReorderBuffer::Message> &out) {
  std::vector<uint8_t> numbers;
  for (const auto &message : out) numbers.push_back(message[0]);
  return numbers;
}

using Numbers = std::vector<uint8_t>;

} // namespace

int main() {
  const Clock::time_point start{};
  std::vector<ReorderBuffer::Message> out;

  ReorderBuffer buffer;
  buffer.start(10, out);
  expect(feed(buffer, {10, 11, 12}, start) == Numbers{10, 11, 12},
         "frames in order pass straight through");
  expect(feed(buffer, {14, 15}, start).empty(),
         "frames after a missing one are held");
  expect(feed(buffer, {13}, start) == Numbers{13, 14, 15} &&
             buffer.waited() == 2,
         "the missing frame releases them in order");
  expect(feed(buffer, {12}, start) == Numbers{12},
         "a frame from before the window is passed on, not held");
  expect(feed(buffer, {17, 17, 18}, start).empty() &&
             feed(buffer, {16}, start) == Numbers{16, 17, 18},
         "a frame received twice is delivered once");

  /* 19 never comes. */
  expect(feed(buffer, {20, 21}, start).empty(), "a gap holds frames back");
  out.clear();
  buffer.expire(start + milliseconds(99), out);
  expect(out.empty(), "before the hold time nothing is given up");
  buffer.expire(start + milliseconds(100), out);
  expect(numbers_of(out) == Numbers{20, 21},
         "after it the frames behind the gap are delivered");
  expect(feed(buffer, {22}, start) == Numbers{22},
         "and the session carries on after them");

  expect(feed(buffer, {24, 26}, start).empty(), "two gaps hold two frames");
  out.clear();
  buffer.advance_to(26, out);
  expect(numbers_of(out) == Numbers{24, 26},
         "a window moved by the sender releases what it passes and reaches");
  expect(feed(buffer, {28}, start).empty(), "the next gap holds again");
  out.clear();
  buffer.stop(out);
  expect(numbers_of(out) == Numbers{28} && !buffer.active(),
         "stopping releases what is still held");

  ReorderBuffer far;
  far.start(0, out);
  out.clear();
  expect(feed(far, {1, 2}, start).empty() &&
             feed(far, {70}, start) == Numbers{1, 2},
         "a frame beyond the window pushes out what no longer fits");
  expect(feed(far, {8}, start).empty() && feed(far, {6}, start) == Numbers{6} &&
             feed(far, {7}, start) == Numbers{7, 8},
         "the window then starts 63 frames before that frame");

  ReorderBuffer wrap;
  wrap.start(4094, out);
  std::vector<ReorderBuffer::Message> wrapped;
  wrap.push(4095, {1}, start, wrapped);
  wrap.push(0, {2}, start, wrapped);
  wrap.push(4094, {0}, start, wrapped);
  expect(numbers_of(wrapped) == Numbers{0, 1, 2},
         "sequence numbers wrap from 4095 to 0");

  return failures ? 1 : 0;
}

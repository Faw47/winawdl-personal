// SPDX-License-Identifier: GPL-2.0-only
/* OutgoingSession and the frames it needs, without hardware. */
#include <cstdio>

#include "outgoing_session.h"

namespace {

using Clock = OutgoingSession::Clock;
using std::chrono::milliseconds;
using std::chrono::seconds;

constexpr block_ack::Mac kOwn = {0x02, 0x11, 0x22, 0x33, 0x44, 0x55};
constexpr block_ack::Mac kPeer = {0x52, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE};
constexpr block_ack::Mac kOther = {0x52, 0x01, 0x02, 0x03, 0x04, 0x05};
constexpr block_ack::Mac kBssid = {0x00, 0x25, 0x00, 0xFF, 0x94, 0x73};

int failures = 0;

void expect(bool condition, const char *what) {
  std::printf("%s  %s\n", condition ? "ok  " : "FAIL", what);
  if (!condition) failures++;
}

/* Sends frames to `station` until the session asks, and returns the ask. */
std::optional<OutgoingSession::Ask>
frames_until_ask(OutgoingSession &session, const block_ack::Mac &station,
                 Clock::time_point now, unsigned most) {
  for (unsigned i = 0; i < most; i++)
    if (const auto ask = session.before_frame(station, now)) return ask;
  return std::nullopt;
}

block_ack::Answer answer_to(const OutgoingSession::Ask &ask, bool accepted,
                            uint16_t buffer_frames) {
  return {.peer = ask.station,
          .dialog_token = ask.dialog_token,
          .tid = OutgoingSession::kTid,
          .accepted = accepted,
          .buffer_frames = buffer_frames};
}

} // namespace

int main() {
  const Clock::time_point start = Clock::time_point{} + seconds(100);
  const unsigned before = OutgoingSession::kFramesBeforeAsking;

  {
    OutgoingSession session;
    expect(!frames_until_ask(session, kPeer, start, before - 1),
           "a few frames to a station do not make it ask");
    const auto ask = session.before_frame(kPeer, start);
    expect(ask && ask->station == kPeer && ask->start_sequence == 0,
           "enough of them do, from the first number it will use");
    expect(!frames_until_ask(session, kPeer, start + milliseconds(500), 100),
           "it does not ask again right away");
    expect(!session.number(kPeer) && !session.window_start(std::nullopt),
           "nothing is numbered before the answer");

    auto wrong = answer_to(*ask, true, 64);
    wrong.dialog_token++;
    expect(!session.answered(wrong), "an answer to another request is ignored");
    expect(session.answered(answer_to(*ask, true, 32)) == 32,
           "the answer opens the session with the station's window");
    expect(session.number(kPeer) == 0 && session.number(kPeer) == 1,
           "frames to it are then numbered in order");
    expect(!session.number(kOther) && !session.before_frame(kOther, start),
           "another station neither gets numbers nor takes the session");
    const auto held = session.window_start(uint16_t{1});
    const auto none_held = session.window_start(std::nullopt);
    expect(held && held->first == kPeer && held->second == 1 && none_held &&
               none_held->second == 2,
           "its window starts at the oldest frame still held, or the next");

    expect(!session.ended({.peer = kPeer, .tid = 0, .by_sender = true}),
           "the station ending its own sending leaves ours open");
    expect(session.ended({.peer = kPeer, .tid = 0, .by_sender = false}) &&
               !session.number(kPeer),
           "the station ending its receiving closes it");
    const auto again =
        frames_until_ask(session, kPeer, start + seconds(5), before);
    expect(again && again->start_sequence == 2,
           "and it is asked again, from where the numbers stopped");
  }
  {
    OutgoingSession session;
    const auto ask = frames_until_ask(session, kPeer, start, before);
    expect(!session.answered(answer_to(*ask, false, 64)) &&
               !frames_until_ask(session, kPeer, start + seconds(60), 1000),
           "a station that declines is not asked again");
    expect(frames_until_ask(session, kOther, start + seconds(60), before)
               .has_value(),
           "but another station is");
  }
  {
    OutgoingSession session;
    unsigned asks = 0;
    for (unsigned second = 0; second < 10; second++)
      asks += frames_until_ask(session, kPeer, start + seconds(second), before)
                  .has_value();
    expect(asks == OutgoingSession::kMostAsks,
           "a station that never answers is asked a few times only");
  }
  {
    OutgoingSession session;
    const auto ask = frames_until_ask(session, kPeer, start, before);
    expect(session.answered(answer_to(*ask, true, 0)) ==
               block_ack::kMaxBufferFrames,
           "a window of no stated size is the widest");
    for (unsigned i = 0; i < OutgoingSession::kSequenceMask; i++)
      session.number(kPeer);
    expect(session.number(kPeer) == OutgoingSession::kSequenceMask &&
               session.number(kPeer) == 0,
           "numbers wrap after twelve bits");
  }

  {
    WindowNotice notice;
    WindowNotice::Heard heard{start, start, 0};
    expect(!notice.due(heard, start), "nothing is told before a frame is lost");
    notice.needed(start);
    expect(notice.due(heard, start), "a lost frame is told about at once");
    notice.told(100, start);
    expect(!notice.due(heard, start + milliseconds(50)),
           "it is not told again while the station stays unheard");
    heard.station = start + milliseconds(400);
    expect(notice.due(heard, start + milliseconds(401)),
           "it is told again once the station is back");
    notice.told(100, start + milliseconds(401));
    heard.station = heard.block_ack = start + milliseconds(402);
    heard.block_ack_start = 90;
    expect(notice.due(heard, start + milliseconds(410)),
           "a block ack that starts before the lost frame settles nothing");
    notice.told(100, start + milliseconds(410));
    heard.station = heard.block_ack = start + milliseconds(411);
    heard.block_ack_start = 100;
    expect(!notice.due(heard, start + milliseconds(420)) &&
               !notice.due(heard, start + seconds(1)),
           "one that starts at it ends the telling");
    expect(notice.tellings() == 3, "each telling is counted");
  }
  {
    WindowNotice notice;
    WindowNotice::Heard heard{start, start, 0};
    notice.needed(start);
    notice.told(4090, start);
    heard.station = heard.block_ack = start + milliseconds(1);
    heard.block_ack_start = 3;
    expect(!notice.due(heard, start + milliseconds(10)),
           "a window start past the wrap counts as after");
    notice.needed(start + seconds(1));
    notice.told(50, start + seconds(1));
    heard.station = start + seconds(5);
    expect(!notice.due(heard, start + seconds(5)),
           "a station that never shows it heard is given up on");
  }

  const auto asked = block_ack::request(kPeer, kOwn, kBssid, 0, 7, 0x123);
  const size_t frame = asked[2];
  const auto echoed = block_ack::parse_request(
      std::span<const uint8_t>(asked).subspan(frame), kPeer);
  expect(echoed && echoed->peer == kOwn && echoed->dialog_token == 7 &&
             echoed->tid == 0 && echoed->start_sequence == 0x123 &&
             echoed->buffer_frames == block_ack::kMaxBufferFrames,
         "our request reads back as one for the widest window");
  expect(asked[frame + 24 + 3] == 0x02 && asked[frame + 24 + 4] == 0x10,
         "it asks for immediate block ack without A-MSDU");

  const auto moved = block_ack::window_start(kPeer, kOwn, 0, 0x123);
  const size_t bar = moved[2];
  expect(moved.size() == bar + 20 && moved[bar] == 0x84 &&
             std::equal(kPeer.begin(), kPeer.end(), moved.begin() + bar + 4) &&
             std::equal(kOwn.begin(), kOwn.end(), moved.begin() + bar + 10),
         "a block-ack request goes from us to the station");
  expect(moved[bar + 16] == 0x04 && moved[bar + 17] == 0x00 &&
             moved[bar + 18] == 0x30 && moved[bar + 19] == 0x12,
         "in the compressed form, with the number the window starts at");

  /* Token 7, success, immediate, TID 0, 64 frames, no timeout. */
  std::vector<uint8_t> granted = {0xD0, 0, 0, 0};
  granted.insert(granted.end(), kOwn.begin(), kOwn.end());
  granted.insert(granted.end(), kPeer.begin(), kPeer.end());
  granted.insert(granted.end(), kBssid.begin(), kBssid.end());
  granted.insert(granted.end(), {0, 0, 3, 1, 7, 0, 0, 0x02, 0x10, 0, 0});
  const auto answer = block_ack::parse_response(granted, kOwn);
  expect(answer && answer->peer == kPeer && answer->dialog_token == 7 &&
             answer->accepted && answer->buffer_frames == 64,
         "an ADDBA response to us is read");
  granted[24 + 3] = 37;
  expect(!block_ack::parse_response(granted, kOwn)->accepted,
         "a status other than success is a refusal");
  expect(!block_ack::parse_response(granted, kPeer),
         "a response to someone else is not for us");

  std::vector<uint8_t> plain = {0x08, 0x00, 0x00, 0x00};
  plain.insert(plain.end(), kPeer.begin(), kPeer.end());
  plain.insert(plain.end(), kOwn.begin(), kOwn.end());
  plain.insert(plain.end(), kBssid.begin(), kBssid.end());
  plain.insert(plain.end(), {0x70, 0x01, 0xAA, 0xBB, 0xCC});
  const auto qos = as_qos_data(plain, 0, 0xABC);
  expect(qos.size() == plain.size() + 2 && qos[0] == 0x88 && qos[22] == 0xC0 &&
             qos[23] == 0xAB && qos[24] == 0 && qos[25] == 0,
         "a data frame becomes QoS data with the session's number");
  expect(std::equal(plain.begin() + 4, plain.begin() + 22, qos.begin() + 4) &&
             std::equal(plain.begin() + 24, plain.end(), qos.begin() + 26),
         "with its addresses and its body untouched");
  plain[0] = 0x48;
  expect(as_qos_data(plain, 0, 1).empty(), "a null frame is left alone");
  plain[0] = 0x08;
  plain[1] = 0x01;
  expect(as_qos_data(plain, 0, 1).empty(),
         "and so is a frame for a distribution system");

  return failures ? 1 : 0;
}

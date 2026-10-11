// SPDX-License-Identifier: GPL-2.0-only
/* The block-ack frames without hardware. The request is the one an iPhone
 * sent on 2026-10-11, with other addresses. */
#include <cstdio>
#include <vector>

#include "block_ack.h"

namespace {

const block_ack::Mac kOwn = {0x02, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE};
const block_ack::Mac kPeer = {0x52, 0x00, 0x5E, 0x00, 0x53, 0x01};
const block_ack::Mac kBssid = {0x00, 0x25, 0x00, 0xFF, 0x94, 0x73};

int failures = 0;

void expect(bool condition, const char *what) {
  std::printf("%s  %s\n", condition ? "ok  " : "FAIL", what);
  if (!condition) failures++;
}

/* A management action frame from the peer to us with this body and a
 * four-byte checksum behind it. */
std::vector<uint8_t> action_frame(std::vector<uint8_t> body) {
  std::vector<uint8_t> frame = {0xD0, 0x00, 0x00, 0x00};
  frame.insert(frame.end(), kOwn.begin(), kOwn.end());
  frame.insert(frame.end(), kPeer.begin(), kPeer.end());
  frame.insert(frame.end(), kBssid.begin(), kBssid.end());
  frame.insert(frame.end(), {0x00, 0x00});
  frame.insert(frame.end(), body.begin(), body.end());
  frame.insert(frame.end(), {0xDE, 0xAD, 0xBE, 0xEF});
  return frame;
}

} // namespace

int main() {
  /* Token 0x25, A-MSDU allowed, immediate, TID 0, 64 frames, from 14. */
  const auto asked =
      action_frame({0x03, 0x00, 0x25, 0x03, 0x10, 0x00, 0x00, 0xE0, 0x00});
  const auto request = block_ack::parse_request(asked, kOwn);
  expect(request.has_value(), "an ADDBA request to us is recognised");
  if (!request) return 1;
  expect(request->peer == kPeer && request->dialog_token == 0x25,
         "it names its sender and its token");
  expect(request->tid == 0 && request->buffer_frames == 64 &&
             request->start_sequence == 14,
         "it gives the TID, the window and the first sequence number");

  expect(!block_ack::parse_request(asked, kPeer),
         "a request to another station is not ours");
  auto other = asked;
  other[25] = 0x01;
  expect(!block_ack::parse_request(other, kOwn),
         "an ADDBA response is not a request");
  other = asked;
  other[24] = 0x7F;
  expect(!block_ack::parse_request(other, kOwn),
         "another action category is not a request");
  expect(!block_ack::parse_request(std::span(asked).first(30), kOwn),
         "a cut-off frame is not a request");

  const auto answer = block_ack::response(*request, kOwn, kBssid);
  const size_t frame = 9;
  expect(answer.size() == frame + 24 + 9 && answer[2] == 9 && answer[8] == 24,
         "the answer has a radiotap header at 12 Mbit/s");
  expect(
      answer[frame] == 0xD0 &&
          std::equal(kPeer.begin(), kPeer.end(), answer.begin() + frame + 4) &&
          std::equal(kOwn.begin(), kOwn.end(), answer.begin() + frame + 10) &&
          std::equal(kBssid.begin(), kBssid.end(), answer.begin() + frame + 16),
      "it goes from us to the peer in the same BSS");
  const size_t body = frame + 24;
  expect(answer[body] == 3 && answer[body + 1] == 1 &&
             answer[body + 2] == 0x25 && answer[body + 3] == 0 &&
             answer[body + 4] == 0,
         "it accepts, echoing the token");
  expect(answer[body + 5] == 0x02 && answer[body + 6] == 0x10,
         "it grants 64 frames on TID 0, immediate, without A-MSDU");

  auto wide = *request;
  wide.buffer_frames = 256;
  wide.tid = 5;
  const auto narrowed = block_ack::response(wide, kOwn, kBssid);
  expect(narrowed[body + 5] == 0x16 && narrowed[body + 6] == 0x10,
         "a wider request is granted 64 frames on its own TID");

  const auto goodbye = block_ack::delba(wide, kOwn, kBssid);
  expect(goodbye.size() == frame + 24 + 6 && goodbye[body] == 3 &&
             goodbye[body + 1] == 2 && goodbye[body + 2] == 0 &&
             goodbye[body + 3] == 0x50 && goodbye[body + 4] == 37,
         "our DELBA ends that TID as its recipient");
  const auto echoed =
      block_ack::parse_delba(std::span(goodbye).subspan(frame), kPeer);
  expect(echoed && echoed->first == kOwn && echoed->second == 5,
         "and reads back as a DELBA from us");

  const auto ended = action_frame({0x03, 0x02, 0x00, 0x58, 0x01, 0x00});
  const auto delba = block_ack::parse_delba(ended, kOwn);
  expect(delba && delba->first == kPeer && delba->second == 5,
         "a DELBA names its sender and its TID");
  expect(!block_ack::parse_delba(asked, kOwn), "a request is not a DELBA");

  return failures ? 1 : 0;
}

// SPDX-License-Identifier: GPL-2.0-only
/* Radio daemon: owns the Wi-Fi card through devourer and relays frames for one
 * client over the pipe in radio_link.h.
 * Usage: winawdl_radio <channel> [--width 20|40|80] [--capture file.pcap]
 *        [--no-block-ack] [--retries 0..62]
 *        [--data-rate auto|client|MCS7/20] */

#include <array>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <memory>
#include <mutex>
#include <optional>
#include <span>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

#include "PcieTransport.h"
#include "RadiotapBuilder.h"
#include "RxPacket.h"
#include "SelectedChannel.h"
#include "SignalStop.h"
#include "WiFiDriver.h"
#include "block_ack.h"
#include "frame_capture.h"
#include "logger.h"
#include "pipe_server.h"
#include "radio_link.h"
#include "rate_ladder.h"
#include "reorder_buffer.h"

namespace {

using Mac = std::array<uint8_t, 6>;

constexpr size_t kMacHeaderBytes = 24;
constexpr size_t kReceiverOffset = 4;
constexpr size_t kBssidOffset = 16;
constexpr uint8_t kFrameTypeMask = 0x0C;
constexpr uint8_t kFrameTypeData = 0x08;
constexpr uint8_t kRetryFlag = 0x08;
constexpr uint8_t kLast2GChannel = 14;
/* How often a station usually repeats an unacknowledged frame. devourer's
 * default is never: its video links are broadcast. */
constexpr int kDefaultRetries = 7;
constexpr int kMostRetries = 62;
constexpr size_t kSenderOffset = 10;
constexpr size_t kSequenceOffset = 22;
constexpr uint8_t kFrameControlQosData = 0x88;
constexpr size_t kQosControlOffset = 24;
constexpr uint8_t kFrameControlBlockAckRequest = 0x84;
/* In a block-ack request: its control field, then the sequence number the
 * sender's window now starts at. */
constexpr size_t kBarControlOffset = 16;
constexpr size_t kBarSequenceOffset = 18;
constexpr size_t kBarBytes = 20;
constexpr uint8_t kGroupAddressBit = 0x01;
constexpr uint8_t kFrameControlAck = 0xD4;
constexpr size_t kAckBytes = 10;
/* A station counts as there for this long after it was last heard. */
constexpr std::chrono::milliseconds kStationHeardRecently{20};
/* More frames than the card holds at once: a longer queue has lost count. */
constexpr size_t kMostInFlight = 4096;

/* Radiotap header put on received frames: FLAGS and DBM_ANTSIGNAL only. */
constexpr uint8_t kRadiotapBytes = 10;
constexpr uint32_t kRadiotapPresent = (1u << 1) | (1u << 5);
constexpr uint8_t kRadiotapFlagFcsAtEnd = 0x10;
/* devourer reports RSSI as dB above -110 dBm on this chip family. */
constexpr int kRssiFloorDbm = -110;

bool stopping() { return g_devourer_should_stop; }

/* One MAC address written by the pipe thread and read per frame by the
 * receive thread, so it is packed into one atomic word. */
class SharedMac {
public:
  void set(const Mac &mac) {
    uint64_t packed = 0;
    std::memcpy(&packed, mac.data(), mac.size());
    _packed = packed;
  }
  bool is_set() const { return _packed != 0; }
  Mac get() const {
    const uint64_t packed = _packed;
    Mac mac;
    std::memcpy(mac.data(), &packed, mac.size());
    return mac;
  }
  bool equals(const uint8_t *six_bytes) const {
    uint64_t other = 0;
    std::memcpy(&other, six_bytes, sizeof(Mac));
    return other == _packed;
  }

private:
  std::atomic<uint64_t> _packed{0};
};

/* What the receive thread saw of block ack, for the pipe thread to act on:
 * only that one talks to the card. */
class BlockAckInbox {
public:
  using Ended = std::pair<Mac, uint8_t>;

  void asked(const block_ack::Request &request) {
    const std::lock_guard<std::mutex> lock(_mutex);
    _request = request;
  }
  void ended(const Ended &session) {
    const std::lock_guard<std::mutex> lock(_mutex);
    _ended = session;
  }
  std::optional<block_ack::Request> take_request() {
    const std::lock_guard<std::mutex> lock(_mutex);
    return std::exchange(_request, std::nullopt);
  }
  std::optional<Ended> take_ended() {
    const std::lock_guard<std::mutex> lock(_mutex);
    return std::exchange(_ended, std::nullopt);
  }

private:
  std::mutex _mutex;
  std::optional<block_ack::Request> _request;
  std::optional<Ended> _ended;
};

/* Another sender gets the one block-ack session only once it has been open
 * this long and its holder has sent fewer than kBusyFrames since that sender
 * last asked. */
constexpr std::chrono::seconds kSessionSettle{1};
constexpr uint64_t kBusyFrames = 16;

/* The reorder buffer of the open block-ack session. The receive thread fills
 * it; the pipe thread starts, stops and expires it. Frames leave for the
 * client under its lock, so in the order it decided. */
class SessionReorder {
public:
  void start(const Mac &peer, uint8_t tid, uint16_t first_sequence,
             PipeServer &pipe) {
    const std::lock_guard<std::mutex> lock(_mutex);
    _peer = peer;
    _tid = tid;
    _recent = 0;
    _buffer.start(first_sequence, _out);
    send(pipe);
  }
  /* Frames the session's sender delivered since this was last asked. */
  uint64_t take_recent() {
    const std::lock_guard<std::mutex> lock(_mutex);
    return std::exchange(_recent, 0);
  }
  void stop(PipeServer &pipe) {
    const std::lock_guard<std::mutex> lock(_mutex);
    _buffer.stop(_out);
    send(pipe);
  }
  void expire(PipeServer &pipe) {
    const std::lock_guard<std::mutex> lock(_mutex);
    if (!_buffer.active()) return;
    _buffer.expire(ReorderBuffer::Clock::now(), _out);
    send(pipe);
  }
  /* Takes `message`, a data frame ready for the client, if `frame` belongs
   * to the session. Otherwise the caller sends it. */
  bool take(std::span<const uint8_t> frame, const std::vector<uint8_t> &message,
            PipeServer &pipe) {
    const std::lock_guard<std::mutex> lock(_mutex);
    if (!_buffer.active() || frame.size() < kQosControlOffset + 2 ||
        frame[0] != kFrameControlQosData || !from_peer(frame) ||
        (frame[kQosControlOffset] & 0x0F) != _tid)
      return false;
    _recent++;
    _buffer.push(sequence_at(frame, kSequenceOffset), message,
                 ReorderBuffer::Clock::now(), _out);
    send(pipe);
    return true;
  }
  /* A block-ack request from the session's sender moves its window. */
  void window_moved(std::span<const uint8_t> frame, PipeServer &pipe) {
    const std::lock_guard<std::mutex> lock(_mutex);
    if (!_buffer.active() || frame.size() < kBarBytes ||
        frame[0] != kFrameControlBlockAckRequest || !from_peer(frame) ||
        (frame[kBarControlOffset + 1] >> 4) != _tid)
      return;
    _buffer.advance_to(sequence_at(frame, kBarSequenceOffset), _out);
    send(pipe);
  }
  uint64_t waited() {
    const std::lock_guard<std::mutex> lock(_mutex);
    return _buffer.waited();
  }

private:
  bool from_peer(std::span<const uint8_t> frame) const {
    return std::memcmp(frame.data() + kSenderOffset, _peer.data(),
                       _peer.size()) == 0;
  }
  static uint16_t sequence_at(std::span<const uint8_t> frame, size_t offset) {
    return static_cast<uint16_t>((frame[offset] | frame[offset + 1] << 8) >> 4);
  }
  void send(PipeServer &pipe) {
    for (const auto &message : _out) pipe.send(RADIO_LINK_RX, message);
    _out.clear();
  }

  std::mutex _mutex;
  ReorderBuffer _buffer;
  std::vector<ReorderBuffer::Message> _out;
  Mac _peer{};
  uint8_t _tid = 0;
  uint64_t _recent = 0;
};

/* A rate by the name it is asked for, and the radiotap header that makes
 * devourer use it. No header leaves a frame at the rate its client chose. */
struct DataRate {
  std::string name;
  std::vector<uint8_t> radiotap;
};

int64_t micros(RateLadder::Clock::time_point time) {
  return std::chrono::duration_cast<std::chrono::microseconds>(
             time.time_since_epoch())
      .count();
}

struct Session {
  SharedMac bssid_filter; /* unset forwards every frame */
  SharedMac own;
  /* The rates data frames to one station may go at, slowest first. */
  std::vector<DataRate> data_rates;
  /* The station such frames last went to and when it was last heard, in
   * microseconds of the steady clock: the receive thread keeps the time for
   * the pipe thread, which has to judge the frames the card gave up on. */
  SharedMac data_station;
  std::atomic<int64_t> station_heard_us{0};
  bool block_ack_enabled = false;
  uint8_t width_mhz = 20;
  BlockAckInbox block_ack;
  SessionReorder reorder;
  /* Data frames addressed to the client and how many were retries. A high
   * share means the card is not acknowledging for the client's address. */
  std::atomic<uint64_t> for_us{0};
  std::atomic<uint64_t> for_us_retried{0};
};

/* Notes that the station data frames go to is there: a frame from it, or
 * an acknowledgement of one of ours, which names no sender. */
void note_station(std::span<const uint8_t> frame, Session &session) {
  const bool acknowledged = frame.size() >= kAckBytes &&
                            frame[0] == kFrameControlAck &&
                            session.own.equals(frame.data() + kReceiverOffset);
  const bool from_station =
      frame.size() >= kSenderOffset + sizeof(Mac) &&
      session.data_station.is_set() &&
      session.data_station.equals(frame.data() + kSenderOffset);
  if (acknowledged || from_station)
    session.station_heard_us = micros(RateLadder::Clock::now());
}

void forward_received(const Packet &packet, PipeServer &pipe, Session &session,
                      FrameCapture *capture, std::vector<uint8_t> &message) {
  const std::span<const uint8_t> frame{packet.Data.data(), packet.Data.size()};
  if (!pipe.connected()) return;
  const int dbm = kRssiFloorDbm + packet.RxAtrib.rssi[0];
  if (capture)
    capture->received(frame, {.rate = packet.RxAtrib.data_rate,
                              .bandwidth = packet.RxAtrib.bw,
                              .ppdu_type = packet.RxAtrib.ppdu_type,
                              .dbm = static_cast<int8_t>(dbm),
                              .fcs_at_end = packet.RxAtrib.fcs_present,
                              .bad_fcs = packet.RxAtrib.crc_err});
  if (packet.RxAtrib.crc_err) return;
  note_station(frame, session);
  if (frame.size() < kMacHeaderBytes) return;
  if (session.block_ack_enabled && session.own.is_set()) {
    const Mac own = session.own.get();
    if (const auto request = block_ack::parse_request(frame, own))
      session.block_ack.asked(*request);
    else if (const auto ended = block_ack::parse_delba(frame, own))
      session.block_ack.ended(*ended);
    session.reorder.window_moved(frame, pipe);
    session.reorder.expire(pipe);
  }
  if ((frame[0] & kFrameTypeMask) == kFrameTypeData &&
      session.own.equals(frame.data() + kReceiverOffset)) {
    session.for_us++;
    if (frame[1] & kRetryFlag) session.for_us_retried++;
  }
  if (session.bssid_filter.is_set() &&
      !session.bssid_filter.equals(frame.data() + kBssidOffset))
    return;

  message.assign(kRadiotapBytes, 0);
  message[2] = kRadiotapBytes;
  std::memcpy(&message[4], &kRadiotapPresent, sizeof(kRadiotapPresent));
  message[8] = packet.RxAtrib.fcs_present ? kRadiotapFlagFcsAtEnd : 0;
  message[9] = static_cast<uint8_t>(static_cast<int8_t>(dbm));
  message.insert(message.end(), frame.begin(), frame.end());
  if (message.size() > RADIO_LINK_MAX_PAYLOAD) return;
  if (!session.reorder.take(frame, message, pipe))
    pipe.send(RADIO_LINK_RX, message);
}

/* Whether a frame to transmit, radiotap header first, names one receiver. */
bool is_for_one_station(std::span<const uint8_t> message) {
  constexpr size_t kRadiotapLengthOffset = 2;
  if (message.size() < kRadiotapLengthOffset + 2) return false;
  const size_t radiotap =
      message[kRadiotapLengthOffset] |
      static_cast<size_t>(message[kRadiotapLengthOffset + 1]) << 8;
  return message.size() > radiotap + kReceiverOffset &&
         !(message[radiotap + kReceiverOffset] & kGroupAddressBit);
}

/* Where the 802.11 frame starts in a data frame to one station, radiotap
 * header first; 0 for any other frame. */
size_t station_data_offset(std::span<const uint8_t> message) {
  if (!is_for_one_station(message)) return 0;
  const size_t radiotap = message[2] | static_cast<size_t>(message[3]) << 8;
  return (message[radiotap] & kFrameTypeMask) == kFrameTypeData ? radiotap : 0;
}

/* Chooses the rate of each data frame to a station and follows what the card
 * reports became of the frames. Only the pipe thread uses it. */
class RateControl {
public:
  RateControl(IRadio &radio, Session &session)
      : _radio(radio), _session(session), _ladder(session.data_rates.size()) {
    const devourer::TxStats stats = radio.GetTxStats();
    _reported = stats.reported;
    _given_up = stats.given_up;
  }

  const RateLadder &ladder() const { return _ladder; }

  /* The frame to send for `message`: under the radiotap header of the rate
   * in use if it is a data frame to one station, in `rated`; else as it is. */
  const std::vector<uint8_t> &prepare(const std::vector<uint8_t> &message,
                                      std::vector<uint8_t> &rated) {
    collect();
    const size_t frame = station_data_offset(message);
    _next = {.rated = frame != 0, .level = 0};
    if (!frame) return message;
    Mac station;
    std::copy_n(message.begin() + frame + kReceiverOffset, station.size(),
                station.begin());
    _session.data_station.set(station);
    _next.level = _ladder.next(RateLadder::Clock::now());
    const auto &radiotap = _session.data_rates[_next.level].radiotap;
    if (radiotap.empty()) return message;
    rated.assign(radiotap.begin(), radiotap.end());
    rated.insert(rated.end(), message.begin() + frame, message.end());
    return rated;
  }

  void not_sent() { _next = {}; }

  /* The card took the frame last prepared, or one that was not prepared. */
  void sent() {
    if (_in_flight.size() >= kMostInFlight) _in_flight.clear();
    _in_flight.push_back(std::exchange(_next, {}));
  }

private:
  struct InFlight {
    bool rated = false;
    size_t level = 0;
  };

  /* Takes the card's reports since the last call. It reports frames in the
   * order it finished them and says how many it gave up on, not which. */
  void collect() {
    const devourer::TxStats stats = _radio.GetTxStats();
    uint64_t finished = stats.reported - _reported;
    uint64_t given_up = stats.given_up - _given_up;
    _reported = stats.reported;
    _given_up = stats.given_up;
    const auto now = RateLadder::Clock::now();
    const bool station_present =
        micros(now) - _session.station_heard_us <=
        std::chrono::microseconds(kStationHeardRecently).count();
    for (; finished && !_in_flight.empty(); finished--) {
      const InFlight frame = _in_flight.front();
      _in_flight.pop_front();
      if (!frame.rated) continue;
      const bool lost = given_up > 0;
      given_up -= lost;
      _ladder.finished(frame.level, lost, station_present, now);
    }
  }

  IRadio &_radio;
  Session &_session;
  RateLadder _ladder;
  std::deque<InFlight> _in_flight;
  InFlight _next;
  uint64_t _reported = 0, _given_up = 0;
};

/* Makes the card acknowledge frames sent to the client, once both of the
 * addresses that takes are known. */
void adopt_identity(IRadio &radio, const Session &session, Logger_t logger) {
  if (!session.own.is_set() || !session.bssid_filter.is_set()) return;
  if (!radio.SetStationIdentity({session.own.get()},
                                {session.bssid_filter.get()}))
    logger->warn("radio: the card will not acknowledge frames for the client; "
                 "peers will repeat everything they send it");
}

/* The block-ack session the card is set up for, and when it was opened. */
struct OpenBlockAck {
  block_ack::Request request;
  ReorderBuffer::Clock::time_point since;
};

bool send_frame(IRadio &radio, FrameCapture *capture, RateControl &rates,
                const std::vector<uint8_t> &message) {
  if (!radio.send_packet(message.data(), message.size())) {
    rates.not_sent();
    return false;
  }
  rates.sent();
  if (capture) capture->transmitted(message);
  return true;
}

/* Answers a sender that asks to aggregate its frames, for one sender and
 * TID at a time: a nearby device with next to nothing to send asks too, and
 * must not keep the session from the one that is sending a file. The card is
 * set up before the answer goes out, so that it is ready for the first
 * aggregate. */
void answer_block_ack(IRadio &radio, PipeServer &pipe, Session &session,
                      FrameCapture *capture, RateControl &rates,
                      std::optional<OpenBlockAck> &open, uint64_t &agreements) {
  session.reorder.expire(pipe);
  const auto close = [&] {
    radio.EndBlockAck();
    session.reorder.stop(pipe);
    open.reset();
  };
  const auto ended = session.block_ack.take_ended();
  if (ended && open && ended->first == open->request.peer &&
      ended->second == open->request.tid)
    close();
  const auto request = session.block_ack.take_request();
  if (!request || !session.bssid_filter.is_set()) return;
  const Mac own = session.own.get(), bssid = session.bssid_filter.get();
  const auto now = ReorderBuffer::Clock::now();
  if (open && open->request.peer != request->peer) {
    const bool holder_is_quiet = session.reorder.take_recent() < kBusyFrames;
    if (now - open->since < kSessionSettle || !holder_is_quiet) return;
    send_frame(radio, capture, rates,
               block_ack::delba(open->request, own, bssid));
    close();
  } else if (open && open->request.tid != request->tid) {
    return;
  }
  if (!radio.AcceptBlockAck({request->peer}, request->tid,
                            request->start_sequence,
                            block_ack::granted_frames(*request)))
    return;
  session.reorder.start(request->peer, request->tid, request->start_sequence,
                        pipe);
  send_frame(radio, capture, rates, block_ack::response(*request, own, bssid));
  if (!open) open = OpenBlockAck{*request, now};
  open->request = *request;
  agreements++;
}

/* Serves client messages until the client leaves or the daemon stops. Returns
 * false for a client that sent nothing, such as a probe for the pipe. */
bool serve_client(PipeServer &pipe, IRadio &radio, Session &session,
                  FrameCapture *capture, uint8_t channel, Logger_t logger) {
  radio_link_hello hello{RADIO_LINK_VERSION, channel, session.width_mhz, {}};
  pipe.send(RADIO_LINK_HELLO,
            {reinterpret_cast<const uint8_t *>(&hello), sizeof(hello)});

  std::vector<uint8_t> payload, rated;
  RateControl rates(radio, session);
  uint64_t messages = 0, sent = 0, failed = 0, to_one = 0, agreements = 0;
  std::optional<OpenBlockAck> open_block_ack;
  radio_link_header header{};
  while (!stopping() && pipe.read_exact(&header, sizeof(header))) {
    messages++;
    if (header.length > RADIO_LINK_MAX_PAYLOAD) {
      logger->error("radio: client sent an oversized message ({} bytes)",
                    header.length);
      break;
    }
    payload.resize(header.length);
    if (header.length && !pipe.read_exact(payload.data(), header.length)) break;

    const bool is_address = payload.size() == sizeof(Mac);
    if (header.type == RADIO_LINK_TX) {
      const auto &frame = rates.prepare(payload, rated);
      send_frame(radio, capture, rates, frame) ? sent++ : failed++;
      to_one += is_for_one_station(payload);
    } else if (header.type == RADIO_LINK_SET_BSSID_FILTER && is_address) {
      Mac bssid;
      std::copy(payload.begin(), payload.end(), bssid.begin());
      session.bssid_filter.set(bssid);
      adopt_identity(radio, session, logger);
    } else if (header.type == RADIO_LINK_SET_ADDRESS && is_address) {
      Mac own;
      std::copy(payload.begin(), payload.end(), own.begin());
      session.own.set(own);
      adopt_identity(radio, session, logger);
    } else if (header.type == RADIO_LINK_SET_CHANNEL && payload.size() == 1 &&
               payload[0] != channel) {
      /* Retuning under a running receive loop is untested on this chip. */
      logger->warn("radio: staying on channel {}, change to {} ignored",
                   channel, payload[0]);
    }
    if (session.block_ack_enabled)
      answer_block_ack(radio, pipe, session, capture, rates, open_block_ack,
                       agreements);
  }
  radio.EndBlockAck();
  session.reorder.stop(pipe);
  if (messages) {
    const uint64_t for_us = session.for_us, retried = session.for_us_retried;
    logger->warn("radio: client gone; sent {} frames ({} to a single station), "
                 "{} failed; received {} data frames for it, {}% of them "
                 "repeats; answered {} block-ack requests, {} frames waited "
                 "for an earlier one",
                 sent, to_one, failed, for_us,
                 for_us ? retried * 100 / for_us : 0, agreements,
                 session.reorder.waited());
    std::string by_rate;
    for (size_t level = 0; level < session.data_rates.size(); level++)
      by_rate += (level ? ", " : "") +
                 std::to_string(rates.ladder().delivered()[level]) + " at " +
                 session.data_rates[level].name;
    logger->warn("radio: data frames a station acknowledged: {}; the rate "
                 "went up {} times and back down {} times",
                 by_rate, rates.ladder().climbs(), rates.ladder().falls());
  }
  return messages > 0;
}

ChannelWidth_t channel_width(uint8_t width_mhz) {
  return width_mhz == 80   ? CHANNEL_WIDTH_80
         : width_mhz == 40 ? CHANNEL_WIDTH_40
                           : CHANNEL_WIDTH_20;
}

struct Options {
  uint8_t channel = 0;
  std::string capture_path;
  bool block_ack = true;
  uint8_t width_mhz = 0; /* 0: the widest the channel allows */
  int retries = kDefaultRetries;
  /* "auto", "client" for the rate the client gives each frame, or one rate
   * in devourer's notation. */
  std::string data_rate = "auto";
};

/* The rate `spec` names, or nothing if it names none or asks for a wider
 * frame than the channel. */
std::optional<DataRate> data_rate(const std::string &spec, uint8_t width_mhz) {
  const devourer::TxMode mode = devourer::parse_tx_mode_str(spec);
  /* What the parser hands back for a spec it does not know. */
  const bool fallback = mode.mode == devourer::TxMode::Mode::Legacy &&
                        spec.rfind("6", 0) != 0 &&
                        mode.legacy_rate_500kbps == 12;
  if (fallback || mode.bw_mhz > width_mhz) return std::nullopt;
  return DataRate{spec, devourer::build_stream_radiotap(mode, false)};
}

/* The rates to choose from, slowest first. "auto" climbs from the client's
 * own rate through the ones an iPhone was measured to receive. */
std::optional<std::vector<DataRate>> data_rates(const std::string &spec,
                                                uint8_t width_mhz) {
  const DataRate as_given{"the client's rate", {}};
  if (spec == "client") return std::vector{as_given};
  if (spec != "auto") {
    const auto fixed = data_rate(spec, width_mhz);
    if (!fixed) return std::nullopt;
    return std::vector{*fixed};
  }
  std::vector rates{as_given, *data_rate("MCS7", width_mhz)};
  if (width_mhz == 80)
    rates.push_back(*data_rate("VHT2SS_MCS7/80/SGI", width_mhz));
  return rates;
}

std::optional<Options> parse_options(int argc, char **argv) {
  if (argc < 2) return std::nullopt;
  Options options;
  options.channel = static_cast<uint8_t>(std::atoi(argv[1]));
  for (int i = 2; i < argc; i++) {
    const std::string_view argument = argv[i];
    if (argument == "--no-block-ack")
      options.block_ack = false;
    else if (argument == "--capture" && i + 1 < argc)
      options.capture_path = argv[++i];
    else if (argument == "--width" && i + 1 < argc)
      options.width_mhz = static_cast<uint8_t>(std::atoi(argv[++i]));
    else if (argument == "--retries" && i + 1 < argc)
      options.retries = std::atoi(argv[++i]);
    else if (argument == "--data-rate" && i + 1 < argc)
      options.data_rate = argv[++i];
    else
      return std::nullopt;
  }
  /* 40 MHz pairs a 5 GHz channel with the one above it, 80 MHz takes its
   * block of four; 2.4 GHz stays narrow. */
  const bool wide_fits = options.channel > kLast2GChannel;
  if (options.width_mhz == 0) options.width_mhz = wide_fits ? 80 : 20;
  const bool wide = options.width_mhz == 40 || options.width_mhz == 80;
  if (options.width_mhz != 20 && !(wide && wide_fits)) return std::nullopt;
  if (options.retries < 0 || options.retries > kMostRetries)
    return std::nullopt;
  if (!data_rates(options.data_rate, options.width_mhz)) return std::nullopt;
  return options;
}

} /* namespace */

int main(int argc, char **argv) {
  const auto options = parse_options(argc, argv);
  if (!options) {
    std::fprintf(stderr,
                 "usage: %s <channel> [--width 20|40|80] [--capture file.pcap] "
                 "[--no-block-ack] [--retries 0..62] "
                 "[--data-rate auto|client|MCS7/20]\n",
                 argv[0]);
    return 2;
  }
  const uint8_t channel = options->channel;

  auto logger = std::make_shared<Logger>();
  std::unique_ptr<FrameCapture> capture;
  if (!options->capture_path.empty()) {
    capture = std::make_unique<FrameCapture>(options->capture_path);
    if (!capture->is_open()) {
      logger->error("radio: cannot write the capture file {}",
                    options->capture_path);
      return 1;
    }
  }
  install_devourer_signal_handlers();

  PipeServer pipe(RADIO_LINK_PIPE_NAME, stopping);
  if (!pipe.valid()) {
    logger->error("radio: cannot create {} ({}): already running?",
                  RADIO_LINK_PIPE_NAME, GetLastError());
    return 1;
  }

  auto transport = devourer::PcieTransport::Open("win", logger);
  if (!transport) return 1;
  WiFiDriver driver(logger);
  devourer::DeviceConfig config;
  config.tx.retry_limit = options->retries;
  auto radio = driver.CreateRadioPcie(std::move(transport), config);
  if (!radio) return 1;

  try {
    radio->InitWrite(
        SelectedChannel{.Channel = channel,
                        .ChannelOffset = 0,
                        .ChannelWidth = channel_width(options->width_mhz),
                        .Band = 0});
  } catch (const std::exception &error) {
    logger->error("radio: bring-up failed: {}", error.what());
    return 1;
  }

  Session session;
  session.block_ack_enabled = options->block_ack;
  session.width_mhz = options->width_mhz;
  session.data_rates = *data_rates(options->data_rate, options->width_mhz);
  std::thread receiver([&] {
    std::vector<uint8_t> message;
    radio->StartRxLoop([&](const Packet &packet) {
      forward_received(packet, pipe, session, capture.get(), message);
    });
  });

  logger->info("radio: ready on channel {}, waiting for a client", channel);
  /* devourer logs a firmware TX report every 100 ms at info level, which
   * would bury the client's own output while a session runs. */
  logger->set_level(Logger::Level::Warn);
  bool served = false;
  while (!stopping() && !served && pipe.wait_for_client()) {
    served =
        serve_client(pipe, *radio, session, capture.get(), channel, logger);
    pipe.drop_client();
  }
  logger->set_level(Logger::Level::Info);

  g_devourer_should_stop = true;
  radio->StopRxLoop();
  receiver.join();
  radio->Stop();
  return 0;
}

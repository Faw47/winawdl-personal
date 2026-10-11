// SPDX-License-Identifier: GPL-2.0-only
/* Radio daemon: owns the Wi-Fi card through devourer and relays frames for one
 * client over the pipe in radio_link.h.
 * Usage: winawdl_radio <channel> [--capture file.pcap] [--no-block-ack] */

#include <array>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <memory>
#include <mutex>
#include <optional>
#include <span>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

#include "PcieTransport.h"
#include "RxPacket.h"
#include "SelectedChannel.h"
#include "SignalStop.h"
#include "WiFiDriver.h"
#include "block_ack.h"
#include "frame_capture.h"
#include "logger.h"
#include "pipe_server.h"
#include "radio_link.h"

namespace {

using Mac = std::array<uint8_t, 6>;

constexpr size_t kMacHeaderBytes = 24;
constexpr size_t kReceiverOffset = 4;
constexpr size_t kBssidOffset = 16;
constexpr uint8_t kFrameTypeMask = 0x0C;
constexpr uint8_t kFrameTypeData = 0x08;
constexpr uint8_t kRetryFlag = 0x08;
constexpr uint8_t kGroupAddressBit = 0x01;

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

struct Session {
  SharedMac bssid_filter; /* unset forwards every frame */
  SharedMac own;
  bool block_ack_enabled = false;
  BlockAckInbox block_ack;
  /* Data frames addressed to the client and how many were retries. A high
   * share means the card is not acknowledging for the client's address. */
  std::atomic<uint64_t> for_us{0};
  std::atomic<uint64_t> for_us_retried{0};
};

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
  if (packet.RxAtrib.crc_err || frame.size() < kMacHeaderBytes) return;
  if (session.block_ack_enabled && session.own.is_set()) {
    const Mac own = session.own.get();
    if (const auto request = block_ack::parse_request(frame, own))
      session.block_ack.asked(*request);
    else if (const auto ended = block_ack::parse_delba(frame, own))
      session.block_ack.ended(*ended);
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
  if (message.size() <= RADIO_LINK_MAX_PAYLOAD)
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

/* Makes the card acknowledge frames sent to the client, once both of the
 * addresses that takes are known. */
void adopt_identity(IRadio &radio, const Session &session, Logger_t logger) {
  if (!session.own.is_set() || !session.bssid_filter.is_set()) return;
  if (!radio.SetStationIdentity({session.own.get()},
                                {session.bssid_filter.get()}))
    logger->warn("radio: the card will not acknowledge frames for the client; "
                 "peers will repeat everything they send it");
}

/* Answers a sender that asks to aggregate its frames, for one sender and
 * TID at a time. The card is set up before the answer goes out, so that it
 * is ready for the first aggregate. */
void answer_block_ack(IRadio &radio, Session &session, FrameCapture *capture,
                      std::optional<block_ack::Request> &open,
                      uint64_t &agreements) {
  const auto ended = session.block_ack.take_ended();
  if (ended && open && ended->first == open->peer &&
      ended->second == open->tid) {
    radio.EndBlockAck();
    open.reset();
  }
  const auto request = session.block_ack.take_request();
  if (!request || !session.bssid_filter.is_set()) return;
  if (open && (open->peer != request->peer || open->tid != request->tid))
    return;
  if (!radio.AcceptBlockAck({request->peer}, request->tid,
                            request->start_sequence,
                            block_ack::granted_frames(*request)))
    return;
  const auto answer = block_ack::response(*request, session.own.get(),
                                          session.bssid_filter.get());
  if (!radio.send_packet(answer.data(), answer.size())) return;
  if (capture) capture->transmitted(answer);
  open = request;
  agreements++;
}

/* Serves client messages until the client leaves or the daemon stops. Returns
 * false for a client that sent nothing, such as a probe for the pipe. */
bool serve_client(PipeServer &pipe, IRadio &radio, Session &session,
                  FrameCapture *capture, uint8_t channel, Logger_t logger) {
  radio_link_hello hello{RADIO_LINK_VERSION, channel, {}};
  pipe.send(RADIO_LINK_HELLO,
            {reinterpret_cast<const uint8_t *>(&hello), sizeof(hello)});

  std::vector<uint8_t> payload;
  uint64_t messages = 0, sent = 0, failed = 0, to_one = 0, agreements = 0;
  std::optional<block_ack::Request> open_block_ack;
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
      radio.send_packet(payload.data(), payload.size()) ? sent++ : failed++;
      to_one += is_for_one_station(payload);
      if (capture) capture->transmitted(payload);
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
      answer_block_ack(radio, session, capture, open_block_ack, agreements);
  }
  radio.EndBlockAck();
  if (messages) {
    const uint64_t for_us = session.for_us, retried = session.for_us_retried;
    logger->warn("radio: client gone; sent {} frames ({} to a single station), "
                 "{} failed; received {} data frames for it, {}% of them "
                 "repeats; answered {} block-ack requests",
                 sent, to_one, failed, for_us,
                 for_us ? retried * 100 / for_us : 0, agreements);
  }
  return messages > 0;
}

struct Options {
  uint8_t channel = 0;
  std::string capture_path;
  bool block_ack = true;
};

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
    else
      return std::nullopt;
  }
  return options;
}

} /* namespace */

int main(int argc, char **argv) {
  const auto options = parse_options(argc, argv);
  if (!options) {
    std::fprintf(stderr,
                 "usage: %s <channel> [--capture file.pcap] [--no-block-ack]\n",
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
  auto radio =
      driver.CreateRadioPcie(std::move(transport), devourer::DeviceConfig{});
  if (!radio) return 1;

  try {
    radio->InitWrite(SelectedChannel{.Channel = channel,
                                     .ChannelOffset = 0,
                                     .ChannelWidth = CHANNEL_WIDTH_20,
                                     .Band = 0});
  } catch (const std::exception &error) {
    logger->error("radio: bring-up failed: {}", error.what());
    return 1;
  }

  Session session;
  session.block_ack_enabled = options->block_ack;
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

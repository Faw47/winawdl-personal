// SPDX-License-Identifier: GPL-2.0-only
/* Listens on one channel and reports AWDL activity: which devices send AWDL
 * frames, what they call themselves and how strongly they arrive.
 * Usage: awdl_sniff <channel> [seconds] */

#include <array>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <map>
#include <optional>
#include <span>
#include <string>
#include <thread>

#include "PcieTransport.h"
#include "RxPacket.h"
#include "SelectedChannel.h"
#include "SignalStop.h"
#include "WiFiDriver.h"
#include "logger.h"

namespace {

using Mac = std::array<uint8_t, 6>;
using Frame = std::span<const uint8_t>;

constexpr size_t kMacHeaderBytes = 24;
constexpr size_t kSenderOffset = 10;
constexpr size_t kBssidOffset = 16;
constexpr size_t kFcsBytes = 4;
constexpr uint8_t kFrameControlAction = 0xD0;
constexpr uint8_t kFrameTypeMask = 0x0C;
constexpr uint8_t kFrameTypeData = 0x08;

/* Layout follows OWL's src/frame.h (struct awdl_action and the TLV ids). */
constexpr uint8_t kCategoryVendorSpecific = 127;
constexpr std::array<uint8_t, 3> kAppleOui = {0x00, 0x17, 0xF2};
constexpr uint8_t kAwdlType = 8;
constexpr Mac kAwdlBssid = {0x00, 0x25, 0x00, 0xFF, 0x94, 0x73};
constexpr size_t kActionHeaderBytes = 16;
constexpr size_t kActionSubtypeOffset = 6;
constexpr size_t kTlvHeaderBytes = 3;
constexpr uint8_t kSubtypePsf = 0;
constexpr uint8_t kSubtypeMif = 3;
constexpr uint8_t kTlvArpa = 16;
/* Arpa TLV value: flags, name length, then the name. */
constexpr size_t kArpaNameOffset = 2;

constexpr int kDefaultSeconds = 30;

struct AwdlAction {
  Mac sender;
  uint8_t subtype;
  std::string hostname; /* empty when the frame carries no Arpa TLV */
};

struct Peer {
  std::string hostname;
  uint64_t psf = 0, mif = 0, other_action = 0, data = 0;
  uint64_t rssi_sum = 0, rssi_samples = 0;
};

Mac mac_at(Frame frame, size_t offset) {
  Mac mac;
  std::copy_n(frame.begin() + offset, mac.size(), mac.begin());
  return mac;
}

std::string to_string(const Mac &mac) {
  char text[18];
  std::snprintf(text, sizeof(text), "%02x:%02x:%02x:%02x:%02x:%02x", mac[0],
                mac[1], mac[2], mac[3], mac[4], mac[5]);
  return text;
}

/* Hostnames come off the air, so anything unprintable is replaced. */
std::string printable(Frame bytes) {
  std::string text;
  for (uint8_t byte : bytes)
    text += (byte >= 0x20 && byte < 0x7F) ? static_cast<char>(byte) : '?';
  return text;
}

std::string arpa_hostname(Frame tlvs) {
  while (tlvs.size() >= kTlvHeaderBytes) {
    const uint8_t type = tlvs[0];
    const size_t length = tlvs[1] | (static_cast<size_t>(tlvs[2]) << 8);
    tlvs = tlvs.subspan(kTlvHeaderBytes);
    if (length > tlvs.size())
      break;
    const Frame value = tlvs.first(length);
    if (type == kTlvArpa && value.size() >= kArpaNameOffset) {
      const size_t name_length = value[1];
      if (kArpaNameOffset + name_length <= value.size())
        return printable(value.subspan(kArpaNameOffset, name_length));
    }
    tlvs = tlvs.subspan(length);
  }
  return {};
}

std::optional<AwdlAction> parse_awdl_action(Frame frame) {
  if (frame.size() < kMacHeaderBytes + kActionHeaderBytes ||
      frame[0] != kFrameControlAction)
    return std::nullopt;
  const Frame body = frame.subspan(kMacHeaderBytes);
  const bool is_awdl = body[0] == kCategoryVendorSpecific &&
                       std::equal(kAppleOui.begin(), kAppleOui.end(),
                                  body.begin() + 1) &&
                       body[4] == kAwdlType;
  if (!is_awdl)
    return std::nullopt;
  return AwdlAction{mac_at(frame, kSenderOffset), body[kActionSubtypeOffset],
                    arpa_hostname(body.subspan(kActionHeaderBytes))};
}

bool is_awdl_data(Frame frame) {
  return frame.size() >= kMacHeaderBytes &&
         (frame[0] & kFrameTypeMask) == kFrameTypeData &&
         mac_at(frame, kBssidOffset) == kAwdlBssid;
}

class AwdlCensus {
public:
  void on_packet(const Packet &packet) {
    _frames++;
    if (packet.RxAtrib.crc_err)
      return;
    Frame frame(packet.Data.data(), packet.Data.size());
    if (packet.RxAtrib.fcs_present && frame.size() > kFcsBytes)
      frame = frame.first(frame.size() - kFcsBytes);

    if (const auto action = parse_awdl_action(frame)) {
      Peer &peer = touch(action->sender, packet);
      if (action->subtype == kSubtypePsf)
        peer.psf++;
      else if (action->subtype == kSubtypeMif)
        peer.mif++;
      else
        peer.other_action++;
      if (peer.hostname.empty() && !action->hostname.empty()) {
        peer.hostname = action->hostname;
        std::printf("%s calls itself \"%s\"\n",
                    to_string(action->sender).c_str(), peer.hostname.c_str());
        std::fflush(stdout);
      }
    } else if (is_awdl_data(frame)) {
      touch(mac_at(frame, kSenderOffset), packet).data++;
    }
  }

  void print_summary(int channel) const {
    std::printf("\nchannel %d: %llu frames heard, %zu AWDL device(s)\n", channel,
                static_cast<unsigned long long>(_frames), _peers.size());
    for (const auto &[mac, peer] : _peers) {
      const uint64_t mean_rssi =
          peer.rssi_samples ? peer.rssi_sum / peer.rssi_samples : 0;
      std::printf("  %s  %-24s PSF %llu, MIF %llu, data %llu, mean rssi %llu\n",
                  to_string(mac).c_str(),
                  peer.hostname.empty() ? "(no name seen)"
                                        : peer.hostname.c_str(),
                  static_cast<unsigned long long>(peer.psf),
                  static_cast<unsigned long long>(peer.mif),
                  static_cast<unsigned long long>(peer.data),
                  static_cast<unsigned long long>(mean_rssi));
    }
  }

private:
  Peer &touch(const Mac &sender, const Packet &packet) {
    const auto [it, is_new] = _peers.try_emplace(sender);
    if (is_new) {
      std::printf("AWDL device %s appeared\n", to_string(sender).c_str());
      std::fflush(stdout);
    }
    it->second.rssi_sum += packet.RxAtrib.rssi[0];
    it->second.rssi_samples++;
    return it->second;
  }

  std::map<Mac, Peer> _peers;
  uint64_t _frames = 0;
};

/* Ends the run after `seconds`, or earlier when Ctrl+C already did. */
void stop_after(int seconds) {
  const auto deadline =
      std::chrono::steady_clock::now() + std::chrono::seconds(seconds);
  while (!g_devourer_should_stop && std::chrono::steady_clock::now() < deadline)
    std::this_thread::sleep_for(std::chrono::milliseconds(100));
  g_devourer_should_stop = true;
}

} /* namespace */

int main(int argc, char **argv) {
  if (argc < 2) {
    std::fprintf(stderr, "usage: %s <channel> [seconds]\n", argv[0]);
    return 2;
  }
  const int channel = std::atoi(argv[1]);
  const int seconds = argc > 2 ? std::atoi(argv[2]) : kDefaultSeconds;

  auto logger = std::make_shared<Logger>();
  install_devourer_signal_handlers();

  auto transport = devourer::PcieTransport::Open("win", logger);
  if (!transport)
    return 1;
  WiFiDriver driver(logger);
  auto radio =
      driver.CreateRadioPcie(std::move(transport), devourer::DeviceConfig{});
  if (!radio)
    return 1;

  AwdlCensus census;
  std::thread timer(stop_after, seconds);
  int exit_code = 0;
  try {
    /* Blocks in the receive loop until the stop flag is raised. */
    radio->Init([&census](const Packet &packet) { census.on_packet(packet); },
                SelectedChannel{.Channel = static_cast<uint8_t>(channel),
                                .ChannelOffset = 0,
                                .ChannelWidth = CHANNEL_WIDTH_20,
                                .Band = 0});
  } catch (const std::exception &error) {
    logger->error("radio bring-up failed: {}", error.what());
    exit_code = 1;
  }
  g_devourer_should_stop = true;
  timer.join();
  radio->Stop();
  census.print_summary(channel);
  return exit_code;
}

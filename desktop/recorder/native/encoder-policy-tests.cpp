// SPDX-License-Identifier: GPL-2.0-or-later
#include "encoder-policy.hpp"

#include <algorithm>
#include <iostream>
#include <stdexcept>
#include <string>

namespace {
void expect(bool condition, const char *message)
{
  if (!condition) throw std::runtime_error(message);
}

const revu::SettingValue &setting(const revu::EncoderConfiguration &config, std::string_view name)
{
  for (const auto &setting : config.settings) if (setting.name == name) return setting.value;
  throw std::runtime_error("Expected encoder setting was missing: " + std::string(name));
}

bool hasSetting(const revu::EncoderConfiguration &config, std::string_view name)
{
  for (const auto &setting : config.settings) if (setting.name == name) return true;
  return false;
}
} // namespace

int main()
{
  try {
    using revu::HardwareVendor;
    std::vector<revu::RegisteredEncoder> detected{{"obs_qsv11_v2", "h264", true, true, false, false},
      {"h264_texture_amf", "h264", true, true, false, false}, {"obs_nvenc_h264_tex", "h264", true, true, false, false},
      {"obs_x264", "h264", true, false, false, false}, {"ffmpeg_aac", "aac", false, false, false, false}};
    for (const auto &expected : revu::hardwareEncoders) {
      const auto choices = revu::eligibleEncoders(detected, expected.vendor);
      expect(choices.size() == 1 && choices.front().id == expected.id, "Mixed-adapter detection selected a different vendor");
      auto reversed = detected;
      std::reverse(reversed.begin(), reversed.end());
      expect(revu::eligibleEncoders(reversed, expected.vendor).front().id == expected.id, "Registration order changed selection");
      auto config = revu::encoderConfiguration(choices.front(), 8000);
      expect(config.has_value(), "Approved hardware configuration missing");
      expect(std::get<int64_t>(setting(*config, "bitrate")) == 8000, "Preset bitrate was not preserved");
      expect(std::get<int64_t>(setting(*config, "keyint_sec")) == 2, "Keyframes must be two seconds apart");
      expect(std::get<std::string_view>(setting(*config, "profile")) == "high", "H.264 profile must remain high");
      const auto bframeKey = expected.vendor == HardwareVendor::Intel ? "bframes" : "bf";
      expect(std::get<int64_t>(setting(*config, bframeKey)) == 0, "B-frame reordering would invalidate the simple movie clock");
      const auto rateControl = expected.vendor == HardwareVendor::Nvidia ? "cbr" : "CBR";
      expect(std::get<std::string_view>(setting(*config, "rate_control")) == rateControl, "Vendor-specific CBR spelling changed");
      if (expected.vendor == HardwareVendor::Nvidia) {
        expect(std::get<std::string_view>(setting(*config, "preset")) == "p4", "Existing NVIDIA preset regressed");
        expect(std::get<std::string_view>(setting(*config, "multipass")) == "disabled", "NVIDIA multipass must remain disabled");
        expect(!std::get<bool>(setting(*config, "lookahead")) && !std::get<bool>(setting(*config, "adaptive_quantization")), "NVIDIA analysis settings regressed");
      } else if (expected.vendor == HardwareVendor::Amd) {
        expect(std::get<std::string_view>(setting(*config, "preset")) == "speed", "AMD must use its speed preset");
        expect(!std::get<bool>(setting(*config, "pre_analysis")), "AMD pre-analysis must be disabled");
        expect(!hasSetting(*config, "target_usage") && !hasSetting(*config, "multipass"), "Another vendor's options leaked into AMD");
      } else {
        expect(std::get<std::string_view>(setting(*config, "target_usage")) == "TU6", "Intel must use its supported speed setting");
        expect(std::get<std::string_view>(setting(*config, "latency")) == "ultra-low", "Intel lookahead must not inherit normal-latency defaults");
        expect(!hasSetting(*config, "preset") && !hasSetting(*config, "bf"), "Another vendor's options leaked into QSV");
      }
    }
    expect(revu::eligibleEncoders({}, HardwareVendor::Nvidia).empty(), "Missing module/hardware should be unavailable");
    expect(revu::eligibleEncoders({detected.front()}, HardwareVendor::Nvidia).empty(), "Cross-adapter Intel candidate must not be selected");
    expect(revu::eligibleEncoders(detected, HardwareVendor::Unknown).empty(), "Unknown render adapter must fail closed");
    for (const auto bad : {revu::RegisteredEncoder{"obs_nvenc_h264_tex", "hevc", true, true, false, false},
      {"obs_nvenc_h264_tex", "h264", false, true, false, false}, {"obs_nvenc_h264_tex", "h264", true, false, false, false},
      {"obs_nvenc_h264_tex", "h264", true, true, true, false}, {"obs_nvenc_h264_tex", "h264", true, true, false, true},
      {"unknown_h264", "h264", true, true, false, false}, {"obs_x264", "h264", true, true, false, false}}) {
      expect(revu::eligibleEncoders({bad}, HardwareVendor::Nvidia).empty(), "Unknown/software/wrong-codec/internal encoder accepted");
    }
    expect(!revu::encoderConfiguration({"obs_x264", HardwareVendor::Nvidia}, 5000), "Software configuration must be rejected");
    expect(!revu::encoderConfiguration({"obs_nvenc_h264_tex", HardwareVendor::Intel}, 5000), "Vendor-ID mismatch must be rejected");
    expect(!revu::encoderConfiguration(revu::hardwareEncoders.front(), 0), "Zero bitrate must be rejected");
    expect(revu::hardwareVendor(0x10de) == HardwareVendor::Nvidia && revu::hardwareVendor(0x1002) == HardwareVendor::Amd && revu::hardwareVendor(0x8086) == HardwareVendor::Intel, "PCI vendor mapping failed");
    expect(revu::hardwareVendor(0x1414) == HardwareVendor::Unknown, "Software render adapter must not be reported as hardware");
    expect(revu::optionalEncoderModule("obs-nvenc") && revu::optionalEncoderModule("obs-qsv11"), "Optional vendor module policy missing");
    expect(!revu::optionalEncoderModule("obs-ffmpeg") && !revu::optionalEncoderModule("win-wasapi"), "Core mux/audio modules must be required");
    for (const auto vendor : {HardwareVendor::Nvidia, HardwareVendor::Amd, HardwareVendor::Intel, HardwareVendor::Unknown}) {
      expect(revu::shouldLoadRecorderModule("obs-nvenc", vendor) == (vendor == HardwareVendor::Nvidia), "An unrelated NVIDIA module would add needless probe work");
      expect(revu::shouldLoadRecorderModule("obs-qsv11", vendor) == (vendor == HardwareVendor::Intel), "An unrelated Intel module would add a needless QSV probe timeout");
      expect(revu::shouldLoadRecorderModule("obs-ffmpeg", vendor) && revu::shouldLoadRecorderModule("win-wasapi", vendor), "Core audio and mux modules must be loaded for all vendors");
    }
    std::cout << "Hardware encoder policy passed for NVIDIA, AMD, Intel, mixed adapters and unavailable/unsafe candidates\n";
    return 0;
  } catch (const std::exception &error) {
    std::cerr << error.what() << '\n';
    return 1;
  }
}

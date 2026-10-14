// SPDX-License-Identifier: GPL-2.0-or-later
#pragma once

#include <array>
#include <cstdint>
#include <optional>
#include <string_view>
#include <variant>
#include <vector>

namespace revu {
enum class HardwareVendor { Unknown, Nvidia, Amd, Intel };

constexpr HardwareVendor hardwareVendor(uint32_t pciVendor)
{
  switch (pciVendor) {
    case 0x10de: return HardwareVendor::Nvidia;
    case 0x1002: return HardwareVendor::Amd;
    case 0x8086: return HardwareVendor::Intel;
    default: return HardwareVendor::Unknown;
  }
}

constexpr std::string_view vendorName(HardwareVendor vendor)
{
  switch (vendor) {
    case HardwareVendor::Nvidia: return "nvidia";
    case HardwareVendor::Amd: return "amd";
    case HardwareVendor::Intel: return "intel";
    default: return "unknown";
  }
}

struct EncoderChoice {
  std::string_view id;
  HardwareVendor vendor;
};

// Exact IDs and property names from the pinned OBS 32.2.2 encoder definitions.
inline constexpr std::array<EncoderChoice, 3> hardwareEncoders{{
  {"obs_nvenc_h264_tex", HardwareVendor::Nvidia},
  {"h264_texture_amf", HardwareVendor::Amd},
  {"obs_qsv11_v2", HardwareVendor::Intel},
}};

struct RegisteredEncoder {
  std::string_view id;
  std::string_view codec;
  bool video;
  bool textureInput;
  bool internal;
  bool deprecated;
};

inline std::vector<EncoderChoice> eligibleEncoders(const std::vector<RegisteredEncoder> &registered, HardwareVendor renderVendor)
{
  std::vector<EncoderChoice> choices;
  // Iterate this stable allowlist, never module registration order. A device
  // belonging to another vendor is not a candidate for this render adapter.
  for (const auto &choice : hardwareEncoders) {
    if (choice.vendor != renderVendor) continue;
    for (const auto &encoder : registered) {
      if (encoder.id == choice.id && encoder.codec == "h264" && encoder.video && encoder.textureInput && !encoder.internal && !encoder.deprecated) {
        choices.push_back(choice);
        break;
      }
    }
  }
  return choices;
}

using SettingValue = std::variant<std::string_view, int64_t, bool>;
struct EncoderSetting { std::string_view name; SettingValue value; };
struct EncoderConfiguration { EncoderChoice encoder; std::vector<EncoderSetting> settings; };

inline std::optional<EncoderConfiguration> encoderConfiguration(const EncoderChoice &choice, uint32_t bitrate)
{
  bool allowed = false;
  for (const auto &encoder : hardwareEncoders) {
    if (choice.id == encoder.id && choice.vendor == encoder.vendor) allowed = true;
  }
  if (!allowed || !bitrate) return std::nullopt;
  const auto text = [](std::string_view name, std::string_view value) -> EncoderSetting { return {name, value}; };
  const auto integer = [](std::string_view name, int64_t value) -> EncoderSetting { return {name, value}; };
  const auto boolean = [](std::string_view name, bool value) -> EncoderSetting { return {name, value}; };
  EncoderConfiguration config{choice, {integer("bitrate", bitrate), text("profile", "high"), integer("keyint_sec", 2)}};
  auto &settings = config.settings;
  switch (choice.vendor) {
    case HardwareVendor::Nvidia:
      settings.push_back(text("rate_control", "cbr"));
      settings.push_back(text("preset", "p4"));
      settings.push_back(text("multipass", "disabled"));
      settings.push_back(text("tune", "ll"));
      settings.push_back(boolean("lookahead", false));
      settings.push_back(boolean("adaptive_quantization", false));
      settings.push_back(integer("bf", 0));
      break;
    case HardwareVendor::Amd:
      settings.push_back(text("rate_control", "CBR"));
      settings.push_back(text("preset", "speed"));
      settings.push_back(boolean("pre_analysis", false));
      settings.push_back(integer("bf", 0));
      break;
    case HardwareVendor::Intel:
      settings.push_back(text("rate_control", "CBR"));
      settings.push_back(text("target_usage", "TU6"));
      // In the pinned QSV implementation, ultra-low selects async depth 1 and
      // leaves lookahead depth at zero. The default normal mode enables it.
      settings.push_back(text("latency", "ultra-low"));
      settings.push_back(integer("bframes", 0));
      break;
    default: return std::nullopt;
  }
  return config;
}

constexpr bool optionalEncoderModule(std::string_view module)
{
  return module == "obs-nvenc" || module == "obs-qsv11";
}

constexpr bool shouldLoadRecorderModule(std::string_view module, HardwareVendor renderVendor)
{
  if (module == "obs-nvenc") return renderVendor == HardwareVendor::Nvidia;
  if (module == "obs-qsv11") return renderVendor == HardwareVendor::Intel;
  return true;
}
} // namespace revu

// SPDX-License-Identifier: GPL-2.0-or-later
#pragma once

#include <algorithm>
#include <cstdint>
#include <string>
#include <vector>

namespace revu {
// Per-minute evidence of how smoothly a recording followed the game. Minutes are movie
// minutes taken from packet timestamps, so they line up with the saved video.
class FrameStats {
public:
  static constexpr size_t maxMinutes = 300;
  static constexpr size_t maxHeldRun = 2;

  // A frame identical to the one before it encodes to a small fraction of the per-frame bit
  // budget. One or two such frames between full-size frames are a capture hold that viewers
  // see as a skip. Longer runs are still screens (shop, death recap, loading) and are not
  // counted. Calibrated on 1080p60 NVENC recordings against decoded frame differences.
  FrameStats(uint32_t bitrateKbps, uint32_t fps)
    : smallBytes(fps ? uint64_t(bitrateKbps) * 1000 / 8 / fps / 5 : 0), fullBytes(smallBytes * 2) {}

  void countPacket(int64_t ptsNs, uint64_t bytes, bool keyframe)
  {
    const size_t minute = grow(ptsNs);
    ++frames[minute];
    // Not named "small": Windows headers define that as a macro.
    const bool tiny = !keyframe && bytes < smallBytes, full = keyframe || bytes >= fullBytes;
    if (tiny) {
      if (runOpen && runLength < maxHeldRun) runMinutes[runLength++] = minute;
      else if (!runOpen && lastFull) { runOpen = true; runMinutes[0] = minute; runLength = 1; }
      else { runOpen = false; runLength = 0; }
      lastFull = false;
      return;
    }
    if (full && runOpen) for (size_t index = 0; index < runLength; ++index) ++repeated[runMinutes[index]];
    runOpen = false; runLength = 0; lastFull = full;
  }

  // libobs keeps a running total of render ticks it missed. The total at output start is the
  // baseline; each later increase belongs to the movie minute in which it was observed.
  void startLagged(uint64_t total) { laggedTotal = total; }
  void countLagged(uint64_t total, int64_t ptsNs)
  {
    if (total <= laggedTotal) return;
    lagged[grow(ptsNs)] += static_cast<uint32_t>(std::min<uint64_t>(total - laggedTotal, UINT32_MAX));
    laggedTotal = total;
  }

  uint64_t repeatedTotal() const
  {
    uint64_t total = 0;
    for (const auto value : repeated) total += value;
    return total;
  }

  std::string json() const
  {
    const auto list = [](const std::vector<uint32_t> &values) {
      std::string text = "[";
      for (size_t index = 0; index < values.size(); ++index) {
        if (index) text += ',';
        text += std::to_string(values[index]);
      }
      return text + ']';
    };
    return "{\"frames\":" + list(frames) + ",\"repeated\":" + list(repeated) + ",\"lagged\":" + list(lagged) + '}';
  }

private:
  size_t grow(int64_t ptsNs)
  {
    const size_t minute = ptsNs <= 0 ? 0
      : static_cast<size_t>(std::min<int64_t>(ptsNs / 60'000'000'000LL, static_cast<int64_t>(maxMinutes - 1)));
    if (frames.size() <= minute) {
      frames.resize(minute + 1); repeated.resize(minute + 1); lagged.resize(minute + 1);
    }
    return minute;
  }

  uint64_t smallBytes, fullBytes;
  std::vector<uint32_t> frames, repeated, lagged;
  size_t runMinutes[maxHeldRun]{};
  size_t runLength = 0;
  bool runOpen = false, lastFull = false;
  uint64_t laggedTotal = 0;
};
} // namespace revu

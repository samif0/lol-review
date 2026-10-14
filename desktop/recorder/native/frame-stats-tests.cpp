// SPDX-License-Identifier: GPL-2.0-or-later
#include "frame-stats.hpp"

#include <iostream>
#include <stdexcept>
#include <string>

namespace {
void expect(bool condition, const char *message)
{
  if (!condition) throw std::runtime_error(message);
}

constexpr int64_t frameNs = 1'000'000'000LL / 60;
constexpr int64_t minuteNs = 60'000'000'000LL;

// 1080p60 at 12 Mbps: 25,000 bytes per frame, so under 5,000 is small and 10,000+ is full.
void feed(revu::FrameStats &stats, int64_t &pts, std::initializer_list<uint64_t> sizes, bool keyframe = false)
{
  for (const auto size : sizes) { stats.countPacket(pts, size, keyframe); pts += frameNs; }
}
} // namespace

int main()
{
  try {
    {
      revu::FrameStats stats(12000, 60);
      int64_t pts = 0;
      feed(stats, pts, {24000, 800, 23000});
      expect(stats.repeatedTotal() == 1, "A single held frame between moving frames was not counted");
      feed(stats, pts, {22000, 900, 1200, 21000});
      expect(stats.repeatedTotal() == 3, "A two-frame hold was not counted as two repeated frames");
      feed(stats, pts, {20000, 700, 600, 500, 400, 19000});
      expect(stats.repeatedTotal() == 3, "A still screen was counted as capture holds");
      feed(stats, pts, {7000, 800, 22000});
      expect(stats.repeatedTotal() == 3, "A small frame after a mid-size frame is not a proven hold");
      feed(stats, pts, {24000, 800, 7000});
      expect(stats.repeatedTotal() == 3, "A hold must end on a full-size frame");
      feed(stats, pts, {200000}, true);
      feed(stats, pts, {300});
      feed(stats, pts, {210000}, true);
      expect(stats.repeatedTotal() == 4, "Keyframes count as full-size neighbors");
    }
    {
      revu::FrameStats stats(12000, 60);
      stats.countPacket(minuteNs - 1, 24000, false);
      stats.countPacket(minuteNs, 800, false);
      stats.countPacket(minuteNs + frameNs, 24000, false);
      expect(stats.json() == "{\"frames\":[1,2],\"repeated\":[0,1],\"lagged\":[0,0]}", "Minute boundaries or JSON layout changed");
      stats.startLagged(30);
      stats.countLagged(30, minuteNs);
      stats.countLagged(31, 0);
      stats.countLagged(35, 3 * minuteNs + 5);
      stats.countLagged(34, 3 * minuteNs + 6);
      expect(stats.json() == "{\"frames\":[1,2,0,0],\"repeated\":[0,1,0,0],\"lagged\":[1,0,0,4]}", "Lagged ticks were not attributed by minute from the output-start baseline");
    }
    {
      revu::FrameStats stats(12000, 60);
      stats.countPacket(-frameNs, 24000, false);
      stats.countPacket(1000 * minuteNs, 24000, false);
      const auto text = stats.json();
      expect(text.find("\"frames\":[1,") == 1, "A negative timestamp must count in the first minute");
      size_t commas = 0;
      for (const auto ch : text.substr(0, text.find(']'))) commas += ch == ',';
      expect(commas + 1 == revu::FrameStats::maxMinutes, "The per-minute arrays must stay bounded");
    }
    {
      revu::FrameStats stats(5000, 0);
      int64_t pts = 0;
      feed(stats, pts, {24000, 0, 23000});
      expect(stats.repeatedTotal() == 0, "Without a frame budget nothing can be classified as repeated");
    }
    std::cout << "Frame stats passed for held frames, still screens, minute buckets and lagged ticks\n";
    return 0;
  } catch (const std::exception &error) {
    std::cerr << error.what() << '\n';
    return 1;
  }
}

// SPDX-License-Identifier: GPL-2.0-or-later
#include "capture-pacing.hpp"

#include <iostream>
#include <stdexcept>

namespace {
void expect(bool condition, const char *message)
{
  if (!condition) throw std::runtime_error(message);
}

constexpr int64_t windowsDefaultHns = 160'000; // Read back on Windows 11 build 26200.
} // namespace

int main()
{
  try {
    expect(revu::captureMinUpdateIntervalHns(60) == 83'333, "1080p60 must allow a capture every 8.33 ms");
    expect(revu::captureMinUpdateIntervalHns(30) == 166'666, "30 FPS presets must allow a capture every 16.67 ms");
    expect(revu::captureMinUpdateIntervalHns(0) == 0, "An unknown frame rate must keep the Windows default");
    for (const uint32_t fps : {30u, 60u}) {
      const int64_t interval = revu::captureMinUpdateIntervalHns(fps), frame = 10'000'000LL / fps;
      expect(interval > 0, "Every preset must override the Windows default");
      expect(2 * interval <= frame, "Two captures must fit in every output frame interval");
      expect(3 * interval > frame, "The compositor must not copy three or more captures per output frame");
    }
    expect(revu::captureMinUpdateIntervalHns(60) < windowsDefaultHns, "1080p60 must capture faster than the Windows default");
    std::cout << "Capture pacing passed for 30 and 60 FPS output\n";
    return 0;
  } catch (const std::exception &error) {
    std::cerr << error.what() << '\n';
    return 1;
  }
}

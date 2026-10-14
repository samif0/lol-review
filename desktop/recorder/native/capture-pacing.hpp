// SPDX-License-Identifier: GPL-2.0-or-later
#pragma once

#include <cstdint>

namespace revu {
// Windows paces every capture session by GraphicsCaptureSession.MinUpdateInterval. Its default
// (read back as 16 ms on Windows 11 build 26200) lets capture take only every
// ceil(16 ms / frame time)-th game frame, so a League window at 160-240 FPS arrives at 53-60 FPS
// and a 60 FPS recording repeats whatever never arrived. Half the output frame interval puts a fresh frame in front of every render tick
// (about two captures per tick) without the compositor copying every game frame.
// Hundred-nanosecond units, as TimeSpan uses; 0 means keep the Windows default.
constexpr int64_t captureMinUpdateIntervalHns(uint32_t fps)
{
  return fps ? 10'000'000LL / fps / 2 : 0;
}
} // namespace revu

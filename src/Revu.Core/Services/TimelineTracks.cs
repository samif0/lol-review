#nullable enable

using System.Text.Json;

namespace Revu.Core.Services;

/// <summary>
/// Position plumbing shared by the post-game timeline analyzers: the sample type,
/// piecewise-linear interpolation (strict bracketing for sweeps, nearest-clamped for a
/// single instant), the death → respawn blackouts, and the shared position parser.
/// Lifted out of <see cref="MapStateAnalyzer"/> so <see cref="TeamfightAnalyzer"/>
/// reads the same payload the same way.
/// </summary>
internal static class TimelineTracks
{
    internal readonly record struct Sample(long TMs, double X, double Y);

    // Max distance in time from the nearest position sample before interpolation
    // gives up and reports no distance (1.5 frame intervals). Death-stamp only —
    // the proximity sweep uses strict bracketing instead (see PositionAtStrict).
    internal const long MaxSampleGapMs = 90_000;

    // TryReadPosition helper split out so a frame's participantFrames entry (position
    // nested under "position") and an event (same shape) share one parser. Internal
    // (with the JSON helpers below) so TeamfightAnalyzer reads the same payload the same way.
    internal static bool TryReadPosition(JsonElement holder, out double x, out double y)
    {
        x = y = 0;
        var pos = holder;
        if (holder.ValueKind == JsonValueKind.Object && holder.TryGetProperty("position", out var nested))
            pos = nested;
        if (pos.ValueKind != JsonValueKind.Object) return false;
        if (!pos.TryGetProperty("x", out var xEl) || xEl.ValueKind != JsonValueKind.Number) return false;
        if (!pos.TryGetProperty("y", out var yEl) || yEl.ValueKind != JsonValueKind.Number) return false;
        x = xEl.GetDouble();
        y = yEl.GetDouble();
        return true;
    }

    // After a tracked participant dies, his next sample is the respawn fountain —
    // interpolating across that teleport would sweep a phantom track through the
    // middle of the map. Black out from each death until the next frame pins the
    // participant again (fallback: one frame interval).
    internal static Dictionary<int, List<(long Start, long End)>> BuildBlackouts(
        Dictionary<int, List<long>> trackedDeaths, List<long> frameTimes)
    {
        var result = new Dictionary<int, List<(long, long)>>();
        foreach (var (pid, deathTimes) in trackedDeaths)
        {
            var windows = new List<(long, long)>();
            foreach (var d in deathTimes)
            {
                var end = d + 60_000;
                foreach (var ft in frameTimes)
                {
                    if (ft > d) { end = ft; break; }
                }
                windows.Add((d, end));
            }
            result[pid] = windows;
        }
        return result;
    }

    internal static bool InBlackout(
        Dictionary<int, List<(long Start, long End)>> blackouts, int pid, long t) =>
        blackouts.TryGetValue(pid, out var windows) && windows.Any(w => t > w.Start && t < w.End);

    // Strict interpolation for the sweep: the instant must sit INSIDE the sample
    // span (bracketing samples; an exact knot answers as itself). No nearest-clamp
    // extrapolation — a lone sighting must not smear across ±90s of sweep. The
    // clamped PositionAt below stays for death stamping, where a nearest sample
    // is an acceptable answer for a single instant.
    internal static (double X, double Y)? PositionAtStrict(List<Sample> sorted, long tMs)
    {
        Sample? before = null, after = null;
        foreach (var s in sorted)
        {
            if (s.TMs <= tMs) before = s;
            if (s.TMs >= tMs) { after = s; break; }
        }
        if (before is not { } b || after is not { } a) return null;
        if (a.TMs == b.TMs) return (b.X, b.Y);
        var f = (double)(tMs - b.TMs) / (a.TMs - b.TMs);
        return (b.X + (a.X - b.X) * f, b.Y + (a.Y - b.Y) * f);
    }

    // Piecewise-linear position at tMs from the sorted samples; nearest-sample
    // fallback at the edges within MaxSampleGapMs, null beyond that. Linear
    // interpolation across the jungler's own death/respawn is a known, bounded
    // approximation — the distances are god-view review anchors, not measurements.
    internal static (double X, double Y)? PositionAt(List<Sample> sorted, long tMs)
    {
        if (sorted.Count == 0) return null;

        Sample? before = null, after = null;
        foreach (var s in sorted)
        {
            if (s.TMs <= tMs) before = s;
            else { after = s; break; }
        }
        if (before is { } b && after is { } a)
        {
            if (a.TMs == b.TMs) return (b.X, b.Y);
            var f = (double)(tMs - b.TMs) / (a.TMs - b.TMs);
            return (b.X + (a.X - b.X) * f, b.Y + (a.Y - b.Y) * f);
        }
        if (before is { } last && tMs - last.TMs <= MaxSampleGapMs) return (last.X, last.Y);
        if (after is { } next && next.TMs - tMs <= MaxSampleGapMs) return (next.X, next.Y);
        return null;
    }

    internal static void AddSample(Dictionary<int, List<Sample>> samples, int pid, Sample s)
    {
        if (!samples.TryGetValue(pid, out var list)) { list = []; samples[pid] = list; }
        list.Add(s);
    }

    internal static double Distance((double X, double Y) a, (double X, double Y) b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

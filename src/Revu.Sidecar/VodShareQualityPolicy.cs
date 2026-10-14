#nullable enable

using Revu.Core.Constants;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>
/// Picks the share copy's quality before encoding (contract 7.4). Pure: the size estimate
/// (duration x 35 or 21 MB/min), the 2 GiB file cap and the proxy's storage pool decide.
/// </summary>
internal static class VodShareQualityPolicy
{
    /// <summary>The outcome: a quality (with why it was lowered), or an error code.</summary>
    internal sealed record Decision(VodShareQuality Quality, string? Reason, string? ErrorCode, long? RetryAt)
    {
        public bool Ok => ErrorCode is null;
    }

    /// <summary>Estimated share copy size in bytes: <c>ceil(d / 60 * bytesPerMinute)</c>.</summary>
    public static long Estimate(VodShareQuality quality, double durationSeconds)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0) return 0;
        durationSeconds = Math.Min(durationSeconds, 1e9);   // a corrupt probe must not overflow decimal
        var perMinute = quality == VodShareQuality.P1080
            ? GameConstants.VodShareBytesPerMinute1080
            : GameConstants.VodShareBytesPerMinute720;
        // Decimal keeps the estimate exact (no 1-byte drift from binary fractions of a second).
        return (long)Math.Ceiling((decimal)durationSeconds * perMinute / 60m);
    }

    /// <summary>
    /// <c>too_long</c> past 2 hours; 1080p falls back to 720p when its estimate passes 95% of
    /// 2 GiB (<c>size</c>) or the free pool (<c>pool</c>); 720p that does not fit the pool is
    /// <c>pool_full</c> with the time it would fit. A null pool skips the pool checks.
    /// </summary>
    public static Decision Decide(VodShareQuality requested, double durationSeconds, VodSharePool? pool, long now)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0 || VodShareEncoderArgs.IsTooLong(durationSeconds))
            return new Decision(requested, null, "too_long", null);

        var quality = requested;
        string? reason = null;
        if (quality == VodShareQuality.P1080
            && Estimate(VodShareQuality.P1080, durationSeconds) > 0.95 * GameConstants.MaxVodShareBytes)
        {
            quality = VodShareQuality.P720;
            reason = "size";
        }
        if (pool is not null && quality == VodShareQuality.P1080
            && Estimate(VodShareQuality.P1080, durationSeconds) > pool.FreeBytes)
        {
            quality = VodShareQuality.P720;
            reason = "pool";
        }
        if (pool is not null && quality == VodShareQuality.P720)
        {
            var est720 = Estimate(VodShareQuality.P720, durationSeconds);
            if (est720 > pool.FreeBytes)
                return new Decision(quality, reason, "pool_full", pool.FitsAt(est720, now));
        }
        return new Decision(quality, reason, null, null);
    }

    /// <summary>Free disk needed to prepare a copy: <c>est * 1.2 + 256 MiB</c>.</summary>
    public static long DiskNeeded(VodShareQuality quality, double durationSeconds) =>
        (long)Math.Ceiling(Estimate(quality, durationSeconds) * 1.2m) + 268435456L;
}

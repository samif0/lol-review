#nullable enable

using Revu.Core.Constants;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>
/// Picks the share copy's quality before encoding (contract 7.4). Pure: the size estimate (the
/// fitted bitrate for the duration and frame rate), the 2 GiB file cap and the proxy's storage
/// pool decide.
/// </summary>
internal static class VodShareQualityPolicy
{
    /// <summary>
    /// Pool checks plan for the estimate plus 3%: exactly what the upload that starts with the
    /// encode reserves (<see cref="VodShareEncoderArgs.ReserveBytes"/>), so a share the sheet
    /// offers is not refused for space at init.
    /// </summary>
    public const decimal PoolMargin = 1.03m;

    /// <summary>The outcome: a quality (with why it was lowered), or an error code.</summary>
    internal sealed record Decision(VodShareQuality Quality, string? Reason, string? ErrorCode, long? RetryAt)
    {
        public bool Ok => ErrorCode is null;
    }

    /// <summary>Estimated share copy size in bytes (<see cref="VodShareEncoderArgs.EstimateBytes"/>).</summary>
    public static long Estimate(VodShareQuality quality, int fps, double durationSeconds) =>
        VodShareEncoderArgs.EstimateBytes(quality, fps, durationSeconds);

    /// <summary>The bytes a pool check plans for: <c>ceil(Estimate * 1.03)</c>.</summary>
    public static long PoolEstimate(VodShareQuality quality, int fps, double durationSeconds) =>
        (long)Math.Ceiling(Estimate(quality, fps, durationSeconds) * PoolMargin);

    /// <summary>
    /// <c>too_long</c> past 2 hours; 1080p falls back to 720p when a long game would push its
    /// bitrate under <see cref="GameConstants.VodShareMin1080VideoKbps"/> to fit (<c>size</c>) or
    /// its pool estimate (3% margin) passes the free pool (<c>pool</c>); 720p whose pool estimate
    /// does not fit the pool is <c>pool_full</c> with the time it would fit. A null pool skips
    /// the pool checks.
    /// </summary>
    public static Decision Decide(VodShareQuality requested, int fps, double durationSeconds, VodSharePool? pool, long now)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0 || VodShareEncoderArgs.IsTooLong(durationSeconds))
            return new Decision(requested, null, "too_long", null);

        var quality = requested;
        string? reason = null;
        if (quality == VodShareQuality.P1080 && VodShareEncoderArgs.TooLongFor1080(fps, durationSeconds))
        {
            quality = VodShareQuality.P720;
            reason = "size";
        }
        if (pool is not null && quality == VodShareQuality.P1080
            && PoolEstimate(VodShareQuality.P1080, fps, durationSeconds) > pool.FreeBytes)
        {
            quality = VodShareQuality.P720;
            reason = "pool";
        }
        if (pool is not null && quality == VodShareQuality.P720)
        {
            var est720 = PoolEstimate(VodShareQuality.P720, fps, durationSeconds);
            if (est720 > pool.FreeBytes)
                return new Decision(quality, reason, "pool_full", pool.FitsAt(est720, now));
        }
        return new Decision(quality, reason, null, null);
    }

    /// <summary>Free disk needed to prepare a copy: <c>est * 1.2 + 256 MiB</c>.</summary>
    public static long DiskNeeded(VodShareQuality quality, int fps, double durationSeconds) =>
        (long)Math.Ceiling(Estimate(quality, fps, durationSeconds) * 1.2m) + 268435456L;
}

#nullable enable

using Microsoft.Extensions.Logging;
using Revu.Core.Data.Repositories;

namespace Revu.Sidecar;

/// <summary>
/// Builds the read-only tilt-check snapshot served at GET /api/tiltcheck.
///
/// <para>
/// Surfaces the Tilt Check page's recent-history + stats slice, minus all
/// WinUI/dispatcher concerns, and emits the camelCase JSON contract (see
/// <see cref="TiltCheckDto"/> and desktop/ui/sample-tiltcheck.json). It reads
/// ONLY Core's <see cref="ITiltCheckRepository"/> via the read-only factory.
/// </para>
///
/// <para>
/// The tilt-reset RITUAL is a WRITE the frontend performs separately
/// (invoke('run_reset', {...}) → POST /api/reset). This builder never mutates.
/// </para>
///
/// <para>
/// Like <see cref="DashboardSnapshotBuilder"/>, each section is wrapped in
/// try/catch that degrades to empty so one failing read never blanks the page,
/// and color constants are hardcoded from the glass-aurora mockup palette
/// (TODO: lift into Revu.Core to share one source of truth).
/// </para>
/// </summary>
public sealed class TiltCheckSnapshotBuilder
{
    // ── Tunables (mirror the Tilt Check page) ───────────────────────────────
    private const int RecentTake = 20;
    private const int LatestPlanMaxAgeDays = 14;

    // ── Mockup palette (TODO: extract to Revu.Core) ─────────────────────────
    // Reset emotions are negative-mental events → the loss-red HUD accent.
    private const string LossHex = "#f3a3a8";

    private readonly ITiltCheckRepository _tiltCheckRepo;
    private readonly ILogger<TiltCheckSnapshotBuilder> _logger;

    public TiltCheckSnapshotBuilder(
        ITiltCheckRepository tiltCheckRepo,
        ILogger<TiltCheckSnapshotBuilder> logger)
    {
        _tiltCheckRepo = tiltCheckRepo;
        _logger = logger;
    }

    public async Task<TiltCheckDto> BuildAsync(CancellationToken ct = default)
    {
        var now = DateTime.Now;

        var recent = await BuildRecentAsync();
        var stats = await BuildStatsAsync();
        var latestPlan = await BuildLatestPlanAsync();

        return new TiltCheckDto(
            GeneratedAt: now.ToString("yyyy-MM-ddTHH:mm:ss"),
            Recent: recent,
            HasRecent: recent.Count > 0,
            Stats: stats,
            LatestPlan: latestPlan,
            HasLatestPlan: !string.IsNullOrWhiteSpace(latestPlan));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Section builders
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<TiltCheckEntryDto>> BuildRecentAsync()
    {
        try
        {
            var rows = await _tiltCheckRepo.GetRecentAsync(RecentTake);
            return rows.Select(MapEntry).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TiltCheck: recent history load failed");
            return Array.Empty<TiltCheckEntryDto>();
        }
    }

    private async Task<TiltCheckStatsDto> BuildStatsAsync()
    {
        try
        {
            var s = await _tiltCheckRepo.GetStatsAsync();
            var topEmotions = s.TopEmotions
                .Select(e => new EmotionCountDto(e.Emotion, e.Count, LossHex))
                .ToList();

            // "−1.8 avg" headline for the reduction stat; "" when no rated rituals.
            var avgReductionText = s.Total > 0
                ? $"{FormatSigned(s.AvgReduction)} avg"
                : "";

            return new TiltCheckStatsDto(
                Total: s.Total,
                AvgBefore: s.AvgBefore,
                AvgAfter: s.AvgAfter,
                AvgReduction: s.AvgReduction,
                AvgReductionText: avgReductionText,
                TopEmotions: topEmotions);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TiltCheck: stats computation failed");
            return new TiltCheckStatsDto(
                Total: 0,
                AvgBefore: 0,
                AvgAfter: 0,
                AvgReduction: 0,
                AvgReductionText: "",
                TopEmotions: Array.Empty<EmotionCountDto>());
        }
    }

    private async Task<string?> BuildLatestPlanAsync()
    {
        try
        {
            return await _tiltCheckRepo.GetLatestPlanAsync(LatestPlanMaxAgeDays);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TiltCheck: latest plan load failed");
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Row mapping
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Map one raw <c>SELECT *</c> tilt_checks row (snake_case keys, raw SQLite
    /// values: <see cref="long"/> / <see cref="string"/> / null) to the DTO.
    /// </summary>
    private static TiltCheckEntryDto MapEntry(Dictionary<string, object?> row)
    {
        var intensityBefore = GetInt(row, "intensity_before");
        var intensityAfter = GetNullableInt(row, "intensity_after");
        int? reduction = intensityAfter.HasValue
            ? intensityBefore - intensityAfter.Value
            : null;

        var ifThenPlan = GetString(row, "if_then_plan");
        var createdAt = GetLong(row, "created_at");
        var createdAtText = createdAt > 0
            ? DateTimeOffset.FromUnixTimeSeconds(createdAt).LocalDateTime.ToString("MMM dd, HH:mm")
            : "";

        return new TiltCheckEntryDto(
            Id: GetLong(row, "id"),
            Emotion: GetString(row, "emotion"),
            IntensityBefore: intensityBefore,
            IntensityAfter: intensityAfter,
            IntensityReduction: reduction,
            ReframeThought: GetString(row, "reframe_thought"),
            ReframeResponse: GetString(row, "reframe_response"),
            ThoughtType: GetString(row, "thought_type"),
            CueWord: GetString(row, "cue_word"),
            FocusIntention: GetString(row, "focus_intention"),
            GameId: GetNullableLong(row, "game_id"),
            IfThenPlan: ifThenPlan,
            HasPlan: !string.IsNullOrWhiteSpace(ifThenPlan),
            CreatedAt: createdAt,
            CreatedAtText: createdAtText,
            EmotionColorHex: LossHex);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Value coercion (SQLite returns INTEGER as long, TEXT as string)
    // ─────────────────────────────────────────────────────────────────────────

    private static string FormatSigned(double v)
    {
        // Use a real minus sign for negatives (reductions read as "−1.8").
        if (v < 0) return $"−{Math.Abs(v):0.#}";
        if (v > 0) return $"+{v:0.#}";
        return "0";
    }

    private static string GetString(Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var v) && v is not null ? Convert.ToString(v) ?? "" : "";

    private static long GetLong(Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var v) && v is not null ? Convert.ToInt64(v) : 0L;

    private static long? GetNullableLong(Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var v) && v is not null ? Convert.ToInt64(v) : null;

    private static int GetInt(Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var v) && v is not null ? Convert.ToInt32(v) : 0;

    private static int? GetNullableInt(Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var v) && v is not null ? Convert.ToInt32(v) : null;
}

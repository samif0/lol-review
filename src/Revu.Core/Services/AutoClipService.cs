#nullable enable

using Microsoft.Extensions.Logging;
using Revu.Core.Data.Repositories;

namespace Revu.Core.Services;

/// <summary>
/// On-demand objective auto-clipper. Plans the clips with the pure
/// <see cref="AutoClipPlanner"/> (which reuses the timeline's
/// <see cref="ObjectiveEventTieResolver"/> so the clipped set matches the loud
/// markers), then extracts each via <see cref="IClipService"/> and persists it with
/// the shared <see cref="ClipPersistence"/> tail — the same bookmark + evidence +
/// objective-practiced records a manual clip writes.
/// </summary>
public sealed class AutoClipService : IAutoClipService
{
    private readonly IConfigService _config;
    private readonly IGameRepository _games;
    private readonly IVodRepository _vod;
    private readonly IGameEventsRepository _events;
    private readonly IObjectivesRepository _objectives;
    private readonly IEvidenceRepository _evidence;
    private readonly IClipService _clips;
    private readonly ILogger<AutoClipService> _logger;

    public AutoClipService(
        IConfigService config,
        IGameRepository games,
        IVodRepository vod,
        IGameEventsRepository events,
        IObjectivesRepository objectives,
        IEvidenceRepository evidence,
        IClipService clips,
        ILogger<AutoClipService> logger)
    {
        _config = config;
        _games = games;
        _vod = vod;
        _events = events;
        _objectives = objectives;
        _evidence = evidence;
        _clips = clips;
        _logger = logger;
    }

    public async Task<AutoClipResult> ClipObjectiveEventsAsync(long gameId, long? objectiveId, CancellationToken ct = default)
    {
        // Toggle gate (defense in depth — the button is hidden client-side too).
        if (!_config.AutoClipObjectivesEnabled)
            return new AutoClipResult(0, 0, "disabled");

        // The recording must exist on disk to clip from.
        string vodPath = "";
        try
        {
            var paths = await _vod.GetVodPathsAsync(new[] { gameId });
            if (paths.TryGetValue(gameId, out var p) && !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                vodPath = p;
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Auto-clip: VOD path lookup failed for {GameId}", gameId); }

        if (vodPath.Length == 0)
            return new AutoClipResult(0, 0, "no_vod");

        var game = await _games.GetAsync(gameId);
        var champion = game?.ChampionName ?? "";
        var durationS = game?.GameDuration > 0 ? game!.GameDuration : 0;

        var events = await _events.GetEventsAsync(gameId);
        var resolver = ObjectiveEventTieResolver.FromTies(await _objectives.GetActiveObjectiveEventTokensAsync());

        // Existing auto-clip keys (incl. dismissed) so re-runs and user-deleted clips
        // are not regenerated.
        var existingKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in await _evidence.GetForGameAsync(gameId, includeDismissed: true))
            if (!string.IsNullOrWhiteSpace(row.SourceKey)) existingKeys.Add(row.SourceKey);

        var planned = AutoClipPlanner.SelectClips(
            gameId, events, resolver, objectiveId, durationS, existingKeys, out var skippedByCap);

        if (planned.Count == 0)
            return new AutoClipResult(0, skippedByCap, skippedByCap > 0 ? "" : "no_events");

        int created = 0, skipped = skippedByCap;
        foreach (var clip in planned)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Extract FIRST (the heavy ffmpeg step), then the quick DB writes — never
                // hold a write across ffmpeg. ExtractClipAsync runs at BelowNormal priority.
                var clipPath = await _clips.ExtractClipAsync(vodPath, clip.StartS, clip.EndS, champion, _config.ClipsFolder);
                if (string.IsNullOrEmpty(clipPath))
                {
                    _logger.LogWarning("Auto-clip: ffmpeg produced no output for game {GameId} event {EventId} ({Start}-{End}s)",
                        gameId, clip.EventId, clip.StartS, clip.EndS);
                    skipped++;
                    continue;
                }

                var note = BuildNote(clip);
                await ClipPersistence.PersistAsync(
                    _vod, _objectives, _evidence,
                    gameId: gameId,
                    startS: clip.StartS,
                    endS: clip.EndS,
                    clipPath: clipPath,
                    note: note,
                    quality: "",
                    objectiveId: clip.ObjectiveId,
                    sourceKey: AutoClipPlanner.SourceKey(gameId, clip.EventId));
                created++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto-clip: failed for game {GameId} event {EventId}", gameId, clip.EventId);
                skipped++;
            }
        }

        _logger.LogInformation("Auto-clip: game {GameId} created {Created}, skipped {Skipped}", gameId, created, skipped);
        return new AutoClipResult(created, skipped, "");
    }

    // A readable bookmark caption: the objective title + the event clock, e.g.
    // "Review every death · 12:41". Falls back to a generic label when untitled.
    private static string BuildNote(PlannedClip clip)
    {
        var title = string.IsNullOrWhiteSpace(clip.ObjectiveTitle) ? "Objective" : clip.ObjectiveTitle.Trim();
        var clock = $"{clip.EventTimeS / 60}:{clip.EventTimeS % 60:D2}";
        return $"{title} · {clock}";
    }
}

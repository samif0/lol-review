#nullable enable

using Microsoft.Extensions.Logging;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>
/// Builds the read-only review snapshot served at GET /api/review.
///
/// <para>
/// Mirrors the post-game review surface for ONE game (the WinUI
/// <c>ReviewViewModel</c>), minus all WinUI/dispatcher/workflow-service
/// concerns, and emits the camelCase JSON contract (see <see cref="ReviewDto"/>
/// and desktop/ui/sample-review.json). It deliberately does NOT reference the
/// WinUI ViewModel — only the Core repo + query interfaces.
/// </para>
///
/// <para>
/// SUBJECT SELECTION: the review page is normally opened with a specific gameId
/// (deferred nav). For v1 the endpoint returns a SAMPLE subject — the most
/// recent unreviewed game in the last 3 days, falling back to the most recent
/// reviewed game — so the Tauri frontend can preview the page without nav.
/// </para>
///
/// <para>
/// READ-ONLY ABSOLUTE: only repo READ methods are called. The review FORM
/// (mental rating / debrief / tags / save / skip) is a MUTATION surface and is
/// DEFERRED: its current saved values ship for read-only display
/// (<see cref="ReviewFormDto"/> with Editable=false), but nothing is written.
/// The objective practiced-toggle is likewise deferred.
/// </para>
///
/// <para>
/// COLOR PARITY: like DashboardSnapshotBuilder, hex constants are hardcoded from
/// the glass-aurora mockup palette (accent #9d8bff, gold #f3c794, win #8ee7ba,
/// loss #f3a3a8) plus the objective level ramp. TODO: lift these into Revu.Core
/// so the app + both snapshot builders share one source of truth.
/// </para>
///
/// <para>
/// Each section is wrapped in try/catch that degrades to empty so one failing
/// section never blanks the whole response (mirrors the dashboard builder).
/// </para>
/// </summary>
public sealed class ReviewSnapshotBuilder
{
    // ── Tunables (mirror ReviewViewModel / the unreviewed window) ───────────
    private const int UnreviewedWindowDays = 3;
    private const int RecentScanLimit = 30;
    // Mirror ReviewViewModel.MaxEvidenceInboxItems — cap on the ATTACHED list.
    // The unassigned "EVIDENCE TO SORT" inbox is intentionally uncapped (P-013).
    private const int MaxEvidenceInbox = 12;

    // ── Mockup palette (TODO: extract to Revu.Core) — same as the dashboard ─
    private const string AccentHex = "#9d8bff";
    private const string GoldHex = "#f3c794";
    private const string WinHex = "#8ee7ba";
    private const string LossHex = "#f3a3a8";
    private const string PrimaryTextHex = "#f4f3ff";
    private const string RingTrackHex = "rgba(255,255,255,0.13)";

    private static readonly string[] EditingNoteText =
    {
        "Review editing is coming soon; these fields are read-only in this preview.",
    };

    private readonly IGameHistoryQuery _gameHistory;
    private readonly IGameRepository _gameRepo;
    private readonly IObjectivesRepository _objectivesRepo;
    private readonly IPromptsRepository _promptsRepo;
    private readonly ISessionLogRepository _sessionLogRepo;
    private readonly IEvidenceRepository _evidenceRepo;
    private readonly IGameEventsRepository _eventsRepo;
    private readonly IDeathClassificationsRepository _deathClassRepo;
    private readonly IMatchupNotesRepository _matchupNotesRepo;
    private readonly IConceptTagRepository _conceptTagRepo;
    private readonly IVodRepository _vodRepo;
    private readonly IConfigService _configService;
    private readonly ILogger<ReviewSnapshotBuilder> _logger;

    public ReviewSnapshotBuilder(
        IGameHistoryQuery gameHistory,
        IGameRepository gameRepo,
        IObjectivesRepository objectivesRepo,
        IPromptsRepository promptsRepo,
        ISessionLogRepository sessionLogRepo,
        IEvidenceRepository evidenceRepo,
        IGameEventsRepository eventsRepo,
        IDeathClassificationsRepository deathClassRepo,
        IMatchupNotesRepository matchupNotesRepo,
        IConceptTagRepository conceptTagRepo,
        IVodRepository vodRepo,
        IConfigService configService,
        ILogger<ReviewSnapshotBuilder> logger)
    {
        _gameHistory = gameHistory;
        _gameRepo = gameRepo;
        _objectivesRepo = objectivesRepo;
        _promptsRepo = promptsRepo;
        _sessionLogRepo = sessionLogRepo;
        _evidenceRepo = evidenceRepo;
        _eventsRepo = eventsRepo;
        _deathClassRepo = deathClassRepo;
        _matchupNotesRepo = matchupNotesRepo;
        _conceptTagRepo = conceptTagRepo;
        _vodRepo = vodRepo;
        _configService = configService;
        _logger = logger;
    }

    /// <param name="gameId">When &gt; 0, load THIS specific game (clicking a game
    /// row opens its review). When null/0, fall back to the sample subject.</param>
    public async Task<ReviewDto> BuildAsync(long? gameId = null, CancellationToken ct = default)
    {
        var now = DateTime.Now;
        var generatedAt = now.ToString("yyyy-MM-ddTHH:mm:ss");

        var (game, sourceText) = await PickSubjectAsync(gameId);
        if (game is null)
        {
            return new ReviewDto(GeneratedAt: generatedAt, Subject: null, SubjectSourceText: "");
        }

        var header = BuildHeader(game);
        var stats = BuildStatStrip(game);
        // P-027: load THIS game's evidence + bookmark share-URLs once so the
        // prompt-grouped clips (Prompts[].Clips / UnpromptedClips / UnsortedClips)
        // and the legacy Evidence (attached/unassigned) split share one row set.
        var promptClips = await BuildPromptClipContextAsync(game.GameId);
        var objectives = await BuildObjectivesAsync(game.GameId, promptClips);
        var form = await BuildFormAsync(game);
        var deaths = await BuildDeathsAsync(game.GameId);
        var evidence = await BuildEvidenceAsync(game.GameId);
        var matchupHistory = await BuildMatchupHistoryAsync(game);
        var tagCatalog = await BuildTagCatalogAsync(game.GameId);

        // P-027 reachability guard: a clip tagged to a prompt/objective is only
        // RENDERED if that objective is active AND (for prompt clips) the prompt
        // still exists. A clip tagged to an archived objective, or to a prompt
        // that was later deleted, would otherwise vanish — the old "to sort"
        // evidence lists that used to catch it are gone. Sweep every prompt/
        // objective bucket that no rendered objective consumed into UnsortedClips
        // so nothing becomes unreachable.
        var unsortedClips = CollectUnreachableClips(objectives, promptClips);

        var subject = new ReviewSubjectDto(
            GameId: game.GameId,
            Header: header,
            Stats: stats,
            Objectives: objectives,
            HasObjectives: objectives.Count > 0,
            Form: form,
            Deaths: deaths,
            HasDeaths: deaths.Count > 0,
            Evidence: evidence,
            MatchupHistory: matchupHistory,
            HasMatchupHistory: matchupHistory.Count > 0,
            TagCatalog: tagCatalog,
            // Fully-untagged moments PLUS any prompt/objective-tagged clip whose
            // prompt/objective isn't rendered — the "To sort" strip catches all.
            UnsortedClips: unsortedClips);

        return new ReviewDto(
            GeneratedAt: generatedAt,
            Subject: subject,
            SubjectSourceText: sourceText);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Subject selection: most-recent unreviewed, else most-recent reviewed.
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<(GameStats? Game, string SourceText)> PickSubjectAsync(long? gameId = null)
    {
        // Explicit game requested (clicking a row) — load exactly that one.
        if (gameId is > 0)
        {
            try
            {
                var specific = await _gameRepo.GetAsync(gameId.Value);
                if (specific is not null)
                {
                    return (specific, "");
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Review: explicit game {GameId} load failed", gameId);
            }
            // fall through to sample selection if the id wasn't found
        }

        try
        {
            // Unreviewed games in the last N days, newest first — the canonical
            // review queue. The first item is the freshest game still needing a
            // review, which is the most useful sample subject.
            var unreviewed = await _gameHistory.GetUnreviewedGamesAsync(days: UnreviewedWindowDays);
            var firstUnreviewed = unreviewed.FirstOrDefault();
            if (firstUnreviewed is not null)
            {
                return (firstUnreviewed, "Most recent unreviewed game");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Review: unreviewed-queue scan failed");
        }

        try
        {
            // Nothing unreviewed in the window — fall back to the single most
            // recent game (likely already reviewed) so the page still previews.
            var recent = await _gameHistory.GetRecentAsync(limit: 1, offset: 0);
            var mostRecent = recent.FirstOrDefault();
            if (mostRecent is not null)
            {
                var source = HasPersistedReview(mostRecent)
                    ? "Most recent reviewed game"
                    : "Most recent game";
                return (mostRecent, source);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Review: recent-game fallback scan failed");
        }

        return (null, "");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Header (mirror ReviewViewModel.ApplyGameData)
    // ─────────────────────────────────────────────────────────────────────────

    private ReviewHeaderDto BuildHeader(GameStats game)
    {
        var duration = game.GameDuration > 0
            ? $"{game.GameDuration / 60}:{game.GameDuration % 60:D2}"
            : "";

        var date = game.Timestamp > 0
            ? DateTimeOffset.FromUnixTimeSeconds(game.Timestamp).LocalDateTime.ToString("MMM dd, HH:mm")
            : "";

        var gameRole = string.IsNullOrWhiteSpace(game.Position)
            ? (_configService.PrimaryRole ?? "")
            : game.Position;

        // Role-aware matchup heading — 2v2 pairing when the participant map is
        // present, else 1v1 (shared with the games-list pill).
        var matchupHeading = MatchupDisplay.Build(
            game.ChampionName, game.EnemyLaner, game.Position, game.ParticipantMap);

        var metaLine = BuildMetaLine(game.DisplayGameMode, date, duration);
        var (laningLine, hasLaning) = BuildLaningLine(game);

        return new ReviewHeaderDto(
            ChampionName: game.ChampionName,
            EnemyChampion: game.EnemyLaner,
            MatchupHeading: matchupHeading,
            GameRole: gameRole,
            Win: game.Win,
            ResultText: game.Win ? "VICTORY" : "DEFEAT",
            WinLossText: game.Win ? "W" : "L",
            ResultColorHex: game.Win ? WinHex : LossHex,
            Kills: game.Kills,
            Deaths: game.Deaths,
            Assists: game.Assists,
            KdaRatio: game.KdaRatio,
            KdaText: $"{game.Kills} / {game.Deaths} / {game.Assists}",
            KdaRatioText: $"({game.KdaRatio:F2})",
            GameMode: game.DisplayGameMode,
            Duration: duration,
            DatePlayed: date,
            MetaLine: metaLine,
            HasReview: HasPersistedReview(game),
            LaningAt10Line: laningLine,
            HasLaningAt10: hasLaning);
    }

    /// <summary>
    /// Mirror of ReviewViewModel.ApplyGameData's laning-at-10 block: the
    /// canonical "did I win lane?" line from the Match-V5 timeline backfill.
    /// Empty until the backfill has populated CsAt10 for this game.
    /// </summary>
    private static (string Line, bool Has) BuildLaningLine(GameStats game)
    {
        if (game.CsAt10 is not { } csAt10)
        {
            return ("", false);
        }

        var line = $"LANING @10 // CS {Revu.Core.Services.ObjectiveCriteria.FormatValue(csAt10)}";
        if (game.GoldDiffAt10 is { } goldDiff)
        {
            line += $" · GOLD DIFF {(goldDiff >= 0 ? "+" : "")}{goldDiff}";
        }
        if (game.CsDiffAt10 is { } csDiff)
        {
            line += $" · CS DIFF {(csDiff >= 0 ? "+" : "")}{Revu.Core.Services.ObjectiveCriteria.FormatValue(csDiff)}";
        }
        return (line, true);
    }

    /// <summary>Mirror of GameDisplayItem.MetaLine: "GAMEMODE  ·  DATE  ·  DURATION".</summary>
    private static string BuildMetaLine(string gameMode, string datePlayed, string duration)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(gameMode)) parts.Add(gameMode.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(datePlayed)) parts.Add(datePlayed.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(duration)) parts.Add(duration);
        return string.Join("  ·  ", parts);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Stat strip — the full 8-up set ReviewViewModel.ApplyGameData shows:
    // Damage / CS / Vision / Gold / Kill Part. / Dmg Taken / Wards / KDA.
    // (The dashboard snapshot ships 6; the review surface shows all 8 + the
    // laning@10 line, which rides on the header.) Uses the dashboard
    // FormatNumber convention; no invented metrics — all off GameStats.
    // ─────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<ReviewStatDto> BuildStatStrip(GameStats game)
    {
        var killPart = game.KillParticipation > 0
            ? $"{game.KillParticipation:F0}%"
            : "—";

        return new[]
        {
            new ReviewStatDto("Damage", FormatNumber(game.TotalDamageToChampions), ""),
            new ReviewStatDto("CS", game.CsTotal.ToString(), $"{game.CsPerMin:F1}/min"),
            new ReviewStatDto("Vision", game.VisionScore.ToString(), ""),
            new ReviewStatDto("Gold", FormatNumber(game.GoldEarned), ""),
            new ReviewStatDto("Kill Part.", killPart, ""),
            new ReviewStatDto("Dmg Taken", FormatNumber(game.TotalDamageTaken), ""),
            new ReviewStatDto("Wards", game.WardsPlaced.ToString(), "placed"),
            new ReviewStatDto("KDA", $"{game.Kills}/{game.Deaths}/{game.Assists}", $"{game.KdaRatio:F2} KDA"),
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Objective-practice context (active objectives, GetActiveAsync). Read-only.
    // Mirrors DashboardSnapshotBuilder.BuildActiveObjectivesAsync.
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<ReviewObjectiveDto>> BuildObjectivesAsync(
        long gameId, PromptClipContext promptClips)
    {
        var result = new List<ReviewObjectiveDto>();

        // Hydrate this game's saved prompt answers once (keyed by prompt id) so
        // each prompt below can show what the user actually wrote (mirrors
        // ReviewViewModel.HydratePromptsAsync). Failure degrades to no answers.
        var answersByPromptId = new Dictionary<long, string>();
        try
        {
            var saved = await _promptsRepo.GetAnswersForGameAsync(gameId);
            foreach (var a in saved)
            {
                answersByPromptId[a.PromptId] = a.AnswerText;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Review: prompt-answer hydrate failed for game {GameId}", gameId);
        }

        // SAVED per-game practice state (practiced flag + execution note) keyed by
        // objective id, so the review re-renders each toggle in its persisted state.
        var practiceByObjective = new Dictionary<long, (bool Practiced, string ExecutionNote)>();
        try
        {
            foreach (var rec in await _objectivesRepo.GetGameObjectivesAsync(gameId))
            {
                practiceByObjective[rec.ObjectiveId] = (rec.Practiced, rec.ExecutionNote ?? "");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Review: game-objective practice hydrate failed for game {GameId}", gameId);
        }

        try
        {
            var objectives = await _objectivesRepo.GetActiveAsync();
            foreach (var obj in objectives)
            {
                var info = IObjectivesRepository.GetLevelInfo(obj.Score, obj.GameCount);
                var isMini = obj.IsMini;

                var progress = isMini && obj.TargetGameCount > 0
                    ? Math.Clamp((double)obj.GameCount / obj.TargetGameCount, 0.0, 1.0)
                    : info.Progress;

                var levelColorHex = isMini ? GoldHex : ObjectiveLevelHex(info.LevelIndex);
                var levelDimColorHex = isMini ? RingTrackHex : ObjectiveLevelDimHex(info.LevelIndex);

                var phaseLabel = ObjectivePhases.ToDisplayLabel(obj.Phase);
                var levelName = isMini ? "" : info.LevelName;
                var progressLabel = $"{Math.Clamp((int)Math.Round(progress * 100.0), 0, 100)}%";
                var metaText = BuildObjectiveMetaText(
                    isMini, obj.TargetGameCount, obj.GameCount, phaseLabel, levelName, obj.Score);

                // Custom coaching prompts the user authored for this objective,
                // each carrying THIS game's clips tagged to that prompt (P-027).
                // Skip BLANK-LABEL prompts: the frontend (review.js renderObjectives:
                // `if (!label) continue`) never renders them, so if we listed one here
                // CollectUnreachableClips would treat it as "rendered" and skip its
                // clips — orphaning a clip tagged to a blank-label prompt (reachable
                // via the R-002 champ-select if-then authoring). Excluding it here
                // keeps C# and JS in lockstep so its clips fall into "To sort".
                var prompts = new List<ReviewPromptDto>();
                try
                {
                    var raw = await _promptsRepo.GetPromptsForObjectiveAsync(obj.Id);
                    prompts.AddRange(raw
                        .Where(p => !string.IsNullOrWhiteSpace(p.Label))
                        .OrderBy(p => p.SortOrder)
                        .Select(p => new ReviewPromptDto(
                            p.Id,
                            p.Phase,
                            p.Label,
                            answersByPromptId.TryGetValue(p.Id, out var ans) ? ans : "",
                            promptClips.ClipsByPromptId.TryGetValue(p.Id, out var clips)
                                ? clips
                                : Array.Empty<ReviewPromptClipDto>())));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Review: prompts load failed for objective {Id}", obj.Id);
                }

                // No-prompt homing: clips tagged to this objective but to no prompt.
                var unpromptedClips = promptClips.UnpromptedByObjectiveId.TryGetValue(obj.Id, out var uc)
                    ? uc
                    : Array.Empty<ReviewPromptClipDto>();

                result.Add(new ReviewObjectiveDto(
                    Id: obj.Id,
                    Title: obj.Title,
                    CompletionCriteria: obj.CompletionCriteria,
                    PhaseLabel: phaseLabel,
                    IsMini: isMini,
                    IsPriority: obj.IsPriority,
                    LevelName: levelName,
                    Score: obj.Score,
                    GameCount: obj.GameCount,
                    TargetGameCount: obj.TargetGameCount,
                    Progress: progress,
                    ProgressLabel: progressLabel,
                    LevelColorHex: levelColorHex,
                    LevelDimColorHex: levelDimColorHex,
                    MetaText: metaText,
                    Practiced: practiceByObjective.TryGetValue(obj.Id, out var pr) && pr.Practiced,
                    ExecutionNote: practiceByObjective.TryGetValue(obj.Id, out var pr2) ? pr2.ExecutionNote : "",
                    Prompts: prompts,
                    UnpromptedClips: unpromptedClips));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Review: active objectives load failed");
        }
        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // P-027 prompt-clip grouping. Load THIS game's evidence once and bucket it:
    //   • ClipsByPromptId        — prompt_id set → groups under each prompt
    //   • UnpromptedByObjectiveId — objective set, prompt_id null → per-objective
    //                                "Objective evidence (no prompt)" sub-block
    //   • UnsortedClips          — neither objective nor prompt → "To sort" strip
    // Share URLs come from the backing bookmark (clip rows' SourceId IS the
    // bookmark id), mirroring VodSnapshotBuilder. Degrades to empty on failure so
    // a bad evidence/bookmark read never blanks the objectives section. Dismissed
    // rows are excluded everywhere (GetForGameAsync default).
    // ─────────────────────────────────────────────────────────────────────────

    private sealed record PromptClipContext(
        IReadOnlyDictionary<long, IReadOnlyList<ReviewPromptClipDto>> ClipsByPromptId,
        IReadOnlyDictionary<long, IReadOnlyList<ReviewPromptClipDto>> UnpromptedByObjectiveId,
        IReadOnlyList<ReviewPromptClipDto> UnsortedClips);

    private static readonly PromptClipContext EmptyPromptClips = new(
        new Dictionary<long, IReadOnlyList<ReviewPromptClipDto>>(),
        new Dictionary<long, IReadOnlyList<ReviewPromptClipDto>>(),
        Array.Empty<ReviewPromptClipDto>());

    private async Task<PromptClipContext> BuildPromptClipContextAsync(long gameId)
    {
        try
        {
            var rows = await _evidenceRepo.GetForGameAsync(gameId);

            // bookmarkId → ShareUrl, so clip rows can carry their share state.
            var shareUrlByBookmarkId = new Dictionary<long, string>();
            try
            {
                foreach (var b in await _vodRepo.GetBookmarksAsync(gameId))
                {
                    if (!string.IsNullOrWhiteSpace(b.ShareUrl)) shareUrlByBookmarkId[b.Id] = b.ShareUrl;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Review: bookmark share-url load failed for game {GameId}", gameId);
            }

            var clipsByPromptId = new Dictionary<long, List<ReviewPromptClipDto>>();
            var unpromptedByObjectiveId = new Dictionary<long, List<ReviewPromptClipDto>>();
            var unsorted = new List<ReviewPromptClipDto>();

            foreach (var row in rows
                .OrderBy(r => r.StartTimeSeconds ?? int.MaxValue)
                .ThenBy(r => r.Id))
            {
                var clip = MapPromptClip(row, shareUrlByBookmarkId);
                if (row.PromptId is long pid)
                {
                    if (!clipsByPromptId.TryGetValue(pid, out var list))
                        clipsByPromptId[pid] = list = new List<ReviewPromptClipDto>();
                    list.Add(clip);
                }
                else if (row.ObjectiveId is long oid)
                {
                    if (!unpromptedByObjectiveId.TryGetValue(oid, out var list))
                        unpromptedByObjectiveId[oid] = list = new List<ReviewPromptClipDto>();
                    list.Add(clip);
                }
                else
                {
                    unsorted.Add(clip);
                }
            }

            return new PromptClipContext(
                clipsByPromptId.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ReviewPromptClipDto>)kv.Value),
                unpromptedByObjectiveId.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ReviewPromptClipDto>)kv.Value),
                unsorted);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Review: prompt-clip context load failed for game {GameId}", gameId);
            return EmptyPromptClips;
        }
    }

    // Fold every prompt/objective clip bucket that NO rendered objective consumed
    // into the "To sort" strip, so a clip tagged to an archived objective or a
    // deleted prompt stays reachable. Starts from the genuinely-untagged
    // UnsortedClips, then appends the orphaned buckets (de-duped by evidence id,
    // ordered by start time) — mirrors the in-objective ordering.
    private static IReadOnlyList<ReviewPromptClipDto> CollectUnreachableClips(
        IReadOnlyList<ReviewObjectiveDto> objectives, PromptClipContext promptClips)
    {
        // Which prompt ids and objective ids actually got rendered.
        var renderedPromptIds = new HashSet<long>();
        var renderedObjectiveIds = new HashSet<long>();
        foreach (var o in objectives)
        {
            renderedObjectiveIds.Add(o.Id);
            foreach (var p in o.Prompts) renderedPromptIds.Add(p.Id);
        }

        var seen = new HashSet<long>();
        var result = new List<ReviewPromptClipDto>();
        void Add(ReviewPromptClipDto c) { if (seen.Add(c.EvidenceId)) result.Add(c); }

        // Genuinely-untagged moments first (the original "To sort" set).
        foreach (var c in promptClips.UnsortedClips) Add(c);
        // Prompt-tagged clips whose prompt isn't rendered (objective archived /
        // prompt deleted).
        foreach (var kv in promptClips.ClipsByPromptId)
            if (!renderedPromptIds.Contains(kv.Key))
                foreach (var c in kv.Value) Add(c);
        // Objective-tagged (no-prompt) clips whose objective isn't rendered.
        foreach (var kv in promptClips.UnpromptedByObjectiveId)
            if (!renderedObjectiveIds.Contains(kv.Key))
                foreach (var c in kv.Value) Add(c);

        return result
            .OrderBy(c => c.StartSeconds <= 0 ? int.MaxValue : c.StartSeconds)
            .ThenBy(c => c.EvidenceId)
            .ToList();
    }

    private ReviewPromptClipDto MapPromptClip(
        EvidenceItemRecord row, IReadOnlyDictionary<long, string> shareUrlByBookmarkId)
    {
        var timeText = "";
        if (row.StartTimeSeconds is int start)
        {
            timeText = FormatGameTime(start);
            if (row.EndTimeSeconds is int end && end > start)
            {
                timeText += $"–{FormatGameTime(end)}";
            }
        }

        // A clip row's SourceId IS the bookmark id the Share button targets.
        var shareUrl = "";
        if (string.Equals(row.SourceKind, EvidenceKinds.Clip, StringComparison.OrdinalIgnoreCase)
            && row.SourceId is long bmId && bmId > 0
            && shareUrlByBookmarkId.TryGetValue(bmId, out var u))
        {
            shareUrl = u;
        }

        return new ReviewPromptClipDto(
            EvidenceId: row.Id,
            TimeText: timeText,
            Note: string.IsNullOrWhiteSpace(row.Note) ? row.Title : row.Note,
            StartSeconds: row.StartTimeSeconds ?? 0,
            Polarity: row.Polarity,
            PolarityColorHex: PolarityHex(row.Polarity),
            ShareUrl: shareUrl);
    }

    /// <summary>Mirror of DashboardObjectiveItem.MetaText.</summary>
    private static string BuildObjectiveMetaText(
        bool isMini, int targetGameCount, int gameCount, string phaseLabel, string levelName, int score)
    {
        if (isMini)
        {
            var games = targetGameCount > 0
                ? $"{Math.Min(gameCount, targetGameCount)}/{targetGameCount} GAMES"
                : $"{gameCount} GAMES";
            return $"FOCUS  ·  {phaseLabel.ToUpperInvariant()}  ·  {games}";
        }
        return string.IsNullOrWhiteSpace(levelName)
            ? phaseLabel.ToUpperInvariant()
            : $"{levelName.ToUpperInvariant()}  ·  {phaseLabel.ToUpperInvariant()}  ·  {score} PTS";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Review form — READ-ONLY saved values. Editing/save are DEFERRED mutations.
    // Text fields read straight off the GameStats row; the mental rating comes
    // from the per-game (game_id → mental_rating) map on session_log.
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<ReviewFormDto> BuildFormAsync(GameStats game)
    {
        var mentalRating = await ResolveMentalRatingAsync(game.GameId);
        var focusAdherence = await ResolveFocusAdherenceAsync(game.GameId);

        return new ReviewFormDto(
            Editable: false,
            EditingNote: EditingNoteText[0],
            MentalRating: mentalRating,
            MentalRatingColorHex: MentalRatingHex(mentalRating),
            WentWell: game.WentWell,
            Mistakes: game.Mistakes,
            FocusNext: game.FocusNext,
            ReviewNotes: game.ReviewNotes,
            SpottedProblems: game.SpottedProblems,
            Attribution: game.Attribution,
            PersonalContribution: game.PersonalContribution,
            OutsideControl: game.OutsideControl,
            WithinControl: game.WithinControl,
            TagsJson: string.IsNullOrWhiteSpace(game.Tags) ? "[]" : game.Tags,
            FocusAdherence: focusAdherence);
    }

    // The saved FOCUS CHECK answer for this game off session_log.focus_adherence
    // (2=Yes / 1=Partly / 0=No; null = unanswered). The write side persists it but
    // the read snapshot never carried it back — so the gold selection vanished on
    // every re-render and never preselected on load (P-028). Mirror the mental
    // lookup: tolerate a missing/failed read by returning null (unanswered).
    private async Task<int?> ResolveFocusAdherenceAsync(long gameId)
    {
        try
        {
            var entry = await _sessionLogRepo.GetEntryAsync(gameId);
            return entry?.FocusAdherence;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Review: focus-adherence lookup failed for game {GameId}", gameId);
            return null;
        }
    }

    private async Task<int> ResolveMentalRatingAsync(long gameId)
    {
        try
        {
            var ratings = await _sessionLogRepo.GetAllMentalRatingsAsync();
            if (ratings.TryGetValue(gameId, out var rating) && rating > 0)
            {
                return rating;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Review: mental-rating lookup failed for game {GameId}", gameId);
        }
        return 0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Death audit — one row per DEATH event from the live kill feed, joined to
    // the saved cause classification (mirror ReviewViewModel.LoadDeathAuditAsync).
    // The six cause chips mirror DeathClasses.All; IsSelected reflects the saved
    // class. Read-only (classify/clear are DEFERRED writes).
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<ReviewDeathDto>> BuildDeathsAsync(long gameId)
    {
        var result = new List<ReviewDeathDto>();
        try
        {
            var events = await _eventsRepo.GetEventsAsync(gameId);
            var deaths = events
                .Where(static e => string.Equals(e.EventType, GameEvent.EventTypes.Death, StringComparison.OrdinalIgnoreCase))
                .OrderBy(static e => e.GameTimeS)
                .ToList();
            if (deaths.Count == 0)
            {
                return result;
            }

            var saved = await _deathClassRepo.GetForGameAsync(gameId);
            // Same dedupe as the VM: first saved class wins per (game, second).
            var savedByTime = saved
                .GroupBy(static c => c.GameTimeSeconds)
                .ToDictionary(static g => g.Key, static g => g.First().DeathClass);

            foreach (var death in deaths)
            {
                savedByTime.TryGetValue(death.GameTimeS, out var selectedClass);
                selectedClass ??= "";

                var chips = DeathClasses.All
                    .Select(c => new ReviewDeathChipDto(
                        Key: c.Key,
                        Label: c.Label,
                        Hint: c.Hint,
                        IsSelected: string.Equals(selectedClass, c.Key, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                var isClassified = !string.IsNullOrWhiteSpace(selectedClass);
                result.Add(new ReviewDeathDto(
                    GameTimeSeconds: death.GameTimeS,
                    TimeText: FormatGameTime(death.GameTimeS),
                    SelectedClass: selectedClass,
                    SelectedLabel: isClassified ? DeathClasses.LabelFor(selectedClass) : "",
                    IsClassified: isClassified,
                    Chips: chips));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Review: death audit load failed for game {GameId}", gameId);
        }
        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Evidence triage — the attached (capped, prioritized) list + the uncapped
    // "EVIDENCE TO SORT" inbox of unassigned, non-dismissed moments. Both derive
    // from the SAME GetForGameAsync row set so they can't drift (P-013). Read-only
    // (polarity/status/objective triage are DEFERRED writes).
    //
    // NB: the WinUI VM ALSO upserts auto timeline-region + clip evidence rows on
    // load (a WRITE). The read-only sidecar must NOT write, so we ship whatever
    // GetForGameAsync already returns — the WinUI app remains the writer.
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<ReviewEvidenceDto> BuildEvidenceAsync(long gameId)
    {
        try
        {
            var allRows = await _evidenceRepo.GetForGameAsync(gameId);

            // Attached: assigned-to-an-objective OR a real clip OR already triaged
            // as evidence/highlight — the prioritized, capped set (mirror
            // ReviewViewModel.PrioritizeEvidenceRows, capped at MaxEvidenceInbox).
            var attached = allRows
                .Where(static row => row.SourceKind == EvidenceKinds.Clip
                    || row.ObjectiveId.HasValue
                    || row.Status is EvidenceStatuses.Evidence or EvidenceStatuses.Highlight)
                .OrderByDescending(static row => row.ObjectiveId.HasValue)
                .ThenByDescending(static row => row.SourceKind == EvidenceKinds.Clip)
                .ThenBy(static row => row.Status == EvidenceStatuses.NeedsReview ? 0 : 1)
                .ThenBy(static row => row.Polarity == EvidencePolarities.Bad ? 0
                    : row.Polarity == EvidencePolarities.Good ? 1 : 2)
                .ThenBy(static row => row.StartTimeSeconds ?? int.MaxValue)
                .Take(MaxEvidenceInbox)
                .Select(MapEvidence)
                .ToList();

            // Unassigned inbox: ALL unassigned, non-dismissed moments (uncapped),
            // so nothing the export shows is hidden here (P-013).
            var unassigned = allRows
                .Where(static row => !row.ObjectiveId.HasValue
                    && row.Status != EvidenceStatuses.Dismissed)
                .OrderBy(static row => row.StartTimeSeconds ?? int.MaxValue)
                .ThenBy(static row => row.Id)
                .Select(MapEvidence)
                .ToList();

            return new ReviewEvidenceDto(
                Attached: attached,
                HasAttached: attached.Count > 0,
                Unassigned: unassigned,
                HasUnassigned: unassigned.Count > 0);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Review: evidence load failed for game {GameId}", gameId);
            return new ReviewEvidenceDto(
                Array.Empty<ReviewEvidenceItemDto>(), false,
                Array.Empty<ReviewEvidenceItemDto>(), false);
        }
    }

    private ReviewEvidenceItemDto MapEvidence(EvidenceItemRecord row)
    {
        var timeText = "";
        if (row.StartTimeSeconds is int start)
        {
            timeText = FormatGameTime(start);
            if (row.EndTimeSeconds is int end && end > start)
            {
                timeText += $"–{FormatGameTime(end)}";
            }
        }

        return new ReviewEvidenceItemDto(
            Id: row.Id,
            SourceKind: row.SourceKind,
            StartTimeSeconds: row.StartTimeSeconds,
            EndTimeSeconds: row.EndTimeSeconds,
            TimeText: timeText,
            Title: row.Title,
            Note: row.Note,
            ObjectiveId: row.ObjectiveId,
            ObjectiveTitle: row.ObjectiveTitle,
            Polarity: row.Polarity,
            Status: row.Status,
            PolarityColorHex: PolarityHex(row.Polarity),
            StatusLabel: StatusLabel(row.Status));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Matchup history — past notes for the same champ-vs-enemy matchup, newest
    // first, excluding this game's own note (mirror ReviewWorkflowService
    // .GetMatchupHistoryAsync + ReviewViewModel.BuildMatchupMetaText). Read-only.
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<ReviewMatchupHistoryDto>> BuildMatchupHistoryAsync(GameStats game)
    {
        var result = new List<ReviewMatchupHistoryDto>();

        var champion = game.ChampionName ?? "";
        var enemy = game.EnemyLaner ?? "";
        if (string.IsNullOrWhiteSpace(champion) || string.IsNullOrWhiteSpace(enemy))
        {
            return result;
        }

        try
        {
            var notes = await _matchupNotesRepo.GetForMatchupAsync(champion, enemy.Trim());
            foreach (var note in notes
                .Where(n => n.GameId != game.GameId && !string.IsNullOrWhiteSpace(n.Note))
                .OrderByDescending(n => n.CreatedAt ?? 0))
            {
                result.Add(new ReviewMatchupHistoryDto(
                    Note: note.Note,
                    Helpful: ParseHelpful(note.Helpful),
                    MetaText: BuildMatchupMetaText(note.GameId, note.CreatedAt, note.Helpful)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Review: matchup history load failed for game {GameId}", game.GameId);
        }
        return result;
    }

    /// <summary>Mirror of ReviewWorkflowService.ParseHelpful.</summary>
    private static bool? ParseHelpful(int? value) => value switch
    {
        1 => true,
        0 => false,
        _ => null,
    };

    /// <summary>Mirror of ReviewViewModel.BuildMatchupMetaText.</summary>
    private static string BuildMatchupMetaText(long? gameId, long? createdAt, int? helpful)
    {
        var createdText = createdAt is > 0
            ? DateTimeOffset.FromUnixTimeSeconds(createdAt.Value).LocalDateTime.ToString("MMM d, yyyy HH:mm")
            : "Unknown date";
        var helpfulText = ParseHelpful(helpful) switch
        {
            true => "Helpful",
            false => "Not helpful",
            null => "Unrated",
        };

        return gameId is > 0
            ? $"Game {gameId}  ·  {createdText}  ·  {helpfulText}"
            : $"{createdText}  ·  {helpfulText}";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Concept-tag catalog — the full tag list with per-tag selection state for
    // THIS game (mirror ReviewWorkflowService tagStates build:
    // IConceptTagRepository.GetAllAsync + GetIdsForGameAsync). Read-only — the
    // selectable toggle grid is a DEFERRED write.
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<ReviewTagDto>> BuildTagCatalogAsync(long gameId)
    {
        var result = new List<ReviewTagDto>();
        try
        {
            var tags = await _conceptTagRepo.GetAllAsync();
            var selectedIds = (await _conceptTagRepo.GetIdsForGameAsync(gameId)).ToHashSet();

            foreach (var tag in tags)
            {
                result.Add(new ReviewTagDto(
                    Id: tag.Id,
                    Name: tag.Name,
                    Polarity: tag.Polarity,
                    // ConceptTagRecord.Color is already a hex string; passthrough.
                    ColorHex: tag.Color,
                    IsSelected: selectedIds.Contains(tag.Id)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Review: tag catalog load failed for game {GameId}", gameId);
        }
        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Shared helpers (mirrors of the dashboard builder / ReviewViewModel)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Mirror of DashboardViewModel.HasPersistedReview.</summary>
    private static bool HasPersistedReview(GameStats game)
    {
        return game.Rating > 0
               || !string.IsNullOrWhiteSpace(game.ReviewNotes)
               || !string.IsNullOrWhiteSpace(game.Mistakes)
               || !string.IsNullOrWhiteSpace(game.WentWell)
               || !string.IsNullOrWhiteSpace(game.FocusNext)
               || !string.IsNullOrWhiteSpace(game.SpottedProblems)
               || !string.IsNullOrWhiteSpace(game.OutsideControl)
               || !string.IsNullOrWhiteSpace(game.WithinControl)
               || !string.IsNullOrWhiteSpace(game.Attribution)
               || !string.IsNullOrWhiteSpace(game.PersonalContribution);
    }

    private static string FormatNumber(int n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:F1}M",
        >= 1_000 => $"{n / 1_000.0:F1}k",
        _ => n.ToString()
    };

    /// <summary>Mirror of ReviewViewModel.FormatGameTime: "MM:SS" (clamped ≥0).</summary>
    private static string FormatGameTime(int seconds)
    {
        if (seconds < 0) seconds = 0;
        return $"{seconds / 60:D2}:{seconds % 60:D2}";
    }

    /// <summary>Evidence polarity accent: bad → loss, good → win, else accent.</summary>
    private static string PolarityHex(string? polarity) => (polarity ?? "").Trim().ToLowerInvariant() switch
    {
        EvidencePolarities.Bad => LossHex,
        EvidencePolarities.Good => WinHex,
        _ => AccentHex,
    };

    /// <summary>Human label for an evidence status key.</summary>
    private static string StatusLabel(string? status) => (status ?? "").Trim().ToLowerInvariant() switch
    {
        EvidenceStatuses.NeedsReview => "Needs review",
        EvidenceStatuses.Evidence => "Evidence",
        EvidenceStatuses.Highlight => "Highlight",
        EvidenceStatuses.Dismissed => "Dismissed",
        _ => "",
    };

    /// <summary>
    /// Mirror of Revu.App.Styling.AppSemanticPalette.MentalRatingHex: low ratings
    /// read negative (red), high ratings positive (green), mid neutral accent.
    /// </summary>
    private static string MentalRatingHex(int rating) => rating switch
    {
        <= 0 => PrimaryTextHex,
        <= 3 => LossHex,
        <= 6 => GoldHex,
        _ => WinHex,
    };

    // ─────────────────────────────────────────────────────────────────────────
    // Objective level palette — same ramp as DashboardSnapshotBuilder.
    // Mirrors Revu.App.Styling.AppSemanticPalette.ObjectiveLevelHex/DimHex.
    // ─────────────────────────────────────────────────────────────────────────

    private static string ObjectiveLevelHex(int levelIndex) => levelIndex switch
    {
        0 => "#7B8494",   // Exploring: Slate
        1 => "#5EC4D4",   // Drilling: Cyan
        2 => "#D4A44E",   // Ingraining: Amber
        3 => "#E8C15E",   // Ready: Bright gold
        _ => "#8A80A8",   // Neutral
    };

    private static string ObjectiveLevelDimHex(int levelIndex) => levelIndex switch
    {
        0 => "#10121A",
        1 => "#0E1A1E",
        2 => "#1E1810",
        3 => "#221C0E",
        _ => "#13111E",
    };
}

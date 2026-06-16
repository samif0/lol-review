#nullable enable

using Microsoft.Extensions.Logging;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>
/// Builds the read-only dashboard snapshot served at GET /api/dashboard.
///
/// <para>
/// Reproduces <c>DashboardViewModel.LoadAsync</c>'s data loading + formatting
/// EXACTLY, minus all WinUI/dispatcher concerns, and emits the camelCase JSON
/// contract (see <see cref="DashboardDto"/> and desktop/sample-dashboard.json).
/// It deliberately does NOT reference the WinUI ViewModel — only the Core repo
/// and query interfaces.
/// </para>
///
/// <para>
/// Like the VM, the death-mix / 30-day baseline / vod-pending / patterns blocks
/// are each wrapped in try/catch that degrades to empty/false/[] so one failing
/// section never blanks the whole dashboard.
/// </para>
///
/// <para>
/// COLOR PARITY: the WinUI app sources its hex constants from
/// <c>Revu.App.Styling.AppSemanticPalette</c>, which lives in the WinUI project
/// and isn't visible to Core. For v1 we hardcode the glass-aurora mockup palette
/// here (accent #9d8bff, gold #f3c794, win #8ee7ba, loss #f3a3a8, objective
/// level colors). TODO: lift these constants into Revu.Core so the app and the
/// sidecar share one source of truth.
/// </para>
/// </summary>
public sealed class DashboardSnapshotBuilder
{
    // ── Tunables (mirror DashboardViewModel) ────────────────────────────────
    private const int BaselineMinGames = 5;
    private const int DeathMixMinTagged = 5;
    private const int UnreviewedTake = 8;
    private const int PatternsTake = 4;

    // ── Mockup palette (TODO: extract to Revu.Core) ─────────────────────────
    private const string AccentHex = "#9d8bff";
    private const string GoldHex = "#f3c794";
    private const string WinHex = "#8ee7ba";
    private const string LossHex = "#f3a3a8";
    private const string RingTrackHex = "rgba(255,255,255,0.13)";

    private readonly ISessionLogRepository _sessionLogRepo;
    private readonly IObjectivesRepository _objectivesRepo;
    private readonly IVodRepository _vodRepo;
    private readonly IEvidenceRepository _evidenceRepo;
    private readonly IDeathClassificationsRepository _deathClassRepo;
    private readonly IRulesRepository _rulesRepo;
    private readonly IGameHistoryQuery _gameHistory;
    private readonly IConfigService _configService;
    private readonly ILogger<DashboardSnapshotBuilder> _logger;

    public DashboardSnapshotBuilder(
        ISessionLogRepository sessionLogRepo,
        IObjectivesRepository objectivesRepo,
        IVodRepository vodRepo,
        IEvidenceRepository evidenceRepo,
        IDeathClassificationsRepository deathClassRepo,
        IRulesRepository rulesRepo,
        IGameHistoryQuery gameHistory,
        IConfigService configService,
        ILogger<DashboardSnapshotBuilder> logger)
    {
        _sessionLogRepo = sessionLogRepo;
        _objectivesRepo = objectivesRepo;
        _vodRepo = vodRepo;
        _evidenceRepo = evidenceRepo;
        _deathClassRepo = deathClassRepo;
        _rulesRepo = rulesRepo;
        _gameHistory = gameHistory;
        _configService = configService;
        _logger = logger;
    }

    public async Task<DashboardDto> BuildAsync(CancellationToken ct = default)
    {
        var now = DateTime.Now;
        var today = now.ToString("yyyy-MM-dd");

        // ── Today's session stats ───────────────────────────────────────────
        var stats = await _sessionLogRepo.GetStatsForDateAsync(today);
        var totalGames = stats.Games;
        var wins = stats.Wins;
        var losses = stats.Losses;
        var avgMental = stats.AvgMental;

        var recordLine = (wins + losses) == 0 ? "" : $"{wins}W // {losses}L";
        var winratePercent = ComputeWinratePercent(wins, losses);

        // ── Adherence streak (behavioral) ───────────────────────────────────
        var adherenceStreak = await _rulesRepo.GetBehavioralAdherenceStreakAsync();
        var adherenceSub = adherenceStreak > 0 ? "DAYS W/O RULE TRIPS" : "NO ACTIVE STREAK";

        // ── Start Block intent / End Block debrief ──────────────────────────
        // Prefer today's session. A block is "open" until it's ended, so if today
        // has no open block, carry over an unfinished one from YESTERDAY (only) so a
        // block you forgot to close last night can still be ended — without letting
        // ancient orphaned blocks trap the dashboard on End Block forever (older ones
        // are treated as abandoned and you can Start Block fresh). BlockDate tells the
        // client which row End Block must target.
        var sessionInfo = await _sessionLogRepo.GetSessionAsync(today);
        var todayIsOpen = sessionInfo != null
            && !string.IsNullOrWhiteSpace(sessionInfo.Intention)
            && sessionInfo.DebriefRating <= 0
            && sessionInfo.EndedAt == null;

        var carriedOver = false;
        if (!todayIsOpen
            && (sessionInfo == null || string.IsNullOrWhiteSpace(sessionInfo.Intention)))
        {
            // Only carry over a block from yesterday onward — a just-missed close-out.
            var yesterday = now.AddDays(-1).ToString("yyyy-MM-dd");
            var openBlock = await _sessionLogRepo.GetOpenBlockAsync(yesterday);
            if (openBlock != null)
            {
                sessionInfo = openBlock;
                carriedOver = openBlock.Date != today;
            }
        }

        var sessionIntentionRaw = sessionInfo?.Intention?.Trim() ?? "";
        var debriefRating = sessionInfo?.DebriefRating ?? 0;
        // null in the contract = ritual not run / block not open (matches sample JSON).
        string? sessionIntention = string.IsNullOrWhiteSpace(sessionIntentionRaw)
            ? null
            : sessionIntentionRaw;
        int? debriefRatingOut = debriefRating > 0 ? debriefRating : null;
        // The date End Block must close out (the open block's own date), null when
        // there's no active intention.
        string? blockDate = sessionIntention != null ? sessionInfo?.Date : null;

        // ── Death mix (14d) — degrade to empty on failure ───────────────────
        var deathMix = await BuildDeathMixAsync();

        // ── 30-day baseline subs — degrade to recordLine/"" on failure ──────
        var (winRateSub, avgMentalSub) = await BuildBaselineSubsAsync(recordLine);

        // ── Patterns reviewed count + nag cards ─────────────────────────────
        var (reviewedPatternCount, patternsReviewedSub, patterns) = await BuildPatternsAsync();

        // ── Active objectives ───────────────────────────────────────────────
        var activeObjectives = await BuildActiveObjectivesAsync();

        // ── Unreviewed queue (Take 8) ───────────────────────────────────────
        var unreviewed = await BuildUnreviewedAsync();

        // ── VOD evidence pending ────────────────────────────────────────────
        var vodPending = await BuildVodPendingAsync();

        // ── Greeting + next-step copy (empty-state derived) ─────────────────
        var greeting = BuildGreeting(now);
        var nextStep = BuildNextStep();

        var statsDto = new DashboardStatsDto(
            TotalGames: totalGames,
            Wins: wins,
            Losses: losses,
            WinratePercent: winratePercent,
            WinRateSub: winRateSub,
            AvgMental: Math.Round(avgMental, 1),
            AvgMentalSub: avgMentalSub,
            AdherenceStreak: adherenceStreak,
            AdherenceSub: adherenceSub,
            ReviewedPatternCount: reviewedPatternCount,
            PatternsReviewedSub: patternsReviewedSub);

        return new DashboardDto(
            GeneratedAt: now.ToString("yyyy-MM-ddTHH:mm:ss"),
            Today: today,
            Greeting: greeting,
            Stats: statsDto,
            NextStep: nextStep,
            Intent: new IntentDto(sessionIntention, debriefRatingOut, blockDate, carriedOver),
            DeathMix: deathMix,
            VodPending: vodPending,
            Unreviewed: unreviewed,
            ActiveObjectives: activeObjectives,
            Patterns: patterns);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Section builders
    // ─────────────────────────────────────────────────────────────────────────

    private static string ComputeWinratePercent(int wins, int losses)
    {
        var games = wins + losses;
        if (games == 0) return "—";
        return $"{(int)Math.Round(100.0 * wins / games)}%";
    }

    private async Task<DeathMixDto> BuildDeathMixAsync()
    {
        try
        {
            var mix = await _deathClassRepo.GetClassMixAsync(days: 14);
            var tagged = mix.Sum(static m => m.Count);
            if (tagged >= DeathMixMinTagged && mix.Count > 0)
            {
                var top = mix[0];
                var percent = (int)Math.Round(100.0 * top.Count / tagged);
                var pct = $"{percent}%";
                // label: raw lowercase class key (e.g. "vision"), matching the
                // structured contract the frontend styles into cause/pct spans.
                var label = top.DeathClass;
                // text: ready-to-show sentence (the frontend recomposes this
                // from label/pct/sample, but we ship it for non-JS consumers).
                var text = $"Your most-tagged death cause is {label}: {pct} of the last {tagged} tagged deaths.";
                return new DeathMixDto(Text: text, Label: label, Pct: pct, Sample: tagged);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Dashboard: death mix computation failed");
        }
        return new DeathMixDto(Text: "", Label: "", Pct: "", Sample: 0);
    }

    private async Task<(string WinRateSub, string AvgMentalSub)> BuildBaselineSubsAsync(string recordLine)
    {
        try
        {
            var last30 = await _sessionLogRepo.GetDailySummariesAsync(days: 30);
            var baselineGames = last30.Sum(s => s.Games);
            var baselineWins = last30.Sum(s => s.Wins);

            var winRateBaseline = baselineGames >= BaselineMinGames
                ? $"30-DAY {(int)Math.Round(100.0 * baselineWins / baselineGames)}%"
                : "";
            var winRateSub = string.IsNullOrEmpty(winRateBaseline)
                ? recordLine
                : string.IsNullOrEmpty(recordLine)
                    ? winRateBaseline
                    : $"{recordLine}  ·  {winRateBaseline}";

            var ratedDays = last30.Where(s => s.AvgMental > 0).ToList();
            var ratedGames = ratedDays.Sum(s => s.Games);
            var avgMentalSub = ratedGames >= BaselineMinGames
                ? $"30-DAY AVG {ratedDays.Sum(s => s.AvgMental * s.Games) / ratedGames:F1}"
                : "";

            return (winRateSub, avgMentalSub);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Dashboard: 30-day baseline computation failed");
            return (recordLine, "");
        }
    }

    private async Task<(int Count, string Sub, PatternsDto Patterns)> BuildPatternsAsync()
    {
        try
        {
            var rawPatterns = await _evidenceRepo.GetPatternCardsAsync(limit: 6);
            var reviewedKeys = await _evidenceRepo.GetReviewedPatternKeysAsync();
            var reviewedCount = await _evidenceRepo.CountReviewedPatternsAsync();

            var pending = rawPatterns
                .Where(p => !reviewedKeys.Contains(p.PatternKey))
                .Take(PatternsTake)
                .Select(p => new ObjectivePatternItemDto(
                    Kind: p.Kind,
                    Title: p.Title,
                    Detail: p.Detail,
                    GameId: p.GameId,
                    ObjectiveId: p.ObjectiveId,
                    Severity: p.Severity,
                    // Severity "high" -> negative red, else gold (mirrors AccentBrush).
                    AccentHex: p.Severity == "high" ? LossHex : GoldHex))
                .ToList();

            var sub = reviewedCount == 0 ? "NONE YET" : "CROSS-GAME";
            return (reviewedCount, sub, new PatternsDto(Has: pending.Count > 0, Items: pending));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Dashboard: objective pattern load failed");
            // Reviewed count couldn't be read; degrade the nag to empty but keep
            // the sub coherent ("NONE YET" since we report 0).
            return (0, "NONE YET", new PatternsDto(Has: false, Items: Array.Empty<ObjectivePatternItemDto>()));
        }
    }

    private async Task<IReadOnlyList<ActiveObjectiveDto>> BuildActiveObjectivesAsync()
    {
        var result = new List<ActiveObjectiveDto>();
        try
        {
            var objectives = await _objectivesRepo.GetActiveAsync();
            foreach (var obj in objectives)
            {
                var info = IObjectivesRepository.GetLevelInfo(obj.Score, obj.GameCount);
                var isMini = obj.IsMini;

                // Minis fill by games done; mastery objectives by score arc.
                var progress = isMini && obj.TargetGameCount > 0
                    ? Math.Clamp((double)obj.GameCount / obj.TargetGameCount, 0.0, 1.0)
                    : info.Progress;

                var levelColorHex = isMini ? GoldHex : ObjectiveLevelHex(info.LevelIndex);
                var levelDimColorHex = isMini ? RingTrackHex : ObjectiveLevelDimHex(info.LevelIndex);

                var infoText = isMini
                    ? (obj.TargetGameCount > 0
                        ? $"FOCUS DRILL  •  {Math.Min(obj.GameCount, obj.TargetGameCount)} of {obj.TargetGameCount} games"
                        : $"FOCUS DRILL  •  {obj.GameCount} games")
                    : $"{info.LevelName}  •  {obj.Score} pts  •  {obj.GameCount} games";

                var phaseLabel = ObjectivePhases.ToDisplayLabel(obj.Phase);
                var levelName = isMini ? "" : info.LevelName;
                var progressLabel = $"{Math.Clamp((int)Math.Round(progress * 100.0), 0, 100)}%";
                var metaText = BuildObjectiveMetaText(isMini, obj.TargetGameCount, obj.GameCount, phaseLabel, levelName, obj.Score);

                result.Add(new ActiveObjectiveDto(
                    Id: obj.Id,
                    Title: obj.Title,
                    PhaseLabel: phaseLabel,
                    IsMini: isMini,
                    TargetGameCount: obj.TargetGameCount,
                    LevelName: levelName,
                    Score: obj.Score,
                    GameCount: obj.GameCount,
                    Progress: progress,
                    LevelColorHex: levelColorHex,
                    LevelDimColorHex: levelDimColorHex,
                    InfoText: infoText,
                    IsPriority: obj.IsPriority,
                    ProgressLabel: progressLabel,
                    MetaText: metaText));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Dashboard: active objectives load failed");
        }
        return result;
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

    private async Task<UnreviewedDto> BuildUnreviewedAsync()
    {
        try
        {
            var unreviewed = await _gameHistory.GetUnreviewedGamesAsync(days: 3);
            var count = unreviewed.Count;
            var items = unreviewed
                .Take(UnreviewedTake)
                .Select(MapGameDisplay)
                .ToList();

            return new UnreviewedDto(
                Count: count,
                CountText: $"{count} game{(count != 1 ? "s" : "")}",
                AllReviewed: count == 0,
                Items: items);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Dashboard: unreviewed queue load failed");
            return new UnreviewedDto(0, "0 games", AllReviewed: true, Items: Array.Empty<GameDisplayItemDto>());
        }
    }

    private async Task<VodPendingDto> BuildVodPendingAsync()
    {
        try
        {
            var recent = await _gameHistory.GetRecentAsync(limit: 30, offset: 0);
            var reviewed = recent.Where(HasPersistedReview).ToList();

            var vodPaths = await _vodRepo.GetVodPathsAsync(reviewed.Select(g => g.GameId).ToArray());
            var gamesWithAvailableVod = reviewed
                .Where(game => vodPaths.TryGetValue(game.GameId, out var path)
                               && !string.IsNullOrWhiteSpace(path)
                               && File.Exists(path))
                .ToList();

            var candidateIds = gamesWithAvailableVod.Select(static game => game.GameId).ToArray();
            var practicedIds = await _objectivesRepo.GetGamesWithPracticedObjectivesAsync(candidateIds);
            var taggedBookmarkIds = await _vodRepo.GetGamesWithObjectiveTaggedBookmarksAsync(candidateIds);

            foreach (var game in gamesWithAvailableVod)
            {
                if (!practicedIds.Contains(game.GameId)) continue;
                if (taggedBookmarkIds.Contains(game.GameId)) continue;

                return new VodPendingDto(
                    Show: true,
                    GameId: game.GameId,
                    Text: $"{game.ChampionName} has VOD but no objective-tagged evidence yet.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Dashboard: VOD evidence pending scan failed");
        }
        return new VodPendingDto(Show: false, GameId: 0, Text: "");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Greeting + next-step copy
    // ─────────────────────────────────────────────────────────────────────────

    private string BuildGreeting(DateTime now)
    {
        var hour = now.Hour;
        var tod = hour < 12 ? "morning" : hour < 17 ? "afternoon" : "evening";

        var id = _configService.RiotId ?? "";
        var hashPos = id.IndexOf('#');
        var gameName = hashPos > 0 ? id.Substring(0, hashPos) : "";

        if (_configService.HasValidRiotSession && !string.IsNullOrEmpty(gameName))
        {
            return $"Good {tod}, {gameName}.";
        }
        if (_configService.HasValidRiotSession)
        {
            return $"Good {tod}. Link your Riot account.";
        }
        return $"Good {tod}. Lock in.";
    }

    /// <summary>
    /// Static Start Block next-step copy. The full WinUI stage machine
    /// (NoGames/HasUnreviewed/NeedsObjective/Normal) drives richer copy in the
    /// app; for v1 the sidecar ships the canonical Start Block card the Tauri
    /// frontend already renders (the per-stage variants need IAnalysisService
    /// and overall-stats wiring that's out of scope for the read-only snapshot).
    /// TODO: port ComputeStageAsync once the stage queries are exposed in Core.
    /// </summary>
    private static NextStepDto BuildNextStep() => new(
        Kicker: "Next step · Start Block",
        Title: "Set your intent before you queue.",
        Detail: "A 30-second ritual: name one focus, check your priority objective, lock in.",
        CtaLabel: "START BLOCK →",
        Action: "start_block");

    // ─────────────────────────────────────────────────────────────────────────
    // Game-row mapping (mirror DashboardViewModel.MapGameDisplay)
    // ─────────────────────────────────────────────────────────────────────────

    private GameDisplayItemDto MapGameDisplay(GameStats game)
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

        // Vision score dropped from the at-a-glance line — it's noise in the
        // unreviewed queue (kept on the DTO as VisionScore for detail views).
        var statsLine =
            $"CS {game.CsTotal} ({game.CsPerMin:F1}/m)  ·  {FormatNumber(game.TotalDamageToChampions)} dmg";

        var metaLine = BuildMetaLine(game.DisplayGameMode, date, duration);

        return new GameDisplayItemDto(
            GameId: game.GameId,
            ChampionName: game.ChampionName,
            EnemyChampion: game.EnemyLaner,
            GameRole: gameRole,
            Win: game.Win,
            WinLossText: game.Win ? "W" : "L",
            Kills: game.Kills,
            Deaths: game.Deaths,
            Assists: game.Assists,
            KdaRatio: game.KdaRatio,
            KdaText: $"{game.Kills}/{game.Deaths}/{game.Assists}",
            KdaRatioText: $"({game.KdaRatio:F1})",
            CsTotal: game.CsTotal,
            CsPerMin: game.CsPerMin,
            VisionScore: game.VisionScore,
            TotalDamageToChampions: game.TotalDamageToChampions,
            Duration: duration,
            DatePlayed: date,
            GameMode: game.DisplayGameMode,
            WinLossColorHex: game.Win ? WinHex : LossHex,
            BorderColorHex: game.Win ? WinHex : LossHex,
            HasReview: HasPersistedReview(game),
            DamageText: FormatNumber(game.TotalDamageToChampions),
            StatsLine: statsLine,
            MetaLine: metaLine);
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

    // ─────────────────────────────────────────────────────────────────────────
    // Objective level palette (TODO: extract to Revu.Core, share with WinUI)
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

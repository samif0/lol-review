#nullable enable

using Microsoft.Extensions.Logging;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>
/// The four list views the Games workspace can render. Mirrors
/// <c>Revu.App.ViewModels.GamesWorkspaceView</c> (Queue / Today / History / Vod).
/// Parsed from the <c>?view=</c> query param; unknown/missing → Queue (index 0),
/// matching the WinUI page's default <c>SelectedViewIndex = 0</c>.
/// </summary>
public enum GamesView
{
    Queue = 0,
    Today = 1,
    History = 2,
    Vod = 3,
}

/// <summary>
/// Builds the read-only games-workspace snapshot served at GET /api/games.
///
/// <para>
/// Reproduces <c>GamesViewModel</c>'s four list views (Queue / Today / History /
/// VOD) + row enrichment, minus all WinUI/dispatcher concerns, and emits the
/// camelCase JSON contract (see <see cref="GamesDto"/> and
/// desktop/ui/sample-games.json). It deliberately does NOT reference the WinUI
/// ViewModel — only the Core repo + query interfaces already registered for the
/// dashboard.
/// </para>
///
/// <para>
/// VIEW SOURCES (mirror <c>GamesViewModel.LoadSelectedViewAsync</c>):
///   • Queue   → <see cref="IGameHistoryQuery.GetUnreviewedGamesAsync"/>(days:14)
///   • Today   → <see cref="IGameHistoryQuery.GetTodaysGamesAsync"/>()
///   • History → <see cref="IGameHistoryQuery.GetRecentAsync"/>(limit:30,
///               offset:page*30) + <see cref="IGameHistoryQuery.GetRecentCountAsync"/>
///   • Vod     → <see cref="IGameHistoryQuery.GetRecentAsync"/>(limit:120) filtered
///               to games whose linked VOD exists on disk.
/// Rows are then enriched (mirroring <c>GamesViewModel.EnrichRowsAsync</c>) with:
///   • <see cref="IVodRepository.GetVodPathsAsync"/> → File.Exists → hasVod
///   • <see cref="IObjectivesRepository.GetGamesWithPracticedObjectivesAsync"/>
///   • <see cref="IVodRepository.GetGamesWithObjectiveTaggedBookmarksAsync"/>
/// </para>
///
/// <para>
/// PAGINATION: History supports server paging via <c>?page=N</c> (offset
/// page*30). hasMore = (offset + returned) &lt; totalCount. Queue/Today/VOD are
/// single-shot (hasMore forced false), matching <c>ApplyViewCopy</c>.
/// </para>
///
/// <para>
/// READ-ONLY: only repository read methods are called — never writes,
/// migrations, deletes, or skips. The row "actions" (open review, watch VOD,
/// skip, delete) are presentation tokens the frontend maps to its own
/// navigation/write handlers (skip/delete go through WriteServices endpoints).
/// </para>
///
/// <para>
/// COLOR PARITY: like <see cref="DashboardSnapshotBuilder"/>, the glass-aurora
/// mockup palette is hardcoded here (win #8ee7ba, loss #f3a3a8, accent #9d8bff,
/// gold #f3c794). TODO: lift these constants into Revu.Core so the app and the
/// sidecar share one source of truth.
/// </para>
/// </summary>
public sealed class GamesSnapshotBuilder
{
    // ── Tunables (mirror GamesViewModel) ────────────────────────────────────
    private const int HistoryPageSize = 30;
    // VOD view scans only the most recent N games for on-disk recordings
    // (mirrors GamesViewModel.LoadVodGamesAsync's GetRecentAsync limit:120).
    private const int VodScanLimit = 120;
    // Queue window: unreviewed games from the last 14 days (mirrors the VM's
    // GetUnreviewedGamesAsync(days:14) call).
    private const int QueueDays = 14;

    // ── Mockup palette (TODO: extract to Revu.Core; mirrors DashboardSnapshotBuilder) ─
    private const string AccentHex = "#9d8bff";
    private const string GoldHex = "#f3c794";
    private const string WinHex = "#8ee7ba";
    private const string LossHex = "#f3a3a8";

    private readonly IGameHistoryQuery _gameHistory;
    private readonly IVodRepository _vodRepo;
    private readonly IObjectivesRepository _objectivesRepo;
    private readonly IConfigService _configService;
    private readonly ILogger<GamesSnapshotBuilder> _logger;

    public GamesSnapshotBuilder(
        IGameHistoryQuery gameHistory,
        IVodRepository vodRepo,
        IObjectivesRepository objectivesRepo,
        IConfigService configService,
        ILogger<GamesSnapshotBuilder> logger)
    {
        _gameHistory = gameHistory;
        _vodRepo = vodRepo;
        _objectivesRepo = objectivesRepo;
        _configService = configService;
        _logger = logger;
    }

    /// <summary>
    /// Build the snapshot for one of the four workspace views. Mirrors
    /// <c>GamesViewModel.LoadSelectedViewAsync</c> + <c>ApplyViewCopy</c>:
    /// each view has its own data source, heading, empty-message, and paging
    /// behavior (only History paginates). <paramref name="view"/> is parsed via
    /// <see cref="ParseView"/> (unknown → Queue); <paramref name="page"/> is the
    /// zero-based History page (clamped ≥ 0; ignored by the other views).
    /// </summary>
    public async Task<GamesDto> BuildAsync(string? view = null, int page = 0, CancellationToken ct = default)
    {
        var now = DateTime.Now;
        var resolvedView = ParseView(view);
        var safePage = Math.Max(page, 0);

        // ── Load the view's games + (History only) total count ──────────────
        // Mirrors the GamesWorkspaceView switch in LoadSelectedViewAsync.
        var games = new List<GameStats>();
        var totalCount = 0;       // History only; the other views report returned count.
        var hasMore = false;      // ApplyViewCopy forces this false off-History.
        var effectivePage = resolvedView == GamesView.History ? safePage : 0;

        try
        {
            switch (resolvedView)
            {
                case GamesView.Queue:
                    games = await _gameHistory.GetUnreviewedGamesAsync(days: QueueDays);
                    totalCount = games.Count;
                    break;

                case GamesView.Today:
                    games = await _gameHistory.GetTodaysGamesAsync();
                    totalCount = games.Count;
                    break;

                case GamesView.Vod:
                    games = await LoadVodGamesAsync();
                    totalCount = games.Count;
                    break;

                case GamesView.History:
                default:
                    var offset = effectivePage * HistoryPageSize;
                    games = await _gameHistory.GetRecentAsync(
                        limit: HistoryPageSize, offset: offset, champion: null, win: null);
                    totalCount = await _gameHistory.GetRecentCountAsync(champion: null, win: null);
                    // hasMore: are there rows beyond what we've loaded so far?
                    // (mirrors GamesViewModel.LoadHistoryPageAsync's
                    // HasMoreHistory = offset + page.Count < totalCount).
                    hasMore = offset + games.Count < totalCount;
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Games: {View} view load (page {Page}) failed", resolvedView, effectivePage);
        }

        // ── Map to display rows ─────────────────────────────────────────────
        var items = games.Select(MapGameDisplay).ToList();

        // ── Enrich rows with VOD availability + objective state (ALL views) ─
        await EnrichRowsAsync(items);

        var returned = items.Count;
        var (heading, emptyMessage) = ViewCopy(resolvedView);
        var countText = totalCount == 1 ? "1 game" : $"{totalCount} games";

        return new GamesDto(
            GeneratedAt: now.ToString("yyyy-MM-ddTHH:mm:ss"),
            View: ViewKey(resolvedView),
            Heading: heading,
            Page: effectivePage,
            PageSize: HistoryPageSize,
            ReturnedCount: returned,
            TotalCount: totalCount,
            CountText: countText,
            HasMore: hasMore,
            IsEmpty: returned == 0,
            EmptyMessage: emptyMessage,
            Items: items);
    }

    /// <summary>
    /// VOD view source: scan the most recent <see cref="VodScanLimit"/> games and
    /// keep only those with a linked recording that exists on disk. Mirrors
    /// <c>GamesViewModel.LoadVodGamesAsync</c> (the App uses FileProbeCache; the
    /// sidecar has no WinUI cache so it probes via File.Exists directly, same as
    /// EnrichRowsAsync).
    /// </summary>
    private async Task<List<GameStats>> LoadVodGamesAsync()
    {
        var recent = await _gameHistory.GetRecentAsync(limit: VodScanLimit, offset: 0, champion: null, win: null);
        if (recent.Count == 0) return recent;

        var vodPaths = await _vodRepo.GetVodPathsAsync(recent.Select(g => g.GameId).ToArray());
        return recent
            .Where(g => vodPaths.TryGetValue(g.GameId, out var path)
                        && !string.IsNullOrWhiteSpace(path)
                        && File.Exists(path))
            .ToList();
    }

    // ── View parsing + per-view copy (mirror GamesViewModel.ApplyViewCopy) ──

    /// <summary>
    /// Parse the <c>?view=</c> query param into a <see cref="GamesView"/>.
    /// Accepts the named keys (queue/today/history/vod) case-insensitively;
    /// anything else (null, empty, garbage) defaults to Queue — the WinUI page's
    /// initial SelectedViewIndex.
    /// </summary>
    private static GamesView ParseView(string? view) => (view ?? "").Trim().ToLowerInvariant() switch
    {
        "today" => GamesView.Today,
        "history" => GamesView.History,
        "vod" => GamesView.Vod,
        _ => GamesView.Queue,
    };

    /// <summary>Stable lowercase view key echoed back in the DTO (queue/today/history/vod).</summary>
    private static string ViewKey(GamesView view) => view switch
    {
        GamesView.Today => "today",
        GamesView.History => "history",
        GamesView.Vod => "vod",
        _ => "queue",
    };

    /// <summary>Heading + empty-message per view (mirror GamesViewModel.ApplyViewCopy).</summary>
    private static (string Heading, string EmptyMessage) ViewCopy(GamesView view) => view switch
    {
        GamesView.Queue => ("Review Queue", "No games need review."),
        GamesView.Today => ("Today", "No games logged today."),
        GamesView.History => ("History", "No games recorded yet."),
        GamesView.Vod => ("VOD Review", "No VOD recordings are linked yet."),
        _ => ("Games", "No games found."),
    };

    // ─────────────────────────────────────────────────────────────────────────
    // Row enrichment (mirror GamesViewModel.EnrichRowsAsync)
    // ─────────────────────────────────────────────────────────────────────────

    private async Task EnrichRowsAsync(List<GamesRowDto> items)
    {
        if (items.Count == 0) return;

        try
        {
            var gameIds = items.Select(g => g.GameId).ToArray();
            var vodPaths = await _vodRepo.GetVodPathsAsync(gameIds);
            var practicedIds = await _objectivesRepo.GetGamesWithPracticedObjectivesAsync(gameIds);
            var taggedBookmarkIds = await _vodRepo.GetGamesWithObjectiveTaggedBookmarksAsync(gameIds);

            for (var i = 0; i < items.Count; i++)
            {
                var row = items[i];

                var hasVod = vodPaths.TryGetValue(row.GameId, out var path)
                             && !string.IsNullOrWhiteSpace(path)
                             && File.Exists(path);
                var practiced = practicedIds.Contains(row.GameId);
                var hasObjectiveEvidence = taggedBookmarkIds.Contains(row.GameId);

                // Mirror GamesViewModel.EnrichRowsAsync objective-state ladder.
                var objectiveStateText = hasObjectiveEvidence
                    ? "Evidence tagged"
                    : practiced && hasVod && row.HasReview
                        ? "VOD evidence pending"
                        : practiced
                            ? "Objective practiced"
                            : "No objective tag";

                // v2.17.8: VOD wins. "Watch VOD" when a recording exists,
                // else "Open" (reviewed) / "Review" (unreviewed).
                var primaryActionText = hasVod
                    ? "Watch VOD"
                    : row.HasReview
                        ? "Open"
                        : "Review";

                // The row-body click is the always-Review escape hatch
                // (data-action open_review); the inline button is the fast
                // path. VOD/skip/delete actions are DEFERRED stubs on the
                // frontend.
                items[i] = row with
                {
                    HasVod = hasVod,
                    ObjectivePracticed = practiced,
                    HasObjectiveEvidence = hasObjectiveEvidence,
                    ObjectiveStateText = objectiveStateText,
                    ReviewStateText = row.HasReview ? "Reviewed" : "Unreviewed",
                    VodStateText = hasVod ? "VOD linked" : "No VOD",
                    PrimaryAction = primaryActionText,
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Games: row enrichment (VOD/objective) failed");
            // Degrade: rows keep their default (unenriched) state — hasVod
            // false, "No objective tag" / "Unreviewed" labels — rather than
            // blanking the whole list.
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Game-row mapping (mirror GamesViewModel.MapGameDisplay / DashboardSnapshotBuilder)
    // ─────────────────────────────────────────────────────────────────────────

    private GamesRowDto MapGameDisplay(GameStats game)
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

        // Vision score is OUT of the at-a-glance stats line (it's noise in the
        // history list) — kept on the DTO as VisionScore for detail views.
        var statsLine =
            $"CS {game.CsTotal} ({game.CsPerMin:F1}/m)  ·  {FormatNumber(game.TotalDamageToChampions)} dmg";

        var metaLine = BuildMetaLine(game.DisplayGameMode, date, duration);

        return new GamesRowDto(
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
            MetaLine: metaLine,
            // Enrichment defaults — overwritten by EnrichRowsAsync. Defaults
            // keep the row coherent if enrichment degrades.
            HasVod: false,
            ObjectivePracticed: false,
            HasObjectiveEvidence: false,
            ObjectiveStateText: "No objective tag",
            ReviewStateText: HasPersistedReview(game) ? "Reviewed" : "Unreviewed",
            VodStateText: "No VOD",
            PrimaryAction: HasPersistedReview(game) ? "Open" : "Review",
            // Stable token the Tauri shell maps to navigation. Row-body click =
            // always open the Review page (mirrors GamesViewModel.OpenReview).
            Action: "open_review");
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

    /// <summary>Mirror of GamesViewModel.HasPersistedReview / DashboardSnapshotBuilder.HasPersistedReview.</summary>
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
}

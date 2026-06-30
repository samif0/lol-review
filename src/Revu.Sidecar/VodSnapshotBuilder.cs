#nullable enable

using Microsoft.Extensions.Logging;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>
/// Builds the read-only VOD snapshot served at GET /api/vod?gameId=N: the matched
/// recording's file path, the game's timeline bookmarks, the live event timeline
/// (kills/deaths/objectives), and the evidence inbox (auto moments + saved clips)
/// for the 'Moments to Review' 3-way filter. READ-ONLY — playback and bookmark/
/// evidence CRUD are the frontend's concern / deferred writes.
/// </summary>
public sealed class VodSnapshotBuilder
{
    // Win/loss/gold hexes mirror the other builders + the WinUI TimelineEvent
    // palette (positive / negative / gold). Used for event-marker bucketing.
    private const string WinHex = "#8ee7ba";
    private const string LossHex = "#f3a3a8";
    private const string GoldHex = "#f3c794";
    private const string NeutralHex = "#9fb0c3";
    // Summoner-spell casts (Flash + the rest) get their own readable cyan so the
    // user can spot summoner usage at a glance on the timeline.
    private const string SummonerHex = "#7fd4ff";
    // Recall (derived from shop purchases) — a soft periwinkle, distinct from the
    // cyan summoner hue. Matches GameEvent.TrackableTokens "Recall" catalog color.
    private const string RecallHex = "#a9c8ff";
    // Trade (derived from your HP dropping while alive) — a warm amber, distinct from
    // both recall periwinkle and loss red. Matches the "Trade" catalog color.
    private const string TradeHex = "#ffb86b";
    // Jungle-ganked death — a deeper, more saturated red than a plain death so a gank
    // stands out from a normal loss marker. Matches the JUNGLE_GANK catalog color.
    private const string JungleGankHex = "#d6455e";
    // Teamfight pin — matches the TEAMFIGHT catalog color (the soft red the band uses).
    private const string TeamfightHex = "#f3a3a8";

    private readonly IGameRepository _gameRepo;
    private readonly IVodRepository _vodRepo;
    private readonly IGameEventsRepository _eventsRepo;
    private readonly IEvidenceRepository _evidenceRepo;
    private readonly IObjectivesRepository _objectivesRepo;
    private readonly ILogger<VodSnapshotBuilder> _logger;

    public VodSnapshotBuilder(
        IGameRepository gameRepo,
        IVodRepository vodRepo,
        IGameEventsRepository eventsRepo,
        IEvidenceRepository evidenceRepo,
        IObjectivesRepository objectivesRepo,
        ILogger<VodSnapshotBuilder> logger)
    {
        _gameRepo = gameRepo;
        _vodRepo = vodRepo;
        _eventsRepo = eventsRepo;
        _evidenceRepo = evidenceRepo;
        _objectivesRepo = objectivesRepo;
        _logger = logger;
    }

    public async Task<VodDto> BuildAsync(long gameId, CancellationToken ct = default)
    {
        var generatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

        GameStats? game = null;
        try { game = await _gameRepo.GetAsync(gameId); }
        catch (Exception ex) { _logger.LogDebug(ex, "VOD: game {GameId} load failed", gameId); }

        if (game is null)
        {
            return Empty(generatedAt, gameId);
        }

        // Resolve the VOD file path (file must exist on disk to be playable).
        string filePath = "";
        try
        {
            var paths = await _vodRepo.GetVodPathsAsync(new[] { gameId });
            if (paths.TryGetValue(gameId, out var p) && !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            {
                filePath = p;
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "VOD: path lookup failed for {GameId}", gameId); }

        // Bookmarks → timeline markers. Also build a bookmarkId → ShareUrl lookup so
        // the saved-clip evidence rows (whose SourceId IS the bookmark id) can carry
        // the share state for the VOD player's Share button.
        var bookmarks = new List<VodBookmarkDto>();
        var shareUrlByBookmarkId = new Dictionary<long, string>();
        try
        {
            var raw = await _vodRepo.GetBookmarksAsync(gameId);
            foreach (var b in raw)
            {
                if (!string.IsNullOrWhiteSpace(b.ShareUrl)) shareUrlByBookmarkId[b.Id] = b.ShareUrl;
            }
            bookmarks.AddRange(raw
                .OrderBy(b => b.GameTimeSeconds)
                .Select(b => new VodBookmarkDto(
                    Id: b.Id,
                    GameTimeSeconds: b.GameTimeSeconds,
                    TimeLabel: FormatClock(b.GameTimeSeconds),
                    Note: b.Note ?? "",
                    TagsJson: b.TagsJson ?? "",
                    HasClip: !string.IsNullOrWhiteSpace(b.ClipPath) || b.ClipStartSeconds.HasValue,
                    ClipStartSeconds: b.ClipStartSeconds,
                    ClipEndSeconds: b.ClipEndSeconds,
                    ObjectiveId: b.ObjectiveId,
                    ShareUrl: b.ShareUrl ?? "")));
        }
        catch (Exception ex) { _logger.LogDebug(ex, "VOD: bookmarks load failed for {GameId}", gameId); }

        // Active-objective event-token ties → which events light up the priority lane.
        // Shared resolver (Revu.Core) so the timeline + the auto-clipper agree exactly.
        ObjectiveEventTieResolver tieResolver;
        try { tieResolver = ObjectiveEventTieResolver.FromTies(await _objectivesRepo.GetActiveObjectiveEventTokensAsync()); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "VOD: objective token map load failed");
            tieResolver = ObjectiveEventTieResolver.FromTies(Array.Empty<(string, long, string)>());
        }

        // Live event timeline (kills/deaths/objectives) → colored markers. Each event
        // is tagged with its tied objective (if any) so the timeline can prioritize it.
        var gameEvents = new List<VodEventDto>();
        try
        {
            var raw = (await _eventsRepo.GetEventsAsync(gameId))
                .OrderBy(e => e.GameTimeS)
                .ToList();

            // A combat marker lights up (priority lane) ONLY for objectives that track the
            // event's OWN token — NOT for objectives that merely track TEAMFIGHT and happen
            // to contain it. Otherwise a teamfight-only objective lit up every KIL/DTH/AST
            // inside each fight (un-actionable individually, and over-clipped). So combat
            // markers use TokenTiesForEvent (direct token ties only).
            foreach (var e in raw)
            {
                gameEvents.Add(MapEvent(e, tieResolver.TokenTiesForEvent(e)));
            }

            // …and the teamfight itself becomes ONE synthetic TEAMFIGHT event per cluster,
            // tagged with the objective(s) that track TEAMFIGHT. This is what a teamfight-
            // tracking objective counts/steps/clips as its events (one per fight), instead
            // of either every combat member (the over-count/over-clip bug) or nothing (the
            // "TF isn't marked as an event" bug). Anchored at the cluster start so the loud
            // TF pin sits at the band's left edge. Negative ids so they never collide with
            // real DB event ids (used as marker keys / jump anchors).
            var synthId = -1L;
            foreach (var c in tieResolver.ResolveTeamfightClusters(raw))
            {
                gameEvents.Add(MapTeamfightCluster(c, synthId));
                synthId--;
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "VOD: game events load failed for {GameId}", gameId); }

        // Evidence inbox → split into AUTO moments (timeline_region) vs SAVED
        // CLIPS (clip). Mirrors VodPlayerViewModel.RefreshEvidenceInboxAsync's
        // AutoReviewMoments / SavedClipReviewMoments split. Bookmarks render from
        // the Bookmarks list above (the 3rd lane of the 3-way filter).
        var autoMoments = new List<VodEvidenceDto>();
        var savedClips = new List<VodEvidenceDto>();
        try
        {
            var raw = await _evidenceRepo.GetForGameAsync(gameId, includeDismissed: false);
            foreach (var item in raw.OrderBy(i => i.StartTimeSeconds ?? int.MaxValue))
            {
                var dto = MapEvidence(item);
                if (string.Equals(item.SourceKind, EvidenceKinds.Clip, StringComparison.OrdinalIgnoreCase))
                {
                    // A clip evidence row's SourceId is the bookmark id the Share
                    // button targets; carry its share state from the lookup above.
                    var shareBmId = item.SourceId ?? 0;
                    var shareUrl = shareBmId > 0 && shareUrlByBookmarkId.TryGetValue(shareBmId, out var u) ? u : "";
                    savedClips.Add(dto with { ShareBookmarkId = shareBmId, ShareUrl = shareUrl });
                }
                else
                    autoMoments.Add(dto);
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "VOD: evidence inbox load failed for {GameId}", gameId); }

        var date = game.Timestamp > 0
            ? DateTimeOffset.FromUnixTimeSeconds(game.Timestamp).LocalDateTime.ToString("MMM dd, HH:mm")
            : "";

        return new VodDto(
            GeneratedAt: generatedAt,
            HasVod: filePath.Length > 0,
            GameId: gameId,
            FilePath: filePath,
            FileName: filePath.Length > 0 ? Path.GetFileName(filePath) : "",
            ChampionName: game.ChampionName ?? "",
            EnemyChampion: game.EnemyLaner ?? "",
            ResultText: game.Win ? "Victory" : "Defeat",
            ResultColorHex: game.Win ? WinHex : LossHex,
            GameMode: game.DisplayGameMode ?? "",
            DatePlayed: date,
            GameDurationSeconds: game.GameDuration > 0 ? game.GameDuration : 0,
            Bookmarks: bookmarks,
            GameEvents: gameEvents,
            AutoMoments: autoMoments,
            SavedClips: savedClips);
    }

    // ── event marker mapping (mirrors WinUI TimelineEvent) ────────────────────
    // The ties here are DIRECT TOKEN ties only (see the caller): a marker lights up for
    // objectives that track the event's own token, not for teamfight-cluster membership
    // (the fight is shown by its band). ObjectiveId/Title/Color keep the FIRST match
    // (priority-lane color). Shared resolver so the timeline can't drift from the clipper.
    private static VodEventDto MapEvent(GameEvent e, IReadOnlyList<ObjectiveTie> matches)
    {
        var (kind, colorHex) = BucketEvent(e.EventType);

        // A jungle-ganked DEATH (Details.jungle_gank) reads distinctly on the timeline —
        // a "GNK" marker in a deeper red — while staying in the loss family (it IS a
        // death, just attributed). Type stays DEATH so consumers that switch on type are
        // unaffected; only the human-facing labels + hue change.
        var isJungleGank = IsDeathEvent(e.EventType) && ReadJsonBool(e, "jungle_gank");
        if (isJungleGank) colorHex = JungleGankHex;

        long? objId = matches.Count > 0 ? matches[0].ObjectiveId : null;
        var objTitle = matches.Count > 0 ? matches[0].Title : "";
        var objColor = matches.Count > 0 ? matches[0].Color : "";
        var objIds = matches.Count > 0 ? matches.Select(m => m.ObjectiveId).ToList() : null;

        return new VodEventDto(
            Id: e.Id,
            EventType: e.EventType ?? "",
            GameTimeSeconds: e.GameTimeS,
            TimeLabel: FormatClock(e.GameTimeS),
            ShortLabel: isJungleGank ? "GNK" : ShortLabel(e.EventType),
            Label: isJungleGank ? "Jungle Gank" : EventLabel(e.EventType),
            Summary: isJungleGank ? JungleGankSummary(e) : EventSummary(e),
            Kind: kind,
            ColorHex: colorHex,
            ObjectiveId: objId,
            ObjectiveTitle: objTitle,
            ObjectiveColorHex: objColor,
            ObjectiveIds: objIds);
    }

    // A teamfight cluster → one synthetic TEAMFIGHT timeline event (the loud "TF" pin),
    // tagged with the objective(s) that track teamfights. Anchored at the fight's start;
    // the Summary carries the span + member count for the hover.
    private static VodEventDto MapTeamfightCluster(TeamfightCluster c, long syntheticId)
    {
        var first = c.Objectives.Count > 0 ? c.Objectives[0] : default;
        var objIds = c.Objectives.Count > 0 ? c.Objectives.Select(o => o.ObjectiveId).ToList() : null;
        var memberCount = c.MemberEventIds.Count;
        return new VodEventDto(
            Id: syntheticId,
            EventType: GameEvent.TrackableTokens.TeamfightToken, // "TEAMFIGHT"
            GameTimeSeconds: c.StartS,
            TimeLabel: FormatClock(c.StartS),
            ShortLabel: "TF",
            Label: "Teamfight",
            Summary: $"{FormatClock(c.StartS)}–{FormatClock(c.EndS)} · {memberCount} events",
            Kind: "teamfight",
            ColorHex: TeamfightHex,
            ObjectiveId: c.Objectives.Count > 0 ? first.ObjectiveId : null,
            ObjectiveTitle: c.Objectives.Count > 0 ? first.Title : "",
            ObjectiveColorHex: c.Objectives.Count > 0 ? first.Color : "",
            ObjectiveIds: objIds);
    }

    private static bool IsDeathEvent(string? eventType) =>
        string.Equals(eventType, "DEATH", StringComparison.OrdinalIgnoreCase);

    private static bool ReadJsonBool(GameEvent e, string property)
    {
        if (string.IsNullOrWhiteSpace(e.Details) || e.Details == "{}") return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(e.Details);
            return doc.RootElement.TryGetProperty(property, out var v)
                && v.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch { return false; }
    }

    // "jungle gank — killed by Khazix" / "jungle gank" when no killer name.
    private static string JungleGankSummary(GameEvent e)
    {
        if (string.IsNullOrWhiteSpace(e.Details) || e.Details == "{}") return "jungle gank";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(e.Details);
            var killer = ReadJsonString(doc.RootElement, "killer");
            return killer.Length > 0 ? $"jungle gank — {killer}" : "jungle gank";
        }
        catch { return "jungle gank"; }
    }

    // Bucket event types into the win/loss/gold/neutral marker language, with the
    // exact per-type hue. Combat-positive → win; deaths → loss; objectives → gold.
    private static (string Kind, string ColorHex) BucketEvent(string? eventType) =>
        (eventType ?? "").ToUpperInvariant() switch
        {
            "KILL" or "ASSIST" or "MULTI_KILL" => ("win", WinHex),
            "DEATH" or "FIRST_BLOOD" => ("loss", LossHex),
            "DRAGON" or "BARON" or "HERALD" or "TURRET" or "INHIBITOR" => ("gold", GoldHex),
            "FLASH" or "SUMMONER_SPELL" => ("summoner", SummonerHex),
            "RECALL" => ("recall", RecallHex),
            "TRADE" => ("trade", TradeHex),
            _ => ("neutral", NeutralHex),
        };

    private static string ShortLabel(string? eventType) => (eventType ?? "").ToUpperInvariant() switch
    {
        "KILL" => "KIL",
        "DEATH" => "DTH",
        "ASSIST" => "AST",
        "DRAGON" => "DRG",
        "BARON" => "BAR",
        "HERALD" => "HRD",
        "TURRET" => "TWR",
        "INHIBITOR" => "INH",
        "FIRST_BLOOD" => "FB",
        "MULTI_KILL" => "MLT",
        "LEVEL_UP" => "LVL",
        "FLASH" => "FLS",
        "SUMMONER_SPELL" => "SUM",
        "RECALL" => "RCL",
        "TRADE" => "TRD",
        _ => "EVT",
    };

    private static string EventLabel(string? eventType) => (eventType ?? "").ToUpperInvariant() switch
    {
        "KILL" => "Kill",
        "DEATH" => "Death",
        "ASSIST" => "Assist",
        "DRAGON" => "Dragon",
        "BARON" => "Baron",
        "HERALD" => "Herald",
        "TURRET" => "Turret",
        "INHIBITOR" => "Inhibitor",
        "FIRST_BLOOD" => "First Blood",
        "MULTI_KILL" => "Multi Kill",
        "LEVEL_UP" => "Level Up",
        "FLASH" => "Flash",
        "SUMMONER_SPELL" => "Summoner Spell",
        "RECALL" => "Recall",
        "TRADE" => "Trade",
        _ => eventType ?? "",
    };

    // Parse a short actor/target out of the Details JSON (mirrors WinUI
    // TimelineEvent.FormatSummary). Best-effort; returns "" on any failure.
    private static string EventSummary(GameEvent e)
    {
        if (string.IsNullOrWhiteSpace(e.Details) || e.Details == "{}") return "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(e.Details);
            var root = doc.RootElement;
            return (e.EventType ?? "").ToUpperInvariant() switch
            {
                "KILL" => ReadJsonString(root, "victim"),
                "DEATH" => ReadJsonString(root, "killer"),
                "ASSIST" => ReadJsonString(root, "victim"),
                "DRAGON" => ReadJsonString(root, "dragon_type"),
                "BARON" or "HERALD" or "TURRET" or "INHIBITOR" => ReadJsonString(root, "killer"),
                "MULTI_KILL" => ReadJsonString(root, "label"),
                "FLASH" or "SUMMONER_SPELL" => ReadJsonString(root, "spell"),
                "RECALL" => root.TryGetProperty("gold_spent", out var g) && g.TryGetInt32(out var gs) && gs > 0
                    ? $"spent {gs}g"
                    : "detected",
                "TRADE" => TradeSummary(root),
                _ => "",
            };
        }
        catch { return ""; }
    }

    private static string ReadJsonString(System.Text.Json.JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value)) return "";
        return value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() ?? "" : "";
    }

    // "short trade -34% hp" / "extended trade" — best-effort from the derived Details.
    private static string TradeSummary(System.Text.Json.JsonElement root)
    {
        var kind = ReadJsonString(root, "kind");
        var label = kind.Length > 0 ? $"{kind} trade" : "trade";
        if (root.TryGetProperty("hp_lost_pct", out var p) && p.TryGetInt32(out var pct) && pct > 0)
            return $"{label} -{pct}% hp";
        return label;
    }

    // ── evidence inbox mapping ────────────────────────────────────────────────
    private static VodEvidenceDto MapEvidence(EvidenceItemRecord item) => new(
        Id: item.Id,
        SourceKind: item.SourceKind ?? "",
        SourceId: item.SourceId,
        StartTimeSeconds: item.StartTimeSeconds,
        EndTimeSeconds: item.EndTimeSeconds,
        TimeLabel: item.StartTimeSeconds.HasValue ? FormatClock(item.StartTimeSeconds.Value) : "",
        Title: item.Title ?? "",
        Note: item.Note ?? "",
        ObjectiveId: item.ObjectiveId,
        ObjectiveTitle: item.ObjectiveTitle ?? "",
        Polarity: item.Polarity ?? EvidencePolarities.Neutral,
        PolarityColorHex: PolarityHex(item.Polarity),
        Status: item.Status ?? EvidenceStatuses.NeedsReview,
        HasClip: string.Equals(item.SourceKind, EvidenceKinds.Clip, StringComparison.OrdinalIgnoreCase));

    private static string PolarityHex(string? polarity) => (polarity ?? "").Trim().ToLowerInvariant() switch
    {
        EvidencePolarities.Good => WinHex,
        EvidencePolarities.Bad => LossHex,
        _ => NeutralHex,
    };

    private static VodDto Empty(string generatedAt, long gameId) => new(
        GeneratedAt: generatedAt, HasVod: false, GameId: gameId, FilePath: "", FileName: "",
        ChampionName: "", EnemyChampion: "", ResultText: "", ResultColorHex: "",
        GameMode: "", DatePlayed: "", GameDurationSeconds: 0,
        Bookmarks: Array.Empty<VodBookmarkDto>(),
        GameEvents: Array.Empty<VodEventDto>(),
        AutoMoments: Array.Empty<VodEvidenceDto>(),
        SavedClips: Array.Empty<VodEvidenceDto>());

    private static string FormatClock(int seconds)
    {
        if (seconds < 0) seconds = 0;
        return $"{seconds / 60}:{seconds % 60:D2}";
    }
}

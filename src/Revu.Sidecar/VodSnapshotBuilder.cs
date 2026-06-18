#nullable enable

using Microsoft.Extensions.Logging;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;

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
        // token (UPPER) → (objectiveId, title, color). Built once per snapshot.
        var objectiveTokenMap = await BuildObjectiveTokenMapAsync();

        // Live event timeline (kills/deaths/objectives) → colored markers. Each event
        // is tagged with its tied objective (if any) so the timeline can prioritize it.
        var gameEvents = new List<VodEventDto>();
        try
        {
            var raw = (await _eventsRepo.GetEventsAsync(gameId))
                .OrderBy(e => e.GameTimeS)
                .ToList();

            // Pre-compute which events belong to an objective-tracked TEAMFIGHT, so a
            // member event can be tied even though no single row IS a teamfight.
            var teamfightTie = ResolveTeamfightTies(raw, objectiveTokenMap);

            foreach (var e in raw)
            {
                gameEvents.Add(MapEvent(e, objectiveTokenMap, teamfightTie));
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
    private static VodEventDto MapEvent(
        GameEvent e,
        IReadOnlyDictionary<string, (long Id, string Title, string Color)> objectiveTokenMap,
        IReadOnlyDictionary<long, (long Id, string Title, string Color)> teamfightTie)
    {
        var (kind, colorHex) = BucketEvent(e.EventType);

        // Objective tie: first the event's own token (raw type or SPELL_*), then a
        // teamfight membership (an event can belong to a tracked TEAMFIGHT cluster).
        long? objId = null; var objTitle = ""; var objColor = "";
        var token = EventToken(e);
        if (token is not null && objectiveTokenMap.TryGetValue(token, out var tie))
        {
            (objId, objTitle, objColor) = (tie.Id, tie.Title, tie.Color);
        }
        else if (teamfightTie.TryGetValue(e.Id, out var tf))
        {
            (objId, objTitle, objColor) = (tf.Id, tf.Title, tf.Color);
        }

        return new VodEventDto(
            Id: e.Id,
            EventType: e.EventType ?? "",
            GameTimeSeconds: e.GameTimeS,
            TimeLabel: FormatClock(e.GameTimeS),
            ShortLabel: ShortLabel(e.EventType),
            Label: EventLabel(e.EventType),
            Summary: EventSummary(e),
            Kind: kind,
            ColorHex: colorHex,
            ObjectiveId: objId,
            ObjectiveTitle: objTitle,
            ObjectiveColorHex: objColor);
    }

    // The trackable TOKEN for an event: per-spell (SPELL_FLASH/SPELL_SMITE/…) parsed
    // from Details.spell for summoner casts, otherwise the raw event type. Returns
    // null for types that have no token (none today, but defensive). UPPERCASE.
    private static string? EventToken(GameEvent e)
    {
        var type = (e.EventType ?? "").ToUpperInvariant();
        if (type is "FLASH" or "SUMMONER_SPELL")
        {
            var spell = ReadSpellName(e);
            if (!string.IsNullOrWhiteSpace(spell))
                return "SPELL_" + spell.Trim().ToUpperInvariant();
            // Legacy rows with no Details.spell: FLASH still maps to its spell token.
            return type == "FLASH" ? "SPELL_FLASH" : null;
        }
        return type.Length > 0 ? type : null;
    }

    // The specific summoner-spell name from Details.spell ("Flash"|"Ignite"|…), "" if absent.
    private static string ReadSpellName(GameEvent e)
    {
        if (string.IsNullOrWhiteSpace(e.Details) || e.Details == "{}") return "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(e.Details);
            return ReadJsonString(doc.RootElement, "spell");
        }
        catch { return ""; }
    }

    // Build token (UPPER) → tied active objective. When several objectives track the
    // same token, the first wins (deterministic by query order).
    private async Task<IReadOnlyDictionary<string, (long Id, string Title, string Color)>> BuildObjectiveTokenMapAsync()
    {
        var map = new Dictionary<string, (long, string, string)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var ties = await _objectivesRepo.GetActiveObjectiveEventTokensAsync();
            foreach (var (token, objId, title) in ties)
            {
                var key = (token ?? "").Trim().ToUpperInvariant();
                if (key.Length == 0 || map.ContainsKey(key)) continue;
                var color = Revu.Core.Models.GameEvent.TrackableTokens.ColorOf(key);
                map[key] = (objId, title ?? "", color);
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "VOD: objective token map load failed"); }
        return map;
    }

    // If any active objective tracks TEAMFIGHT, cluster the combat events (the same
    // definition the client timeline uses: ≥3 combat events within 14s) and tie every
    // member event to that objective. Returns eventId → tied objective. Empty when no
    // objective tracks teamfights. Mirrors vodplayer.js teamfightZones + the
    // "Teamfight" derived-event default (KILL/DEATH cluster).
    private static IReadOnlyDictionary<long, (long Id, string Title, string Color)> ResolveTeamfightTies(
        IReadOnlyList<GameEvent> events,
        IReadOnlyDictionary<string, (long Id, string Title, string Color)> objectiveTokenMap)
    {
        var ties = new Dictionary<long, (long, string, string)>();
        if (!objectiveTokenMap.TryGetValue(Revu.Core.Models.GameEvent.TrackableTokens.TeamfightToken, out var tf))
            return ties;

        const int GapSeconds = 14;
        const int MinEvents = 3;
        bool IsCombat(string? t) =>
            (t ?? "").ToUpperInvariant() is "KILL" or "DEATH" or "ASSIST" or "MULTI_KILL" or "FIRST_BLOOD";

        // GameTimeS > 0 mirrors the client teamfightZones() filter (vodplayer.js): a
        // t=0 combat event (unresolved EventTime) is dropped client-side, so dropping
        // it here too keeps the server ties aligned with the visible TF band.
        var combat = events.Where(e => IsCombat(e.EventType) && e.GameTimeS > 0)
            .OrderBy(e => e.GameTimeS).ToList();
        var cluster = new List<GameEvent>();
        void Flush()
        {
            if (cluster.Count >= MinEvents)
                foreach (var m in cluster) ties[m.Id] = (tf.Id, tf.Title, tf.Color);
            cluster = new List<GameEvent>();
        }
        foreach (var e in combat)
        {
            if (cluster.Count == 0 || e.GameTimeS - cluster[^1].GameTimeS <= GapSeconds)
                cluster.Add(e);
            else { Flush(); cluster.Add(e); }
        }
        Flush();
        return ties;
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

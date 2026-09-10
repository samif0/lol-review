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
    // Jungle proximity (map-state backfill) — threat pink for the enemy jungler, calm
    // teal for the ally, violet fallback. Match the Map-group catalog colors.
    private const string EnemyProximityHex = "#ff7a9e";
    private const string AllyProximityHex = "#6bd6c8";
    private const string ProximityHex = "#b07cd8";

    private readonly IGameRepository _gameRepo;
    private readonly IVodRepository _vodRepo;
    private readonly IGameEventsRepository _eventsRepo;
    private readonly IEvidenceRepository _evidenceRepo;
    private readonly IObjectivesRepository _objectivesRepo;
    private readonly IConfigService _config;
    private readonly ILogger<VodSnapshotBuilder> _logger;
    // v3.11: the corrections ledger. Optional (trailing, defaulted) so the seven-argument
    // call sites keep compiling; MS.DI supplies the read-graph registration.
    private readonly IEventCorrectionsRepository? _corrections;

    public VodSnapshotBuilder(
        IGameRepository gameRepo,
        IVodRepository vodRepo,
        IGameEventsRepository eventsRepo,
        IEvidenceRepository evidenceRepo,
        IObjectivesRepository objectivesRepo,
        IConfigService config,
        ILogger<VodSnapshotBuilder> logger,
        IEventCorrectionsRepository? corrections = null)
    {
        _gameRepo = gameRepo;
        _vodRepo = vodRepo;
        _eventsRepo = eventsRepo;
        _evidenceRepo = evidenceRepo;
        _objectivesRepo = objectivesRepo;
        _config = config;
        _logger = logger;
        _corrections = corrections;
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
                    PromptId: b.PromptId,
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

        // v3.11: this game's corrections ledger. The applicable rows (active, absorbed,
        // orphaned) decorate the markers they are attached to and rebuild the ghosts of
        // removed events; every non-revert row feeds the panel's list. Best-effort like
        // every other load here: a ledger failure still renders the timeline.
        var applicable = new List<EventCorrection>();
        var ledger = new List<EventCorrection>();
        if (_corrections is not null)
        {
            try
            {
                applicable.AddRange(await _corrections.GetActiveForGameAsync(gameId));
                ledger.AddRange(await _corrections.GetForGameAsync(gameId));
            }
            catch (Exception ex) { _logger.LogDebug(ex, "VOD: corrections load failed for {GameId}", gameId); }
        }
        var byApplied = new Dictionary<long, EventCorrection>();
        var bySubject = new Dictionary<string, EventCorrection>(StringComparer.Ordinal);
        foreach (var c in applicable)
        {
            // Removes own no live row; they become ghosts below.
            if (c.Op == CorrectionOps.Remove) continue;
            if (c.AppliedEventId is { } appliedId) byApplied.TryAdd(appliedId, c);
            if (c.SubjectKey.Length > 0) bySubject.TryAdd(c.SubjectKey, c);
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
            // markers use TokenTiesForEvent (direct token ties only). A stored TEAMFIGHT
            // row is its fight: it renders ONLY through the fight pass below, never as a
            // generic marker (that would put two pins on one fight).
            foreach (var e in raw)
            {
                if (TeamfightClustering.IsStoredTeamfight(e)) continue;
                gameEvents.Add(Decorate(MapEvent(e, tieResolver.TokenTiesForEvent(e)), e, byApplied, bySubject));
            }

            // …and every fight becomes exactly ONE TEAMFIGHT event: a stored post-game row
            // (numbers, verdict, roster — in AND away fights, keyed by the row's own id) or,
            // for a game the pass has not reached, the synthetic own-event cluster it has
            // always been (negative id, only when some objective tracks it, as today).
            // Ties come from the shared resolver so the pin, the auto-clipper and the
            // pattern anchors can never disagree about which objectives a fight belongs to.
            var ties = tieResolver.ResolveTeamfightClusters(raw);
            var synthId = -1L;
            foreach (var span in TeamfightClustering.Resolve(raw))
            {
                var cluster = ties.FirstOrDefault(c => span.Stored is not null
                    ? c.StoredEventId == span.Stored.Id
                    : c.StoredEventId is null && c.StartS == span.StartS);
                var objectives = cluster?.Objectives ?? Array.Empty<ObjectiveTie>();
                if (span.Stored is null && objectives.Count == 0) continue;
                gameEvents.Add(Decorate(
                    VodTeamfightMapper.Map(span, objectives, span.Stored?.Id ?? synthId--), span.Stored, byApplied, bySubject));
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "VOD: game events load failed for {GameId}", gameId); }

        // v3.11 ghosts: an active op remove DELETED its row at save time (so death counts,
        // derived instances and anchors need no tombstone filter); the timeline still shows
        // where it was, as a dashed bar the user can select and restore. Appended after the
        // fight pins, Id = 0.
        foreach (var c in applicable)
        {
            if (c.Op == CorrectionOps.Remove && c.State == CorrectionStates.Active)
                gameEvents.Add(Ghost(c));
        }

        // Evidence inbox → split into AUTO moments (timeline_region) vs SAVED
        // CLIPS (clip). Mirrors VodPlayerViewModel.RefreshEvidenceInboxAsync's
        // AutoReviewMoments / SavedClipReviewMoments split. Bookmarks render from
        // the Bookmarks list above (the 3rd lane of the 3-way filter).
        var autoMoments = new List<VodEvidenceDto>();
        var savedClips = new List<VodEvidenceDto>();
        try
        {
            // v3.10: untouched auto anchors (the post-game pass's trades / fights /
            // failed criteria) fill the AUTO lane only while the user's "Auto-fill
            // Timeline Inbox from game events" setting is on.
            var raw = EvidenceAutoAnchors.ForSurface(
                await _evidenceRepo.GetForGameAsync(gameId, includeDismissed: false),
                _config.AutoTimelineClippingEnabled);
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
            SavedClips: savedClips,
            EventTypeCatalog: VodCorrectionMapper.Catalog,
            Corrections: ledger.Select(VodCorrectionMapper.Map).ToList());
    }

    // ── v3.11 correction decoration ────────────────────────────────────────────
    // The ledger row attached to a marker is found by applied_event_id first, then by
    // the row's stable event_key (a fuzzy rebase can move the key, never the id). A
    // synthetic fight pin (no stored row) keeps EventKey "" and is never decorated.
    private static VodEventDto Decorate(
        VodEventDto dto,
        GameEvent? row,
        IReadOnlyDictionary<long, EventCorrection> byApplied,
        IReadOnlyDictionary<string, EventCorrection> bySubject)
    {
        if (row is null) return dto;
        var key = row.EventKey ?? "";
        EventCorrection? c;
        if (!byApplied.TryGetValue(row.Id, out c) && (key.Length == 0 || !bySubject.TryGetValue(key, out c)))
            c = null;
        if (c is null) return dto with { EventKey = key };
        return dto with
        {
            EventKey = key,
            Corrected = c.Op != CorrectionOps.Add,
            AddedByUser = c.Op == CorrectionOps.Add,
            Confirmed = c.Patch.Confirmed,
            CorrectionState = c.State,
            CorrectionId = c.CorrectionId,
            CorrectionOp = c.Op,
        };
    }

    // The marker of a removed event, rebuilt from the ledger's subject (type + time as
    // detected). Kind/color follow the type's bucket so the ghost sits in its family.
    private static VodEventDto Ghost(EventCorrection c)
    {
        var (kind, colorHex) = BucketEvent(c.SubjectType);
        return new VodEventDto(
            Id: 0,
            EventType: c.SubjectType,
            GameTimeSeconds: c.SubjectTimeS,
            TimeLabel: FormatClock(c.SubjectTimeS),
            ShortLabel: ShortLabel(c.SubjectType),
            Label: EventLabel(c.SubjectType),
            Summary: "removed by you",
            Kind: kind,
            ColorHex: colorHex,
            EventKey: c.SubjectKey,
            Removed: true,
            CorrectionState: c.State,
            CorrectionId: c.CorrectionId,
            CorrectionOp: CorrectionOps.Remove);
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

        // JUNGLE_PROXIMITY markers carry WHOSE jungler is near (Details.who) in
        // every human-facing channel — hue (enemy threat pink / ally calm teal),
        // short code (EJX/AJX) and label — because an objective tied to the
        // GENERIC token paints focused markers in the objective's color, and the
        // short code is then the only thing left distinguishing enemy from ally.
        var proximityWho = string.Equals(e.EventType, GameEvent.EventTypes.JungleProximity, StringComparison.OrdinalIgnoreCase)
            ? ReadJsonWho(e)
            : null;
        if (proximityWho is not null)
            colorHex = proximityWho switch
            {
                "enemy" => EnemyProximityHex,
                "ally" => AllyProximityHex,
                _ => ProximityHex,
            };

        long? objId = matches.Count > 0 ? matches[0].ObjectiveId : null;
        var objTitle = matches.Count > 0 ? matches[0].Title : "";
        var objColor = matches.Count > 0 ? matches[0].Color : "";
        var objIds = matches.Count > 0 ? matches.Select(m => m.ObjectiveId).ToList() : null;

        return new VodEventDto(
            Id: e.Id,
            EventType: e.EventType ?? "",
            GameTimeSeconds: e.GameTimeS,
            TimeLabel: FormatClock(e.GameTimeS),
            ShortLabel: isJungleGank ? "GNK"
                : proximityWho switch { "enemy" => "EJX", "ally" => "AJX", _ => (string?)null } ?? ShortLabel(e.EventType),
            Label: isJungleGank ? "Jungle Gank"
                : proximityWho switch { "enemy" => "Enemy Jungler Near", "ally" => "Ally Jungler Near", _ => (string?)null } ?? EventLabel(e.EventType),
            Summary: isJungleGank ? JungleGankSummary(e) : EventSummary(e),
            Kind: kind,
            ColorHex: colorHex,
            ObjectiveId: objId,
            ObjectiveTitle: objTitle,
            ObjectiveColorHex: objColor,
            ObjectiveIds: objIds,
            EncounterClassification: EncounterProperty(e, "classification") is { Length: > 0 } classification
                ? classification : EncounterProperty(e, "kind"),
            EncounterEndSeconds: EncounterEnd(e),
            EncounterNote: EncounterProperty(e, "note"),
            ReviewedEncounter: EncounterProperty(e, "source") == "reviewed_encounter");
    }

    private static string EncounterProperty(GameEvent e, string property)
    {
        if (!ReviewedEncountersRepository.IsEncounter(e.EventType)) return "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(e.Details);
            return ReadJsonString(doc.RootElement, property);
        }
        catch { return ""; }
    }

    private static int? EncounterEnd(GameEvent e)
    {
        if (!ReviewedEncountersRepository.IsEncounter(e.EventType)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(e.Details);
            return doc.RootElement.TryGetProperty("end_s", out var end) && end.TryGetInt32(out var seconds)
                ? seconds : e.GameTimeS;
        }
        catch { return e.GameTimeS; }
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
            "ALL_IN" => ("all-in", "#f87171"),
            "UNCERTAIN_COMBAT" => ("neutral", "#9ca3af"),
            "JUNGLE_PROXIMITY" => ("neutral", ProximityHex), // hue refined by Details.who in MapEvent
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
        "ALL_IN" => "ALL",
        "UNCERTAIN_COMBAT" => "?",
        "JUNGLE_PROXIMITY" => "JPX",
        "TEAMFIGHT" => "TF",
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
        "ALL_IN" => "All-in",
        "UNCERTAIN_COMBAT" => "Uncertain combat",
        "JUNGLE_PROXIMITY" => "Jungle Proximity",
        "TEAMFIGHT" => "Teamfight",
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
                "ALL_IN" or "UNCERTAIN_COMBAT" => ReadJsonString(root, "note"),
                "JUNGLE_PROXIMITY" => ProximitySummary(root),
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

    // "enemy Nocturne 671 units · 45s" — whose jungler was near, the closest
    // approach, and (v2 visits) how long he stayed inside the threat radius.
    private static string ProximitySummary(System.Text.Json.JsonElement root)
    {
        var who = ReadJsonString(root, "who");
        var champion = ReadJsonString(root, "champion");
        var subject = string.Join(' ', new[] { who, champion.Length > 0 ? champion : "jungler" }
            .Where(static s => s.Length > 0));
        if (root.TryGetProperty("distance", out var d) && d.TryGetInt32(out var dist) && dist > 0)
            subject = $"{subject} {dist} units";
        if (root.TryGetProperty("duration_s", out var du) && du.TryGetInt32(out var seconds) && seconds > 0)
            subject = $"{subject} · {seconds}s";
        return subject;
    }

    // Details.who from a JUNGLE_PROXIMITY event ("enemy" | "ally"), lower-cased,
    // "" on any parse failure.
    private static string ReadJsonWho(GameEvent e)
    {
        if (string.IsNullOrWhiteSpace(e.Details) || e.Details == "{}") return "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(e.Details);
            return ReadJsonString(doc.RootElement, "who").Trim().ToLowerInvariant();
        }
        catch { return ""; }
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
        HasClip: string.Equals(item.SourceKind, EvidenceKinds.Clip, StringComparison.OrdinalIgnoreCase),
        PromptId: item.PromptId);

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
        SavedClips: Array.Empty<VodEvidenceDto>(),
        EventTypeCatalog: VodCorrectionMapper.Catalog,
        Corrections: Array.Empty<VodCorrectionDto>());

    private static string FormatClock(int seconds)
    {
        if (seconds < 0) seconds = 0;
        return $"{seconds / 60}:{seconds % 60:D2}";
    }
}

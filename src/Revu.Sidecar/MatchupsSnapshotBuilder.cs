#nullable enable

using Microsoft.Extensions.Logging;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>
/// The most recent game and what a journal card pre-filled from it would hold.
/// <see cref="Game"/> null = no games on record; <see cref="Prefill"/> null =
/// the game's lane or champions can't be resolved; <see cref="Existing"/> is the
/// card already linked to that game, if any.
/// </summary>
public sealed record LastGameResolution(GameStats? Game, MatchupPrefillResult? Prefill, MatchupCard? Existing);

/// <summary>
/// Builds the read-only matchup-journal snapshot served at GET /api/matchups and
/// the Markdown served at GET /api/matchups/export.
///
/// <para>
/// Cards come from <see cref="IMatchupsRepository.GetAllAsync"/> (newest first)
/// and are grouped by lane (fixed <see cref="MatchupLanes.All"/> order, empty
/// lanes omitted) then by <see cref="MatchupLanes.Key"/>, the group holding the
/// newest card first. The "New card from last game" preview resolves the most
/// recent game through the SAME pure <see cref="MatchupPrefill"/> the write
/// route uses (<see cref="ResolveLastGameAsync"/> is shared), so the button
/// never promises a card the write can't create.
/// </para>
///
/// <para>
/// READ-ONLY: only repository read methods are called. The card writes are the
/// <c>POST /api/matchup/*</c> endpoints (WriteServices.Matchups); the page
/// refetches this snapshot after each. Like the other builders, each section
/// degrades to empty on failure so one bad read never blanks the page.
/// </para>
/// </summary>
public sealed class MatchupsSnapshotBuilder
{
    public const string EmptyMessage =
        "No matchup cards yet. Write a prior before you queue, then what you actually saw after.";
    public const string NoGamesReason = "No games recorded yet.";
    public const string NoPrefillReason = "Couldn't tell which lane you played in your last game.";
    public const string LoadFailedReason = "Couldn't read your last game.";
    /// <summary>Opponents missing and the player is signed in: the click looks them up first.</summary>
    public const string EnemyLookupHint = "Opponents weren't recorded for this game — Revu will look them up from Riot when you click.";
    /// <summary>Opponents missing and no Riot session: the click opens the form to add them.</summary>
    public const string EnemyManualHint = "Opponents weren't recorded for this game — you'll add them when the card opens.";
    /// <summary>The game carries no lane; the pre-fill used the configured primary role, so the form opens for a check.</summary>
    public const string LaneGuessHint = "Lane guessed from your primary role — check it when the card opens.";

    /// <summary>v3.10.1: the matchup was estimated at game end and Riot has not confirmed it yet.</summary>
    public const string EstimateLookupHint = "Estimated when the game ended. Revu checks it with Riot when you click.";

    /// <summary>v3.10.1: an estimate that cannot be checked (not signed in).</summary>
    public const string EstimateManualHint = "Estimated when the game ended. Check it when the card opens.";

    private readonly IMatchupsRepository _matchups;
    private readonly IGameHistoryQuery _games;
    private readonly IConfigService _config;
    private readonly ILogger<MatchupsSnapshotBuilder> _logger;

    public MatchupsSnapshotBuilder(
        IMatchupsRepository matchups,
        IGameHistoryQuery games,
        IConfigService config,
        ILogger<MatchupsSnapshotBuilder> logger)
    {
        _matchups = matchups;
        _games = games;
        _config = config;
        _logger = logger;
    }

    public async Task<MatchupsDto> BuildAsync(CancellationToken ct = default)
    {
        var now = DateTime.Now;

        IReadOnlyList<MatchupCard> cards;
        try
        {
            cards = await _matchups.GetAllAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Matchups: card load failed");
            cards = [];
        }

        ct.ThrowIfCancellationRequested();
        var gameLabels = await BuildGameLabelsAsync(cards);
        var lanes = BuildLanes(cards, gameLabels);
        var lastGame = await BuildLastGameAsync();

        return new MatchupsDto(
            GeneratedAt: now.ToString("yyyy-MM-ddTHH:mm:ss"),
            TotalCount: cards.Count,
            IsEmpty: cards.Count == 0,
            EmptyMessage: EmptyMessage,
            Lanes: lanes,
            LastGame: lastGame);
    }

    /// <summary>
    /// GET /api/matchups/export: the journal as Markdown after the optional
    /// lane / last-N filters, plus how many cards it covers.
    /// </summary>
    public async Task<(string Markdown, int Count)> BuildExportAsync(string? lane, long? last)
    {
        // The query param binds as long so an absurd value can't 400 the route;
        // anything past int.MaxValue simply means "all".
        var take = last is > 0 ? (int)Math.Min(last.Value, int.MaxValue) : (int?)null;
        var cards = MatchupJournalExporter.Filter(await _matchups.GetAllAsync(), lane, take);
        return (MatchupJournalExporter.Build(cards), cards.Count);
    }

    /// <summary>
    /// The most recent game and its pre-fill. "Most recent" is the newest
    /// ranked / manual, non-hidden game — <see cref="IGameHistoryQuery.GetRecentAsync"/>'s
    /// scope, the same one every games list and the review queue use — so a
    /// casual game never seeds a card. Shared by this read snapshot and the
    /// POST /api/matchup/from-last-game write so both see the same game, the
    /// same champions, and the same existing-card check.
    /// </summary>
    public static async Task<LastGameResolution> ResolveLastGameAsync(
        IGameHistoryQuery games, IMatchupsRepository matchups, string? fallbackPosition = null)
    {
        var recent = await games.GetRecentAsync(limit: 1);
        var game = recent.Count > 0 ? recent[0] : null;
        if (game is null) return new LastGameResolution(null, null, null);

        // fallbackPosition = the player's configured primary role, for a row
        // with no position and no usable participant map.
        var prefill = MatchupPrefill.FromGame(game, fallbackPosition);
        var existing = await matchups.GetForGameAsync(game.GameId);
        return new LastGameResolution(game, prefill, existing);
    }

    /// <summary>"Sep 5, 2026 · Win" — the games-list date style plus the result.</summary>
    internal static string GameLabel(GameStats game)
    {
        var date = MatchupJournalExporter.CreatedAtText(game.Timestamp);
        var result = game.Win ? "Win" : "Loss";
        return date.Length > 0 ? $"{date} · {result}" : result;
    }

    internal static MatchupCardDto MapCard(MatchupCard card, IReadOnlyDictionary<long, string> gameLabels)
    {
        var hasGame = card.GameId is > 0;
        var gameLabel = hasGame && gameLabels.TryGetValue(card.GameId!.Value, out var label) ? label : "";
        return new MatchupCardDto(
            Id: card.Id,
            Lane: card.Lane,
            LaneLabel: MatchupLanes.Label(card.Lane),
            AllyChamps: card.AllyChamps,
            EnemyChamps: card.EnemyChamps,
            MatchupKey: MatchupLanes.Key(card.Lane, card.AllyChamps, card.EnemyChamps),
            MatchupTitle: MatchupLanes.Title(card.AllyChamps, card.EnemyChamps),
            Prior: card.Prior,
            Observed: card.Observed,
            HasPrior: !string.IsNullOrWhiteSpace(card.Prior),
            HasObserved: !string.IsNullOrWhiteSpace(card.Observed),
            GameId: hasGame ? card.GameId : null,
            HasGame: hasGame,
            GameLabel: gameLabel,
            CreatedAt: card.CreatedAt,
            CreatedAtText: MatchupJournalExporter.CreatedAtText(card.CreatedAt),
            DateText: MatchupJournalExporter.DateText(card.CreatedAt));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Section builders
    // ─────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<MatchupLaneDto> BuildLanes(
        IReadOnlyList<MatchupCard> cards, IReadOnlyDictionary<long, string> gameLabels)
    {
        var lanes = new List<MatchupLaneDto>();
        foreach (var lane in MatchupLanes.All)
        {
            var laneCards = cards.Where(c => MatchupLanes.Normalize(c.Lane) == lane).ToList();
            if (laneCards.Count == 0) continue;

            var groups = laneCards
                .GroupBy(c => MatchupLanes.Key(c.Lane, c.AllyChamps, c.EnemyChamps))
                .Select(g =>
                {
                    var ordered = g.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).ToList();
                    var newest = ordered[0];
                    return new MatchupGroupDto(
                        Key: g.Key,
                        Title: MatchupLanes.Title(newest.AllyChamps, newest.EnemyChamps),
                        AllyChamps: newest.AllyChamps,
                        EnemyChamps: newest.EnemyChamps,
                        CardCount: ordered.Count,
                        LatestCreatedAt: newest.CreatedAt,
                        Cards: ordered.Select(c => MapCard(c, gameLabels)).ToList());
                })
                // Same tie-break as MatchupJournalExporter so the page and the
                // export never disagree on which group leads.
                .OrderByDescending(g => g.LatestCreatedAt)
                .ThenByDescending(g => g.Cards.Max(c => c.Id))
                .ToList();

            lanes.Add(new MatchupLaneDto(
                Lane: lane,
                LaneLabel: MatchupLanes.Label(lane),
                ChampSlots: MatchupLanes.ChampSlots(lane),
                CardCount: laneCards.Count,
                Groups: groups));
        }
        return lanes;
    }

    /// <summary>"Sep 5, 2026 · Win" per linked game (one read per distinct id; best-effort).</summary>
    private async Task<IReadOnlyDictionary<long, string>> BuildGameLabelsAsync(IReadOnlyList<MatchupCard> cards)
    {
        var labels = new Dictionary<long, string>();
        foreach (var gameId in cards.Where(c => c.GameId is > 0).Select(c => c.GameId!.Value).Distinct())
        {
            try
            {
                var game = await _games.GetAsync(gameId);
                if (game is not null) labels[gameId] = GameLabel(game);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Matchups: game {GameId} label load failed (degrading)", gameId);
            }
        }
        return labels;
    }

    private async Task<LastGamePrefillDto> BuildLastGameAsync()
    {
        try
        {
            // The read graph owns its own IConfigService instance; like
            // ConfigSnapshotBuilder, force a disk re-read so a sign-in or an
            // onboarding primary role saved via WriteServices.Config since the
            // last read shapes the hint and the lane fallback below.
            try { await _config.LoadAsync(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Matchups: config re-read failed; using the cached copy"); }

            var last = await ResolveLastGameAsync(_games, _matchups, _config.PrimaryRole);
            if (last.Game is null) return Unavailable(NoGamesReason);
            var gameLabel = GameLabel(last.Game);

            // A card already links to the game: the button opens it, and the
            // line describes THAT card (not a re-derived, possibly partial,
            // pre-fill) — the same order the write route checks in.
            if (last.Existing is { } card)
            {
                return new LastGamePrefillDto(
                    Available: true,
                    GameId: last.Game.GameId,
                    Lane: card.Lane,
                    LaneLabel: MatchupLanes.Label(card.Lane),
                    AllyChamps: card.AllyChamps,
                    EnemyChamps: card.EnemyChamps,
                    EnemyKnown: true,
                    MatchupTitle: MatchupLanes.Title(card.AllyChamps, card.EnemyChamps),
                    GameLabel: gameLabel,
                    Hint: "",
                    ExistingCardId: card.Id,
                    UnavailableReason: "");
            }

            if (last.Prefill is null) return Unavailable(NoPrefillReason, last.Game.GameId, gameLabel);

            var complete = last.Prefill.IsComplete;
            var hint = !complete
                ? (MatchupFromLastGame.CanLookUpMatches(_config) ? EnemyLookupHint : EnemyManualHint)
                : last.Prefill.LaneIsGuess ? LaneGuessHint
                // v3.10.1: an unconfirmed game-end estimate opens the form; the write
                // route creates outright only once Riot has confirmed it.
                : Revu.Core.Models.MatchupSources.NeedsConfirmation(last.Game.MatchupSource)
                    ? (MatchupFromLastGame.CanLookUpMatches(_config) ? EstimateLookupHint : EstimateManualHint)
                : "";
            return new LastGamePrefillDto(
                Available: true,
                GameId: last.Game.GameId,
                Lane: last.Prefill.Lane,
                LaneLabel: MatchupLanes.Label(last.Prefill.Lane),
                AllyChamps: last.Prefill.AllyChamps,
                EnemyChamps: last.Prefill.EnemyChamps,
                EnemyKnown: complete,
                MatchupTitle: last.Prefill.Title,
                GameLabel: gameLabel,
                Hint: hint,
                ExistingCardId: null,
                UnavailableReason: "");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Matchups: last-game prefill failed (degrading)");
            return Unavailable(LoadFailedReason);
        }
    }

    private static LastGamePrefillDto Unavailable(string reason, long gameId = 0, string gameLabel = "") => new(
        Available: false,
        GameId: gameId,
        Lane: "",
        LaneLabel: "",
        AllyChamps: [],
        EnemyChamps: [],
        EnemyKnown: false,
        MatchupTitle: "",
        GameLabel: gameLabel,
        Hint: "",
        ExistingCardId: null,
        UnavailableReason: reason);
}

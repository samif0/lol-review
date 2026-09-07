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
    public const string NoPrefillReason = "Couldn't tell the lane or champions of your last game.";
    public const string LoadFailedReason = "Couldn't read your last game.";

    private readonly IMatchupsRepository _matchups;
    private readonly IGameHistoryQuery _games;
    private readonly ILogger<MatchupsSnapshotBuilder> _logger;

    public MatchupsSnapshotBuilder(
        IMatchupsRepository matchups,
        IGameHistoryQuery games,
        ILogger<MatchupsSnapshotBuilder> logger)
    {
        _matchups = matchups;
        _games = games;
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
    public async Task<(string Markdown, int Count)> BuildExportAsync(string? lane, int? last)
    {
        var cards = MatchupJournalExporter.Filter(await _matchups.GetAllAsync(), lane, last);
        return (MatchupJournalExporter.Build(cards), cards.Count);
    }

    /// <summary>
    /// The most recent ranked / manual game and its pre-fill. Shared by this
    /// read snapshot and the POST /api/matchup/from-last-game write so both see
    /// the same game, the same champions, and the same existing-card check.
    /// </summary>
    public static async Task<LastGameResolution> ResolveLastGameAsync(IGameHistoryQuery games, IMatchupsRepository matchups)
    {
        var recent = await games.GetRecentAsync(limit: 1);
        var game = recent.Count > 0 ? recent[0] : null;
        if (game is null) return new LastGameResolution(null, null, null);

        var prefill = MatchupPrefill.FromGame(game);
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
                .OrderByDescending(g => g.LatestCreatedAt)
                .ThenByDescending(g => g.Cards[0].Id)
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
            var last = await ResolveLastGameAsync(_games, _matchups);
            if (last.Game is null) return Unavailable(NoGamesReason);
            if (last.Prefill is null) return Unavailable(NoPrefillReason);

            return new LastGamePrefillDto(
                Available: true,
                GameId: last.Game.GameId,
                Lane: last.Prefill.Lane,
                LaneLabel: MatchupLanes.Label(last.Prefill.Lane),
                AllyChamps: last.Prefill.AllyChamps,
                EnemyChamps: last.Prefill.EnemyChamps,
                MatchupTitle: last.Prefill.Title,
                GameLabel: GameLabel(last.Game),
                ExistingCardId: last.Existing?.Id,
                UnavailableReason: "");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Matchups: last-game prefill failed (degrading)");
            return Unavailable(LoadFailedReason);
        }
    }

    private static LastGamePrefillDto Unavailable(string reason) => new(
        Available: false,
        GameId: 0,
        Lane: "",
        LaneLabel: "",
        AllyChamps: [],
        EnemyChamps: [],
        MatchupTitle: "",
        GameLabel: "",
        ExistingCardId: null,
        UnavailableReason: reason);
}

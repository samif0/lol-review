#nullable enable

using Revu.Core.Data.Repositories;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Services;

/// <summary>v2.16.1: a single passive-learning card surfaced on PreGamePage.
/// Rotated every few seconds by IntelRotatorControl. Headline + body, with
/// an optional eyebrow/category label for the small caps text above.</summary>
public sealed record IntelCard(
    string Eyebrow,
    string Headline,
    string Body);

/// <summary>v2.16.1: assembles the rotating intel deck shown during champ
/// select. Mixes the user's own data (priority objective, last game, matchup
/// notes, prior pre-game answers) with external static data (enemy ability
/// cooldowns from CommunityDragon).
///
/// All inputs are best-effort — any individual fetch failure just drops that
/// card from the deck. An empty deck is a valid result; the caller should
/// hide the rotator when it's empty.
/// </summary>
public sealed class PreGameIntelService
{
    private readonly IObjectivesRepository _objectives;
    private readonly ISessionLogRepository _sessionLog;
    private readonly IGameRepository _games;
    private readonly IMatchupNotesRepository _matchupNotes;
    private readonly IPromptsRepository _prompts;
    private readonly IRiotChampionDataClient _championData;
    private readonly ILogger<PreGameIntelService> _logger;

    public PreGameIntelService(
        IObjectivesRepository objectives,
        ISessionLogRepository sessionLog,
        IGameRepository games,
        IMatchupNotesRepository matchupNotes,
        IPromptsRepository prompts,
        IRiotChampionDataClient championData,
        ILogger<PreGameIntelService> logger)
    {
        _objectives = objectives;
        _sessionLog = sessionLog;
        _games = games;
        _matchupNotes = matchupNotes;
        _prompts = prompts;
        _championData = championData;
        _logger = logger;
    }

    /// <summary>Build the rotating intel deck for the current champ select.
    /// <paramref name="myChampion"/> is the user's locked champion (display
    /// name, e.g. "Kai'Sa"); <paramref name="enemyChampion"/> is the opposing
    /// laner. Either may be empty if champ select is still in progress.</summary>
    public async Task<IReadOnlyList<IntelCard>> BuildAsync(
        string myChampion,
        string enemyChampion,
        CancellationToken ct = default)
    {
        var cards = new List<IntelCard>();

        await TryAddPriorityObjective(cards, ct).ConfigureAwait(false);
        await TryAddLastGameIntel(cards, myChampion, ct).ConfigureAwait(false);
        await TryAddMatchupHistory(cards, myChampion, enemyChampion, ct).ConfigureAwait(false);
        await TryAddPreGameAnswers(cards, myChampion, ct).ConfigureAwait(false);
        await TryAddEnemyAbilities(cards, enemyChampion, ct).ConfigureAwait(false);

        return cards;
    }

    private async Task TryAddPriorityObjective(List<IntelCard> cards, CancellationToken ct)
    {
        try
        {
            var priority = await _objectives.GetPriorityAsync().ConfigureAwait(false);
            if (priority is null) return;
            cards.Add(new IntelCard(
                Eyebrow: "FOCUS THIS SESSION",
                Headline: priority.Title,
                Body: string.IsNullOrWhiteSpace(priority.CompletionCriteria)
                    ? ""
                    : $"Success: {priority.CompletionCriteria}"));
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Intel: priority objective skipped"); }
    }

    private async Task TryAddLastGameIntel(List<IntelCard> cards, string myChampion, CancellationToken ct)
    {
        try
        {
            // Pull the most recent session_log entry irrespective of champion —
            // "last game" is about carry-over, not champion-specific learning.
            var entries = await _sessionLog.GetRangeAsync(days: 7).ConfigureAwait(false);
            var last = entries.OrderByDescending(e => e.Timestamp).FirstOrDefault();
            if (last is null) return;
            if (string.IsNullOrWhiteSpace(last.ImprovementNote)) return;

            var resultText = last.Win ? "W" : "L";
            cards.Add(new IntelCard(
                Eyebrow: $"LAST GAME · {last.ChampionName.ToUpperInvariant()} · {resultText}",
                Headline: last.ImprovementNote,
                Body: $"Mental: {last.MentalRating}/10"));
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Intel: last game skipped"); }
    }

    private async Task TryAddMatchupHistory(
        List<IntelCard> cards, string myChampion, string enemyChampion, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(myChampion) || string.IsNullOrWhiteSpace(enemyChampion)) return;
        try
        {
            var notes = await _matchupNotes.GetForMatchupAsync(myChampion, enemyChampion).ConfigureAwait(false);
            if (notes.Count == 0) return;

            // Most-recent first; emit one card per note so each gets equal
            // air-time in the rotation.
            foreach (var note in notes.OrderByDescending(n => n.CreatedAt).Take(3))
            {
                if (string.IsNullOrWhiteSpace(note.Note)) continue;
                cards.Add(new IntelCard(
                    Eyebrow: $"MATCHUP · {myChampion.ToUpperInvariant()} VS {enemyChampion.ToUpperInvariant()}",
                    Headline: note.Note,
                    Body: ""));
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Intel: matchup notes skipped"); }
    }

    private async Task TryAddPreGameAnswers(List<IntelCard> cards, string myChampion, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(myChampion)) return;
        try
        {
            // Find the most recent game on this champion that has prompt answers,
            // then surface the ones from the pre-game phase.
            var recent = await _games.GetRecentAsync(limit: 5, champion: myChampion).ConfigureAwait(false);
            foreach (var game in recent.OrderByDescending(g => g.Timestamp))
            {
                var answers = await _prompts.GetAnswersForGameAsync(game.GameId).ConfigureAwait(false);
                var preGame = answers
                    .Where(a => string.Equals(a.Phase, "pregame", StringComparison.OrdinalIgnoreCase))
                    .Where(a => !string.IsNullOrWhiteSpace(a.AnswerText))
                    .ToList();
                if (preGame.Count == 0) continue;

                foreach (var ans in preGame.Take(2))
                {
                    cards.Add(new IntelCard(
                        Eyebrow: $"YOUR LAST {myChampion.ToUpperInvariant()} PRE-GAME",
                        Headline: ans.AnswerText,
                        Body: string.IsNullOrWhiteSpace(ans.Label) ? "" : ans.Label));
                }
                return; // only mine the most recent qualifying game
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Intel: pre-game answers skipped"); }
    }

    private async Task TryAddEnemyAbilities(List<IntelCard> cards, string enemyChampion, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(enemyChampion)) return;
        try
        {
            var id = await _championData.ResolveChampionIdAsync(enemyChampion, ct).ConfigureAwait(false);
            if (id <= 0) return;
            var data = await _championData.GetChampionAbilitiesAsync(id, ct).ConfigureAwait(false);
            if (data is null) return;

            // One card listing Q/W/E/R cooldowns at level 1. Kept compact —
            // the rotator cycles, so we don't need a card per ability.
            var spells = data.Abilities.Where(a => a.Slot is "Q" or "W" or "E" or "R").ToList();
            if (spells.Count == 0) return;

            var lines = new List<string>();
            foreach (var s in spells)
            {
                if (s.CooldownByRank.Count == 0)
                {
                    lines.Add($"{s.Slot} · {s.Name}");
                }
                else
                {
                    var lvl1 = s.CooldownByRank[0];
                    lines.Add($"{s.Slot} · {s.Name} · {lvl1:0.#}s lvl 1");
                }
            }

            cards.Add(new IntelCard(
                Eyebrow: $"{enemyChampion.ToUpperInvariant()} · KEY COOLDOWNS",
                Headline: data.Name,
                Body: string.Join("\n", lines)));
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Intel: enemy abilities skipped"); }
    }
}

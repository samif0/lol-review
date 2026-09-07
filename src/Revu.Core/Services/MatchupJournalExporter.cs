#nullable enable

using System.Globalization;
using System.Text;
using Revu.Core.Models;

namespace Revu.Core.Services;

/// <summary>
/// Markdown export of the matchup journal: one H2 per lane (fixed lane order),
/// one H3 per matchup key (the group with the newest card first), and per card
/// its date plus <c>Prior</c> / <c>Observed</c> H4 subheadings — a shape a
/// notes app or an LLM can read back without Revu. Pure over
/// <see cref="MatchupCard"/> rows, so it is unit-tested without a database and
/// the sidecar's export route is a one-liner over it.
/// </summary>
public static class MatchupJournalExporter
{
    public const string Heading = "# Matchup Journal";
    private const string EmptyNote = "_(not written yet)_";

    /// <summary>
    /// Apply the optional filters: <paramref name="lane"/> keeps one lane;
    /// <paramref name="last"/> keeps that many newest cards (after the lane
    /// filter). Null / blank / non-positive values mean "no filter". Output is
    /// newest first regardless of input order.
    /// </summary>
    public static IReadOnlyList<MatchupCard> Filter(IReadOnlyList<MatchupCard> cards, string? lane, int? last)
    {
        IEnumerable<MatchupCard> query = cards
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id);

        var wanted = MatchupLanes.Normalize(lane);
        if (wanted is not null)
        {
            query = query.Where(c => MatchupLanes.Normalize(c.Lane) == wanted);
        }

        if (last is > 0)
        {
            query = query.Take(last.Value);
        }

        return query.ToList();
    }

    /// <summary>Build the Markdown document for the given cards (any order).</summary>
    public static string Build(IReadOnlyList<MatchupCard> cards, DateTimeOffset? exportedAt = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Heading);
        sb.AppendLine();

        if (cards.Count == 0)
        {
            sb.AppendLine("_No cards._");
            return sb.ToString();
        }

        var stamp = (exportedAt ?? DateTimeOffset.Now).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        sb.AppendLine($"{CountText(cards.Count)} · exported {stamp}");

        var byLane = cards
            .GroupBy(c => MatchupLanes.Normalize(c.Lane) ?? c.Lane.Trim().ToLowerInvariant())
            .OrderBy(g => MatchupLanes.Order(g.Key))
            .ThenBy(g => g.Key, StringComparer.Ordinal);

        foreach (var laneGroup in byLane)
        {
            sb.AppendLine();
            var label = MatchupLanes.Label(laneGroup.Key);
            sb.AppendLine($"## {(label.Length > 0 ? label : laneGroup.Key)}");

            var byMatchup = laneGroup
                .GroupBy(c => MatchupLanes.Key(c.Lane, c.AllyChamps, c.EnemyChamps))
                .OrderByDescending(g => g.Max(c => c.CreatedAt))
                .ThenByDescending(g => g.Max(c => c.Id));

            foreach (var matchup in byMatchup)
            {
                var ordered = matchup
                    .OrderByDescending(c => c.CreatedAt)
                    .ThenByDescending(c => c.Id)
                    .ToList();
                var newest = ordered[0];

                sb.AppendLine();
                sb.AppendLine($"### {OneLine(MatchupLanes.Title(newest.AllyChamps, newest.EnemyChamps))}");

                foreach (var card in ordered)
                {
                    sb.AppendLine();
                    sb.AppendLine($"**{DateText(card.CreatedAt)}**");
                    sb.AppendLine();
                    sb.AppendLine("#### Prior");
                    sb.AppendLine();
                    sb.AppendLine(Note(card.Prior));
                    sb.AppendLine();
                    sb.AppendLine("#### Observed");
                    sb.AppendLine();
                    sb.AppendLine(Note(card.Observed));
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>Local calendar date of a unix-seconds timestamp as "yyyy-MM-dd"; "" when unset.</summary>
    public static string DateText(long unixSeconds) =>
        unixSeconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "";

    /// <summary>Local date of a unix-seconds timestamp as "MMM d, yyyy" (the games list's date style); "" when unset.</summary>
    public static string CreatedAtText(long unixSeconds) =>
        unixSeconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)
            : "";

    private static string CountText(int count) => count == 1 ? "1 card" : $"{count} cards";

    private static string Note(string? value) =>
        string.IsNullOrWhiteSpace(value) ? EmptyNote : value.Trim().ReplaceLineEndings("\n");

    private static string OneLine(string value) => value.ReplaceLineEndings(" ").Trim();
}

#nullable enable

using System.Text;
using LoLReview.Core.Data.Repositories;
using Microsoft.Extensions.Logging;

namespace LoLReview.Core.Services;

/// <summary>
/// Generates a paste-ready text block for a new Claude conversation.
/// Ported from Python database/context.py.
/// </summary>
public sealed class ClaudeContextService : IClaudeContextService
{
    private const string Separator = "============================================================";

    private readonly IGameRepository _games;
    private readonly ISessionLogRepository _sessionLog;
    private readonly INotesRepository _notes;
    private readonly ILogger<ClaudeContextService> _logger;

    public ClaudeContextService(
        IGameRepository games,
        ISessionLogRepository sessionLog,
        INotesRepository notes,
        ILogger<ClaudeContextService> logger)
    {
        _games = games;
        _sessionLog = sessionLog;
        _notes = notes;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> GenerateContextAsync()
    {
        var sb = new StringBuilder();

        sb.AppendLine(Separator);
        sb.AppendLine("LEAGUE OF LEGENDS \u2014 SESSION LOG & CONTEXT");
        sb.AppendLine(Separator);
        sb.AppendLine();

        // ── Adherence streak ────────────────────────────────────────
        try
        {
            var streak = await _sessionLog.GetAdherenceStreakAsync().ConfigureAwait(false);
            sb.AppendLine($"Schedule Adherence Streak: {streak} clean play-day(s)");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Context: adherence streak failed");
            sb.AppendLine("Schedule Adherence Streak: (unavailable)");
        }
        sb.AppendLine();

        // ── Mental-winrate correlation ──────────────────────────────
        sb.AppendLine("--- MENTAL STATE vs WINRATE ---");
        try
        {
            var correlations = await _sessionLog.GetMentalWinrateCorrelationAsync().ConfigureAwait(false);
            if (correlations.Count > 0)
            {
                foreach (var c in correlations)
                {
                    sb.AppendLine($"  Mental {c.Bracket}: {c.Games} games, {c.Wins} wins, {c.Winrate}% WR");
                }
            }
            else
            {
                sb.AppendLine("  No data yet.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Context: mental correlation failed");
            sb.AppendLine("  No data yet.");
        }
        sb.AppendLine();

        // ── Last 7 days of session logs ─────────────────────────────
        sb.AppendLine("--- LAST 7 DAYS ---");
        try
        {
            var summaries = await _sessionLog.GetDailySummariesAsync(7).ConfigureAwait(false);
            if (summaries.Count > 0)
            {
                foreach (var day in summaries)
                {
                    var broke = day.RuleBreaks > 0 ? " [RULE BROKEN]" : "";
                    sb.Append($"  {day.Date}: {day.Games}G ");
                    sb.Append($"{day.Wins}W-{day.Losses}L  ");
                    sb.Append($"avg mental {day.AvgMental}");
                    sb.AppendLine(broke);

                    if (!string.IsNullOrEmpty(day.ChampionsPlayed))
                    {
                        sb.AppendLine($"    Champions: {day.ChampionsPlayed}");
                    }
                }
            }
            else
            {
                sb.AppendLine("  No session data yet.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Context: daily summaries failed");
            sb.AppendLine("  No session data yet.");
        }
        sb.AppendLine();

        // ── Detailed game log ───────────────────────────────────────
        sb.AppendLine("--- DETAILED GAME LOG ---");
        try
        {
            var entries = await _sessionLog.GetRangeAsync(7).ConfigureAwait(false);
            if (entries.Count > 0)
            {
                string currentDate = "";
                foreach (var e in entries)
                {
                    if (e.Date != currentDate)
                    {
                        currentDate = e.Date;
                        sb.AppendLine();
                        sb.AppendLine($"  [{currentDate}]");
                    }

                    var result = e.Win ? "W" : "L";
                    var broke = e.RuleBroken != 0 ? " **RULE BREAK**" : "";
                    var noteStr = !string.IsNullOrWhiteSpace(e.ImprovementNote)
                        ? $" \u2014 \"{e.ImprovementNote.Trim()}\""
                        : "";

                    sb.AppendLine($"    {e.ChampionName} {result} (mental: {e.MentalRating}/10){broke}{noteStr}");

                    if (!string.IsNullOrWhiteSpace(e.MentalHandled))
                    {
                        sb.AppendLine($"      Mental handled: \"{e.MentalHandled.Trim()}\"");
                    }

                    // Fetch full game data for review details
                    if (e.GameId.HasValue)
                    {
                        var game = await _games.GetAsync(e.GameId.Value).ConfigureAwait(false);
                        if (game is not null)
                        {
                            AppendGameDetails(sb, game);
                        }
                    }
                }
            }
            else
            {
                sb.AppendLine("  No games logged yet.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Context: detailed game log failed");
            sb.AppendLine("  No games logged yet.");
        }
        sb.AppendLine();

        // ── Persistent notes ────────────────────────────────────────
        sb.AppendLine("--- MY NOTES / PATTERNS ---");
        try
        {
            var notesContent = await _notes.GetAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(notesContent))
            {
                foreach (var line in notesContent.Trim().Split('\n'))
                {
                    sb.AppendLine($"  {line}");
                }
            }
            else
            {
                sb.AppendLine("  (No persistent notes set yet)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Context: persistent notes failed");
            sb.AppendLine("  (No persistent notes set yet)");
        }
        sb.AppendLine();

        sb.AppendLine(Separator);
        sb.AppendLine("Hold me accountable. Call out patterns you see.");
        sb.AppendLine(Separator);

        return sb.ToString();
    }

    // ── Private helpers ─────────────────────────────────────────────

    /// <summary>
    /// Append performance stats and review details from the full game record.
    /// Includes KDA, CS/min, vision, damage, plus review notes/mistakes/went well/focus.
    /// </summary>
    private static void AppendGameDetails(StringBuilder sb, Models.GameStats game)
    {
        // Performance stats line: KDA, CS/min, vision, damage
        var kda = $"{game.Kills}/{game.Deaths}/{game.Assists}";
        var kdaRatio = game.Deaths > 0
            ? ((double)(game.Kills + game.Assists) / game.Deaths).ToString("F2")
            : "Perfect";
        var csMin = game.CsPerMin.ToString("F1");
        var vision = game.VisionScore;
        var dmg = game.TotalDamageToChampions;

        sb.AppendLine($"      KDA: {kda} ({kdaRatio})  CS/min: {csMin}  Vision: {vision}  Dmg: {dmg:N0}");

        // Review notes
        var reviewNotes = GetStringField(game, "ReviewNotes");
        if (!string.IsNullOrWhiteSpace(reviewNotes))
            sb.AppendLine($"      Notes: \"{reviewNotes.Trim()}\"");

        // Review fields: mistakes, went well, focus next
        var mistakes = GetStringField(game, "Mistakes");
        var wentWell = GetStringField(game, "WentWell");
        var focusNext = GetStringField(game, "FocusNext");

        if (!string.IsNullOrWhiteSpace(mistakes))
            sb.AppendLine($"      Mistakes: \"{mistakes.Trim()}\"");
        if (!string.IsNullOrWhiteSpace(wentWell))
            sb.AppendLine($"      Went well: \"{wentWell.Trim()}\"");
        if (!string.IsNullOrWhiteSpace(focusNext))
            sb.AppendLine($"      Focus next: \"{focusNext.Trim()}\"");
    }

    /// <summary>
    /// Get a string field from the game model, checking the dedicated property first
    /// then falling back to the RawStats dict.
    /// </summary>
    private static string GetStringField(Models.GameStats game, string propertyName)
    {
        // Check dedicated properties first
        var value = propertyName switch
        {
            "Mistakes" => game.Mistakes,
            "WentWell" => game.WentWell,
            "FocusNext" => game.FocusNext,
            "ReviewNotes" => game.ReviewNotes,
            _ => ""
        };

        if (!string.IsNullOrWhiteSpace(value))
            return value;

        // Fall back to RawStats dict with snake_case keys
        var snakeKey = propertyName switch
        {
            "Mistakes" => "mistakes",
            "WentWell" => "went_well",
            "FocusNext" => "focus_next",
            "ReviewNotes" => "review_notes",
            _ => propertyName.ToLowerInvariant()
        };

        if (game.RawStats.TryGetValue(snakeKey, out var val) && val is string s)
            return s;
        return "";
    }
}

#nullable enable

using System.Text.Json;
using Revu.Core.Constants;
using Revu.Core.Data;
using Revu.Core.Models;
using Revu.Core.Services;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Lcu;

public sealed class GameEndCaptureService : IGameEndCaptureService
{
    private static readonly bool DiagnosticDumpEnabled =
        string.Equals(
            Environment.GetEnvironmentVariable("LOLREVIEW_DIAG_LOGS"),
            "1",
            StringComparison.Ordinal);

    private readonly ILcuClient _lcuClient;
    private readonly ILogger<GameEndCaptureService> _logger;

    public GameEndCaptureService(
        ILcuClient lcuClient,
        ILogger<GameEndCaptureService> logger)
    {
        _lcuClient = lcuClient;
        _logger = logger;
    }

    public async Task<GameStats?> CaptureAsync(
        IReadOnlyList<GameEvent> liveEvents,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < GameConstants.EogStatsRetryAttempts; attempt++)
        {
            CoreDiagnostics.WriteVerbose(
                $"LCU: GameEndCapture attempt={attempt + 1}/{GameConstants.EogStatsRetryAttempts}");
            var eogData = await _lcuClient.GetEndOfGameStatsAsync(cancellationToken).ConfigureAwait(false);
            if (eogData is JsonElement eog)
            {
                // Dump full EOG JSON only when verbose diagnostics are enabled
                // (LOLREVIEW_DIAG_LOGS=1), mirroring the CoreDiagnostics gating
                // pattern. Skipped in normal runs to avoid disk I/O on every game.
                if (attempt == 0 && DiagnosticDumpEnabled)
                {
                    try
                    {
                        var raw = eog.GetRawText();
                        var dumpPath = Path.Combine(AppDataPaths.UserDataRoot, "last_eog_dump.json");
                        await File.WriteAllTextAsync(dumpPath, raw, cancellationToken).ConfigureAwait(false);
                    }
                    catch { /* best-effort diagnostic */ }
                }

                var stats = StatsExtractor.ExtractFromEog(eog, _logger);
                if (stats is not null)
                {
                    var summonerName = await TryGetCurrentSummonerNameAsync(cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(summonerName))
                    {
                        stats.SummonerName = summonerName;
                    }

                    stats.LiveEvents = [.. liveEvents];

                    // Stamp jungle-gank deaths: resolve the enemy jungler's summoner
                    // name(s) from the EOG (role → summoner), then flag any laning-phase
                    // DEATH where that jungler was the killer or an assister. Best-effort —
                    // when the jungler can't be resolved the classifier no-ops.
                    try
                    {
                        var junglers = ResolveEnemyJunglerNames(eog, stats.TeamId);
                        var stamped = JungleGankClassifier.Stamp(stats.LiveEvents, junglers);
                        if (stamped > 0)
                            CoreDiagnostics.WriteVerbose($"LCU: flagged {stamped} jungle-gank death(s)");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Jungle-gank classification failed (non-fatal)");
                    }

                    CoreDiagnostics.WriteVerbose(
                        $"LCU: GameEndCapture success gameId={stats.GameId} attempt={attempt + 1}");
                    return stats;
                }
            }

            if (attempt + 1 < GameConstants.EogStatsRetryAttempts)
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(GameConstants.EogStatsRetryDelayS),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogWarning("Could not retrieve end-of-game stats after retries");
        CoreDiagnostics.WriteVerbose("LCU: GameEndCapture failed after retries");
        return null;
    }

    /// <summary>
    /// The enemy jungler's summoner name(s) from EOG data, for jungle-gank stamping.
    /// Prefers an explicit <c>selectedPosition == "JUNGLE"</c>; when positions are blank
    /// (some queues), falls back to inferring the jungle champion from the enemy comp
    /// (<see cref="RoleAssignment"/>) and returning the summoner who plays it. Returns an
    /// empty list when the enemy team or its jungler can't be resolved — the classifier
    /// then no-ops rather than guessing. Usually one name; a list keeps it robust to
    /// duplicate-position oddities.
    /// </summary>
    internal static IReadOnlyList<string> ResolveEnemyJunglerNames(JsonElement eog, int myTeamId)
    {
        if (eog.ValueKind != JsonValueKind.Object) return Array.Empty<string>();
        if (!eog.TryGetProperty("teams", out var teams) || teams.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        // Enemy roster: (candidate name forms, championName, selectedPosition). We keep
        // EVERY name form a player exposes (Riot-ID game name, legacy summoner name, full
        // riotId) because the kill-feed we match against uses the Riot-ID game name, NOT
        // the legacy summonerName — comparing only summonerName would never match on a
        // renamed account. See [[bug_puuid_lcu_vs_riot_scoping]]: LCU vs Riot identity
        // formats diverge and silently break string compares.
        var enemy = new List<(IReadOnlyList<string> Names, string Champ, string Pos)>();
        foreach (var team in teams.EnumerateArray())
        {
            var teamId = team.GetPropertyIntOrDefault("teamId", 0);
            if (teamId == myTeamId) continue; // enemy team only
            if (!team.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var p in players.EnumerateArray())
            {
                var names = PlayerNameForms(p);
                if (names.Count == 0) continue;
                enemy.Add((
                    names,
                    p.GetPropertyOrDefault("championName", ""),
                    p.GetPropertyOrDefault("selectedPosition", "")));
            }
        }
        if (enemy.Count == 0) return Array.Empty<string>();

        // 1) Explicit position is authoritative when LCU filled it in.
        var explicitJg = enemy
            .Where(e => string.Equals(e.Pos, "JUNGLE", StringComparison.OrdinalIgnoreCase))
            .SelectMany(e => e.Names)
            .ToList();
        if (explicitJg.Count > 0) return explicitJg;

        // 2) Fallback: infer the jungle champion from the comp, map back to its player.
        var champs = enemy.Select(e => e.Champ).ToList();
        var byRole = RoleAssignment.AssignRoles(champs); // index 1 == Jungle
        var jgChamp = byRole.Length > ChampionRolePriors.Jungle ? byRole[ChampionRolePriors.Jungle] : "";
        if (string.IsNullOrWhiteSpace(jgChamp)) return Array.Empty<string>();
        return enemy
            .Where(e => string.Equals(e.Champ, jgChamp, StringComparison.OrdinalIgnoreCase))
            .SelectMany(e => e.Names)
            .ToList();
    }

    /// <summary>
    /// Every distinct name form a player exposes, so a kill-feed name (which uses the
    /// Riot-ID game name) can match regardless of which form the EOG happens to carry.
    /// Mirrors <see cref="LiveEventApi.ResolveActivePlayerName"/>'s field precedence
    /// (riotIdGameName → summonerName → riotId) but collects ALL of them, plus the
    /// game-name portion of a "gameName#tag" riotId. Empty when the player is nameless.
    /// </summary>
    private static IReadOnlyList<string> PlayerNameForms(JsonElement p)
    {
        var forms = new List<string>();
        void Add(string raw)
        {
            var s = raw?.Trim() ?? "";
            if (s.Length == 0) return;
            if (!forms.Any(f => string.Equals(f, s, StringComparison.OrdinalIgnoreCase))) forms.Add(s);
            // A "gameName#tagLine" also matches as its bare game-name half (the kill-feed
            // usually omits the tag).
            var hash = s.IndexOf('#');
            if (hash > 0)
            {
                var bare = s[..hash].Trim();
                if (bare.Length > 0 && !forms.Any(f => string.Equals(f, bare, StringComparison.OrdinalIgnoreCase)))
                    forms.Add(bare);
            }
        }
        Add(p.GetPropertyOrDefault("riotIdGameName", ""));
        Add(p.GetPropertyOrDefault("summonerName", ""));
        Add(p.GetPropertyOrDefault("riotId", ""));
        Add(p.GetPropertyOrDefault("gameName", ""));
        return forms;
    }

    private async Task<string?> TryGetCurrentSummonerNameAsync(CancellationToken cancellationToken)
    {
        try
        {
            var summoner = await _lcuClient.GetCurrentSummonerAsync(cancellationToken).ConfigureAwait(false);
            if (summoner is not JsonElement summonerElement)
            {
                return null;
            }

            return summonerElement.GetPropertyOrDefault("displayName", "") is { Length: > 0 } displayName
                ? displayName
                : summonerElement.GetPropertyOrDefault("gameName", "Unknown");
        }
        catch
        {
            return null;
        }
    }
}

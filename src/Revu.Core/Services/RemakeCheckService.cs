#nullable enable

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Revu.Core.Data.Repositories;
using Revu.Core.Lcu;

namespace Revu.Core.Services;

/// <summary>One verdict the startup remake check changed (publish it so pages refresh).</summary>
public sealed record RemakeChange(long GameId, bool IsRemake);

/// <summary>Outcome of <see cref="RemakeCheckService.RunBackfillAsync"/>. Unknown games had
/// no flag in any source. They are retried next startup, unless both the client and
/// Match-V5 were asked: then an <c>unknown</c> row stops the sweep from asking again.</summary>
public sealed record RemakeBackfillResult(
    int Checked, int Marked, int Unknown, bool LcuConnected, IReadOnlyList<RemakeChange> Changes);

/// <summary>
/// 3.14.1: confirms and backfills remake verdicts from League's own early-surrender flag.
/// Only Riot's flag ever marks a remake; the duration cap only picks which games are looked
/// up. A recorded negative (is_remake=0) is authoritative too, so the sweep converges to no
/// work. Sources, in order: the local client's match history (lcu), then Match-V5. Every
/// lookup is by game id, so only the player's own participant is read (never a stranger's).
/// </summary>
public sealed class RemakeCheckService
{
    /// <summary>Games at or past this length are never looked up by the sweep.</summary>
    public const int CandidateMaxDurationS = 600;
    public const int DefaultBackfillLimit = 25;
    private static readonly TimeSpan MatchV5Throttle = TimeSpan.FromMilliseconds(600);

    private readonly IGameRepository _games;
    private readonly ILcuClient? _lcu;
    private readonly IRiotMatchClient _matchClient;
    private readonly IConfigService _config;
    private readonly ILogger<RemakeCheckService> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public RemakeCheckService(
        IGameRepository games,
        ILcuClient? lcu,
        IRiotMatchClient matchClient,
        IConfigService config,
        ILogger<RemakeCheckService> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _games = games;
        _lcu = lcu;
        _matchClient = matchClient;
        _config = config;
        _logger = logger;
        _delay = delay ?? (static (span, token) => Task.Delay(span, token));
    }

    private bool HasRiotSession =>
        _config.HasValidRiotSession && !string.IsNullOrWhiteSpace(_config.RiotRegion);

    /// <summary>Games the sweep would look at (0 means: no backup, no write, no calls).</summary>
    public async Task<int> PendingCountAsync(int limit = DefaultBackfillLimit) =>
        (await _games.GetRemakeCandidatesAsync(CandidateMaxDurationS, limit).ConfigureAwait(false)).Count;

    /// <summary>
    /// Post-game confirmation (about 90 s after the game). The connected client's match
    /// history first (no Riot session needed), then Match-V5 when signed in. Null when
    /// nothing was written: the player's own or a Match-V5 verdict already stands, a long
    /// game already has a capture verdict, or no source had the flag.
    /// </summary>
    public async Task<RemakeWriteResult?> ConfirmAsync(long gameId, int duration, CancellationToken ct = default)
    {
        if (gameId <= 0) return null;
        var existing = await _games.GetRemakeVerdictAsync(gameId).ConfigureAwait(false);
        if (existing is { Source: RemakeSql.SourceUser or RemakeSql.SourceMatchV5 }) return null;
        if (duration >= CandidateMaxDurationS && existing is not null) return null;

        bool? flag = null;
        var source = RemakeSql.SourceLcu;
        if (await IsLcuConnectedAsync(ct).ConfigureAwait(false))
            flag = await FromLcuAsync(gameId, await LcuPuuidAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        if (flag is null && HasRiotSession)
        {
            flag = await FromMatchV5Async(gameId, ct).ConfigureAwait(false);
            source = RemakeSql.SourceMatchV5;
        }
        if (flag is not bool isRemake) return null;
        var result = await _games.SetRemakeVerdictAsync(gameId, isRemake, false, source).ConfigureAwait(false);
        _logger.LogInformation("Remake check: {Source} says game {GameId} {Verdict} (changed={Changed})",
            source, gameId, isRemake ? "was a remake" : "was not a remake", result.Changed);
        return result;
    }

    /// <summary>
    /// Startup sweep over short visible ranked games with no verdict: the local client's
    /// match history first (when connected), then Match-V5 (when signed in, throttled).
    /// The first non-null flag, true or false, is recorded with its source. When both
    /// sources were asked and neither had it, an <c>unknown</c> row (never a remake) ends
    /// the retries. <paramref name="beforeFirstWrite"/> runs once, right before the first
    /// write (the session backup), so a sweep that writes nothing takes no backup.
    /// </summary>
    public async Task<RemakeBackfillResult> RunBackfillAsync(
        int limit = DefaultBackfillLimit, Func<Task>? beforeFirstWrite = null, CancellationToken ct = default)
    {
        var candidates = await _games.GetRemakeCandidatesAsync(CandidateMaxDurationS, limit).ConfigureAwait(false);
        var lcuConnected = candidates.Count > 0 && await IsLcuConnectedAsync(ct).ConfigureAwait(false);
        var session = HasRiotSession;
        if (candidates.Count == 0 || (!lcuConnected && !session))
            return new RemakeBackfillResult(0, 0, candidates.Count, lcuConnected, []);

        var lcuPuuid = lcuConnected ? await LcuPuuidAsync(ct).ConfigureAwait(false) : "";
        int checkedCount = 0, marked = 0, unknown = 0, matchV5Calls = 0;
        var wrote = false;
        var changes = new List<RemakeChange>();
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            checkedCount++;
            bool? flag = null;
            var source = RemakeSql.SourceLcu;
            if (lcuConnected)
            {
                flag = await FromLcuAsync(candidate.GameId, lcuPuuid, ct).ConfigureAwait(false);
            }
            if (flag is null && session)
            {
                if (matchV5Calls++ > 0) await _delay(MatchV5Throttle, ct).ConfigureAwait(false);
                flag = await FromMatchV5Async(candidate.GameId, ct).ConfigureAwait(false);
                source = RemakeSql.SourceMatchV5;
            }
            bool isRemake;
            if (flag is bool known)
            {
                isRemake = known;
            }
            else
            {
                unknown++;
                if (!lcuConnected || !session) continue; // a source was not asked: retry next startup
                isRemake = false;
                source = RemakeSql.SourceUnknown;
            }

            if (!wrote && beforeFirstWrite is not null) await beforeFirstWrite().ConfigureAwait(false);
            wrote = true;
            var result = await _games.SetRemakeVerdictAsync(candidate.GameId, isRemake, false, source).ConfigureAwait(false);
            if (isRemake && result.Applied) marked++;
            if (result.Changed) changes.Add(new RemakeChange(candidate.GameId, isRemake));
        }

        _logger.LogInformation(
            "Remake check: {Checked} checked, {Marked} remake(s), {Unknown} unknown (client connected: {Lcu})",
            checkedCount, marked, unknown, lcuConnected);
        return new RemakeBackfillResult(checkedCount, marked, unknown, lcuConnected, changes);
    }

    private async Task<bool> IsLcuConnectedAsync(CancellationToken ct)
    {
        if (_lcu is null) return false;
        try { return await _lcu.IsConnectedAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }

    // The client's own puuid (UUID-shaped), which its match history uses; the signed-in
    // Riot puuid is a different id for the same account.
    private async Task<string> LcuPuuidAsync(CancellationToken ct)
    {
        if (_lcu is null) return "";
        try
        {
            var summoner = await _lcu.GetCurrentSummonerAsync(ct).ConfigureAwait(false);
            return summoner is JsonElement s && s.ValueKind == JsonValueKind.Object
                && s.TryGetProperty("puuid", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() ?? "" : "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Remake check: could not read the client's own puuid");
            return "";
        }
    }

    private async Task<bool?> FromLcuAsync(long gameId, string lcuPuuid, CancellationToken ct)
    {
        if (_lcu is null) return null;
        try
        {
            var details = await _lcu.GetMatchDetailsAsync(gameId, ct).ConfigureAwait(false);
            if (details is not JsonElement game) return null;
            var flag = RemakeSignals.FromLcuMatch(game, lcuPuuid, requireSelf: true);
            if (flag is null && !string.Equals(lcuPuuid, _config.RiotPuuid, StringComparison.Ordinal))
                flag = RemakeSignals.FromLcuMatch(game, _config.RiotPuuid, requireSelf: true);
            return flag;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Remake check: client match history failed for game {GameId}", gameId);
            return null;
        }
    }

    private async Task<bool?> FromMatchV5Async(long gameId, CancellationToken ct)
    {
        var region = _config.RiotRegion;
        if (string.IsNullOrWhiteSpace(region)) return null;
        try
        {
            var doc = await _matchClient.GetMatchAsync($"{region.ToUpperInvariant()}_{gameId}", region, ct)
                .ConfigureAwait(false);
            return doc is JsonElement match ? RemakeSignals.FromMatchV5(match, _config.RiotPuuid, requireSelf: true) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Remake check: Match-V5 lookup failed for game {GameId}", gameId);
            return null;
        }
    }
}

using Revu.Core.Data.Repositories;
using Revu.Core.Lcu;
using Revu.Core.Services;

namespace Revu.Sidecar;

public sealed record RecentGameRecoveryResult(bool Ok, string Message, int Recovered = 0,
    int Matched = 0, int RecordingCount = 0);

/// <summary>
/// Explicit recovery reads trusted League history, then links recordings to saved identities.
/// Each game is saved by <c>saveGame</c>: the game-flow coordinator's
/// <see cref="SidecarGameFlowCoordinator.SaveRecoveredGameAsync"/>, the same path and gate as
/// Home's Save, so a waiting or tracked game keeps its live timeline and pre-game choices. It
/// answers whether that call saved the game, and throws when the save failed.
/// </summary>
public sealed class RecentGameRecovery(
    ILcuClient lcu, IMatchHistoryReconciliationService history,
    Func<MissedGameCandidate, CancellationToken, Task<bool>> saveGame,
    IGameRepository games, IConfigService config, IVodService scan, RecordingRegistrationService registrations,
    SidecarBackgroundWork work, Func<Task> ensureBackup, Action<string, object> publish,
    ILogger<RecentGameRecovery> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Accepted recovery belongs to the host, not an HTTP request. Closing the
    // Settings page cannot abandon a save; shutdown cancels reads and waits for
    // the current save before preventing the next one.
    public Task<RecentGameRecoveryResult> RecoverAsync()
    {
        var completion = new TaskCompletionSource<RecentGameRecoveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!work.TryRun("recover recent League games", async () =>
            completion.TrySetResult(await RunAsync())))
            completion.TrySetResult(new(false, "Revu is closing; recovery was not started."));
        return completion.Task;
    }

    private async Task<RecentGameRecoveryResult> RunAsync()
    {
        var entered = false;
        var recovered = new List<long>();
        try
        {
            await _gate.WaitAsync(work.Stopping);
            entered = true;
            work.Stopping.ThrowIfCancellationRequested();
            if (!await lcu.IsConnectedAsync(work.Stopping))
                return new(false, "Open League of Legends, sign in, and wait for Revu to connect, then try again.");

            var candidates = await history.FindMissedGamesAsync(checkGameSaved: null, work.Stopping);
            work.Stopping.ThrowIfCancellationRequested();
            // A disconnect during history lookup must not look like an empty,
            // successful recovery or start a recording scan with stale context.
            if (!await lcu.IsConnectedAsync(work.Stopping))
                return new(false, "League disconnected before recovery. Open the League client and try again.");

            var backedUp = false;
            foreach (var candidate in candidates.DistinctBy(c => c.GameId).OrderBy(c => c.Timestamp))
            {
                work.Stopping.ThrowIfCancellationRequested();
                // Recheck after discovery and inside our serialization barrier:
                // another recovery or live capture may have saved this game.
                if (await games.GetAsync(candidate.GameId) is not null) continue;
                if (!backedUp) { await ensureBackup(); backedUp = true; }
                work.Stopping.ThrowIfCancellationRequested();
                // The first backup can yield long enough for live capture to
                // finish. Do not re-log that game's session with default mood.
                if (await games.GetAsync(candidate.GameId) is not null) continue;
                if (await saveGame(candidate, work.Stopping)) recovered.Add(candidate.GameId);
            }

            var prefix = recovered.Count == 0
                ? "No matches were recovered from League's latest 10 matches. If a match is missing, wait for League's match history to load and try again."
                : $"Recovered {recovered.Count} {(recovered.Count == 1 ? "ranked game" : "ranked games")}.";
            work.Stopping.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace((await config.LoadAsync()).AscentFolder))
                return new(true, prefix + " Connect an Ascent recordings folder to link videos.", recovered.Count);

            if (!backedUp) await ensureBackup();
            work.Stopping.ThrowIfCancellationRequested();
            var scanned = await registrations.RunExternalScanAsync(
                reserved => scan.ScanAsync(work.Stopping, reserved), work.Stopping);
            foreach (var gameId in scanned.LinkedGameIds) publish("vodLinked", new { gameId });
            return new(scanned.Ok, prefix + " " + scanned.Message, recovered.Count, scanned.Matched, scanned.RecordingCount);
        }
        catch (OperationCanceledException) when (work.Stopping.IsCancellationRequested)
        {
            return new(false, $"Revu is closing; recovery stopped. {recovered.Count} recovered games and existing video links are kept.", recovered.Count);
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Recent game recovery failed after saving {Count} games", recovered.Count);
            return new(false, $"Couldn't finish recovery. {recovered.Count} recovered games are kept. Keep League open and try again.", recovered.Count);
        }
        finally
        {
            if (entered) _gate.Release();
            if (recovered.Count > 0)
            {
                try { publish("gamesRecovered", new { gameIds = recovered.ToArray() }); }
                catch (Exception error) { logger.LogWarning(error, "Could not announce recovered games"); }
            }
        }
    }
}

#nullable enable

using Microsoft.Extensions.Logging;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>
/// 3.14.1: one place that announces a changed remake verdict. Open pages refresh their
/// labels and counts (<c>remakeUpdated</c>), and a hard stop that only held because the
/// remake was counted is cleared (<see cref="HardStopEnforcer.ReevaluateAsync"/>).
/// </summary>
public static class RemakeNotifications
{
    public static async Task PublishChangedAsync(
        SidecarEventHub hub, HardStopEnforcer? hardStop, long gameId, bool isRemake, ILogger? logger = null)
    {
        hub.Publish("remakeUpdated", new { gameId, isRemake });
        if (hardStop is null) return;
        try
        {
            await hardStop.ReevaluateAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Hard stop re-evaluation after a remake change failed for game {GameId}", gameId);
        }
    }

    /// <summary>
    /// POST /api/review/remake: the player's own verdict (source <c>user</c>, sticky against
    /// every automatic source in both directions). Null when the game does not exist;
    /// otherwise the stored is_remake. Backs up before the first write of the session.
    /// </summary>
    public static async Task<bool?> SetByPlayerAsync(
        long gameId, bool isRemake, Revu.Core.Data.Repositories.IGameRepository games, Func<Task> ensureBackedUp,
        SidecarEventHub hub, HardStopEnforcer? hardStop, ILogger logger)
    {
        if (gameId <= 0 || await games.GetAsync(gameId).ConfigureAwait(false) is null) return null;
        await ensureBackedUp().ConfigureAwait(false);
        var result = await games.SetRemakeVerdictAsync(gameId, isRemake, false, "user").ConfigureAwait(false);
        if (result.Changed)
            await PublishChangedAsync(hub, hardStop, gameId, isRemake, logger).ConfigureAwait(false);
        logger.LogInformation("Remake verdict set by the player for game {GameId}: {IsRemake}", gameId, isRemake);
        return (await games.GetRemakeVerdictAsync(gameId).ConfigureAwait(false))?.IsRemake ?? isRemake;
    }

    /// <summary>
    /// The startup sweep (about 20 s after launch so the client can connect): skips when
    /// nothing is pending, checks up to <see cref="RemakeCheckService.DefaultBackfillLimit"/>
    /// games, backs up only right before its first write (never when no lookup could run),
    /// and retries once after two minutes when some were unknown and the client was not
    /// connected yet.
    /// </summary>
    public static async Task RunStartupCheckAsync(
        WriteServices write, SidecarEventHub hub, HardStopEnforcer? hardStop, CancellationToken stopping, ILogger logger)
    {
        var delays = new[] { TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(2) };
        foreach (var delay in delays)
        {
            await Task.Delay(delay, stopping).ConfigureAwait(false);
            if (await write.RemakeCheck.PendingCountAsync().ConfigureAwait(false) == 0) return;

            var result = await write.RemakeCheck.RunBackfillAsync(
                beforeFirstWrite: () => write.BackupGuard.EnsureBackedUpAsync(), ct: stopping).ConfigureAwait(false);
            foreach (var change in result.Changes)
                await PublishChangedAsync(hub, hardStop, change.GameId, change.IsRemake, logger).ConfigureAwait(false);

            if (result.Unknown == 0 || result.LcuConnected) return;
            logger.LogInformation("Startup remake check: {Unknown} game(s) unknown without the client; retrying once", result.Unknown);
        }
    }
}

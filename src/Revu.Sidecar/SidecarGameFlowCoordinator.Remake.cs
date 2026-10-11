#nullable enable

using Microsoft.Extensions.Logging;

namespace Revu.Sidecar;

public sealed partial class SidecarGameFlowCoordinator
{
    /// <summary>
    /// 3.14.1: confirm the just-ended game's remake verdict from Match-V5. True once a
    /// verdict was written (so the +5 min attempt skips it); false when Match-V5 had
    /// nothing yet or the check was skipped. A flip is announced and re-evaluates the lock.
    /// </summary>
    private async Task<bool> TryConfirmRemakeAsync(long gameId, int duration)
    {
        try
        {
            var result = await _write.RemakeCheck.ConfirmAsync(gameId, duration, _backgroundWork.Stopping).ConfigureAwait(false);
            if (result is null) return false;
            if (result.Changed)
            {
                var verdict = await _write.Games.GetRemakeVerdictAsync(gameId).ConfigureAwait(false);
                await RemakeNotifications.PublishChangedAsync(
                    _eventHub, _hardStop, gameId, verdict?.IsRemake == true, _logger).ConfigureAwait(false);
            }
            return true;
        }
        catch (OperationCanceledException) when (_backgroundWork.Stopping.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Post-game remake check failed for game {GameId} (startup check heals)", gameId);
            return false;
        }
    }
}

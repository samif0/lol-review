using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>Bounded external-recording retries, owned and cancelled by the sidecar's work tracker.</summary>
public static class AscentRecordingScan
{
    public static async Task RunWithRetryAsync(IVodService scan, IConfigService config, IVodRepository vods,
        SidecarBackgroundWork work, Func<Task> beforeWrite, Action<long> linked, ILogger logger,
        long? gameId = null, IReadOnlyList<TimeSpan>? delays = null,
        RecordingRegistrationService? registrations = null)
    {
        // Absolute attempt times: +30s / +90s / +5min. The first delay gives the
        // external encoder time to close and native receipts first opportunity.
        delays ??= [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(210)];
        foreach (var delay in delays)
        {
            work.Stopping.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace((await config.LoadAsync()).AscentFolder)) return;
            if (gameId is long id && await vods.GetVodAsync(id) is not null) return;
            await Task.Delay(delay, work.Stopping).ConfigureAwait(false);
            try
            {
                if (string.IsNullOrWhiteSpace((await config.LoadAsync()).AscentFolder)) return;
                await beforeWrite().ConfigureAwait(false);
                var result = registrations is null
                    ? await scan.ScanAsync(work.Stopping).ConfigureAwait(false)
                    : await registrations.RunExternalScanAsync(
                        reserved => scan.ScanAsync(work.Stopping, reserved), work.Stopping).ConfigureAwait(false);
                foreach (var linkedId in result.LinkedGameIds) linked(linkedId);
                if (gameId is long target && await vods.GetVodAsync(target) is not null) return;
            }
            catch (OperationCanceledException) when (work.Stopping.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "Ascent recording scan will retry on the next attempt"); }
        }
    }
}

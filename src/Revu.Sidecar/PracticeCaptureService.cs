using System.Text.Json;
using Revu.Core.Services.EventProcessing;

namespace Revu.Sidecar;

/// <summary>Opt-in local diagnostic; never writes game events or ranked history.</summary>
public sealed class PracticeCaptureService(IHttpClientFactory clients, ILogger<PracticeCaptureService> logger) : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _stop;
    private Task? _run;
    private CaptureStatus _status = new("idle", "", 0, 0, null, "", "");
    public sealed record CaptureStatus(string State, string Directory, int Samples, int Gaps,
        double? GameSeconds, string LastError, string Limitation);
    public CaptureStatus Status { get { lock (_gate) return _status; } }

    public CaptureStatus Arm()
    {
        lock (_gate)
        {
            if (_run is { IsCompleted: false }) return _status;
            _stop?.Dispose();
            _stop = new CancellationTokenSource(TimeSpan.FromHours(1));
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Revu", "diagnostics", $"practice-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(directory);
            _status = new("waiting-for-game", directory, 0, 0, null, "",
                "Riot Live Client capture only. Attributed champion damage and cast timing are not validated; health changes are not trades.");
            _run = Task.Run(() => CaptureAsync(directory, _stop.Token));
            return _status;
        }
    }

    public async Task<CaptureStatus> StopAsync()
    {
        Task? run;
        lock (_gate) { _stop?.Cancel(); run = _run; }
        if (run is not null) await run;
        return Status;
    }

    private async Task CaptureAsync(string directory, CancellationToken ct)
    {
        const string kind = "player.health";
        var session = CreateDiagnosticSession();
        var client = clients.CreateClient("LiveEventApi");
        var wall = System.Diagnostics.Stopwatch.StartNew();
        var cpuStart = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds;
        double? firstClock = null;
        int misses = 0;
        long bytes = 0;
        string endReason = "stopped";
        try
        {
            using var stream = new FileStream(Path.Combine(directory, "observations.jsonl"), FileMode.CreateNew,
                FileAccess.Write, FileShare.Read, 16384, FileOptions.Asynchronous);
            using var writer = new StreamWriter(stream);
            while (!ct.IsCancellationRequested)
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    using var response = await client.GetAsync("https://127.0.0.1:2999/liveclientdata/allgamedata",
                        HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    response.EnsureSuccessStatusCode();
                    await response.Content.LoadIntoBufferAsync(1024 * 1024).WaitAsync(timeout.Token);
                    var payload = await response.Content.ReadAsStringAsync(timeout.Token);
                    using var doc = JsonDocument.Parse(payload);
                    if (!TryHealth(doc.RootElement, out var seconds, out var health))
                        throw new InvalidDataException("Missing or invalid health/game clock");
                    if (firstClock is not null && seconds < Status.GameSeconds)
                    { endReason = "game-clock-reset"; break; }
                    firstClock ??= seconds;
                    misses = 0;
                    int sequence = Status.Samples + 1;
                    var received = DateTimeOffset.UtcNow;
                    var line = JsonSerializer.Serialize(new { version = 1, sequence, receivedUtc = received,
                        requestMilliseconds = started.Elapsed.TotalMilliseconds, source = "riot.live-client",
                        payload = doc.RootElement });
                    bytes += System.Text.Encoding.UTF8.GetByteCount(line) + 1;
                    if (bytes > 32 * 1024 * 1024) { endReason = "32-MB-capture-limit"; break; }
                    await writer.WriteLineAsync(line);
                    if (sequence % 5 == 0) await writer.FlushAsync();
                    session.Publish(new($"practice:{sequence}", "practice.live-client", 1, kind, 1, "local-player",
                        TimeSpan.FromSeconds(seconds), received, started.Elapsed, true, health));
                    lock (_gate) _status = _status with { State = "capturing", Samples = sequence, GameSeconds = seconds, LastError = "" };
                    if (sequence >= 900 || seconds - firstClock >= 900) { endReason = "15-minute-capture-limit"; break; }
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException)
                {
                    if (ct.IsCancellationRequested) break;
                    misses++;
                    if (firstClock is not null)
                    {
                        session.ReportGap(kind, "Practice source request failed or payload incompatible");
                        lock (_gate) _status = _status with { Gaps = _status.Gaps + 1, LastError = ex.GetType().Name };
                        await writer.WriteLineAsync(JsonSerializer.Serialize(new { version = 1, gap = true,
                            receivedUtc = DateTimeOffset.UtcNow, error = ex.GetType().Name }));
                        if (misses >= 10) { endReason = "source-disconnected"; break; }
                    }
                }
                var delay = (firstClock is null ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(1)) - started.Elapsed;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            endReason = "capture-failed";
            lock (_gate) _status = _status with { LastError = ex.GetType().Name };
            logger.LogWarning(ex, "Practice diagnostic capture failed");
        }
        finally
        {
            var report = await session.CompleteAsync(TimeSpan.FromSeconds(2));
            lock (_gate) _status = _status with { State = endReason };
            try
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "processing-report.json"), JsonSerializer.Serialize(report));
                // The full live report keeps its gaps. A separate, explicitly partial
                // replay preserves observable facts before the first disconnection.
                var prefix = await ReplayCapturedPrefixAsync(Path.Combine(directory, "observations.jsonl"));
                await File.WriteAllTextAsync(Path.Combine(directory, "captured-prefix-report.json"), JsonSerializer.Serialize(prefix));
                await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new
                {
                    status = Status, wallSeconds = wall.Elapsed.TotalSeconds, bytes,
                    sidecarCpuSeconds = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds - cpuStart,
                    performanceGate = "Not measured: sidecar CPU includes other app work; matched CPU/memory/frame-time tests still required",
                    sourceGate = "Unvalidated: raw snapshots and health changes do not establish champion exchanges"
                }));
            }
            catch (Exception ex) { logger.LogWarning(ex, "Could not persist practice diagnostic summary"); }
        }
    }

    private static ProcessingSession CreateDiagnosticSession(int capacity = 256) => new(
        new PayloadRegistry().Observation<PlayerHealth>("player.health", 1).Event<HealthChange>("DIAGNOSTIC_HEALTH_CHANGE"),
        [new("practice.live-client", 1, new Dictionary<string, TimeSpan> { ["player.health"] = TimeSpan.FromSeconds(3) })],
        [new HealthChangeDetector()], [new(0, "DIAGNOSTIC_HEALTH_CHANGE")], shadow: true, queueCapacity: capacity);

    public sealed record PrefixReplay(int Samples, double? LastGameSeconds, bool EndedAtGap,
        string Scope, ProcessingReport Report);

    public static async Task<PrefixReplay> ReplayCapturedPrefixAsync(string path)
    {
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("Capture exceeds replay byte limit");
        var session = CreateDiagnosticSession(900);
        int samples = 0;
        double? lastClock = null;
        bool gap = false;
        ProcessingReport report;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                using var doc = JsonDocument.Parse(line);
                var row = doc.RootElement;
                if (row.TryGetProperty("gap", out var missing) && missing.GetBoolean()) { gap = true; break; }
                if (row.GetProperty("version").GetInt32() != 1 || row.GetProperty("source").GetString() != "riot.live-client"
                    || !TryHealth(row.GetProperty("payload"), out var seconds, out var health))
                    throw new InvalidDataException("Unsupported diagnostic observation");
                if (++samples > 900) throw new InvalidDataException("Replay sample limit exceeded");
                var duration = row.GetProperty("requestMilliseconds").GetDouble();
                if (!double.IsFinite(duration) || duration < 0) throw new InvalidDataException("Invalid request duration");
                session.Publish(new($"practice:{samples}", "practice.live-client", 1, "player.health", 1,
                    "local-player", TimeSpan.FromSeconds(seconds), row.GetProperty("receivedUtc").GetDateTimeOffset(),
                    TimeSpan.FromMilliseconds(duration), true, health));
                lastClock = seconds;
            }
        }
        finally { report = await session.CompleteAsync(TimeSpan.FromSeconds(2)); }
        return new(samples, lastClock, gap, "Captured prefix only; no coverage after the first gap or last sample. Health changes are not trades.", report);
    }

    internal static bool TryHealth(JsonElement root, out double seconds, out PlayerHealth health)
    {
        seconds = 0; health = new(0, 0);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("gameData", out var game)
            || game.ValueKind != JsonValueKind.Object || !game.TryGetProperty("gameTime", out var clock)
            || clock.ValueKind != JsonValueKind.Number || !clock.TryGetDouble(out seconds) || !double.IsFinite(seconds) || seconds < 0
            || !root.TryGetProperty("activePlayer", out var player) || player.ValueKind != JsonValueKind.Object
            || !player.TryGetProperty("championStats", out var stats) || stats.ValueKind != JsonValueKind.Object
            || !stats.TryGetProperty("currentHealth", out var current) || current.ValueKind != JsonValueKind.Number
            || !stats.TryGetProperty("maxHealth", out var max) || max.ValueKind != JsonValueKind.Number
            || !current.TryGetDouble(out var hp) || !max.TryGetDouble(out var maximum)
            || !double.IsFinite(hp) || !double.IsFinite(maximum) || hp < 0 || maximum <= 0 || hp > maximum) return false;
        health = new(hp, maximum);
        return true;
    }

    public void Dispose() { lock (_gate) _stop?.Cancel(); }

    private sealed record HealthChange(double Before, double After) : IEventPayload;
    private sealed class HealthChangeDetector : IEventDetector
    {
        private double? _previous;
        private string? _previousId;
        public DetectorDefinition Definition { get; } = new("diagnostic-health-change", 1,
            [new("player.health", TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1))], ["DIAGNOSTIC_HEALTH_CHANGE"],
            TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(50), true, "");
        public IEnumerable<EventCandidate> Observe(Observation o, ObservationWindow history)
        {
            var health = (PlayerHealth)o.Payload;
            var before = _previous;
            var beforeId = _previousId;
            _previous = health.Current;
            _previousId = o.Id;
            return before is { } previous && beforeId is not null && Math.Abs(previous - health.Current) >= 1
                ? [new(o.Id, "DIAGNOSTIC_HEALTH_CHANGE", o.GameTime, o.GameTime, [beforeId, o.Id],
                    new HealthChange(previous, health.Current), VerificationStatus.Supported)] : [];
        }
        public IEnumerable<EventCandidate> Complete(ObservationWindow history) => [];
    }
}

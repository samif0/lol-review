using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Revu.Core.Models;

namespace Revu.Core.Services.EventProcessing;

/// <summary>
/// One match, one objective snapshot. Only trusted application detectors run here.
/// Callback deadlines isolate failures but cannot forcibly terminate arbitrary managed code.
/// </summary>
public sealed class ProcessingSession
{
    private sealed class State(IEventDetector detector, long[] objectives)
    {
        public readonly IEventDetector Detector = detector;
        public readonly long[] Objectives = objectives;
        public string Status = "active";
        public string Reason = "";
        public readonly Dictionary<string, GameEvent> Events = new(StringComparer.Ordinal);
        public int EvidenceBytes;
    }
    private readonly PayloadRegistry _registry;
    private readonly State[] _states;
    private readonly Dictionary<string, SourceCapabilities> _sources;
    private readonly Channel<Observation> _queue;
    private readonly ObservationWindow _history;
    private readonly Dictionary<(string Source, string Kind), TimeSpan> _last = [];
    private readonly Task _worker;
    private readonly bool _shadow;
    private readonly ObjectiveSubscription[] _subscriptions;
    private long _dropped;
    private int _closed;
    private readonly object _gate = new();
    private ProcessingReport? _report;
    public IReadOnlyDictionary<string, TimeSpan> SamplingPlan { get; }

    public ProcessingSession(PayloadRegistry registry, IEnumerable<SourceCapabilities> sources,
        IEnumerable<IEventDetector> detectors, IEnumerable<ObjectiveSubscription> subscriptions,
        bool shadow = true, int queueCapacity = 256, SharedFactRegistry? sharedFacts = null)
    {
        _registry = registry;
        _shadow = shadow;
        _sources = sources.ToDictionary(s => s.SourceId, s => s with { AvailableKinds = new Dictionary<string, TimeSpan>(s.AvailableKinds) });
        var snapshot = subscriptions.Distinct().ToArray();
        _subscriptions = snapshot;
        _states = detectors.Select(d => new State(d, snapshot
            .Where(s => d.Definition.OutputTokens.Contains(s.Token, StringComparer.OrdinalIgnoreCase))
            .Select(s => s.ObjectiveId).Distinct().Order().ToArray()))
            .Where(s => s.Objectives.Length > 0).ToArray();
        if (_states.Select(s => s.Detector.Definition.Id).Distinct().Count() != _states.Length)
            throw new ArgumentException("Duplicate detector IDs");
        if (_states.Length > 16) throw new ArgumentException("At most 16 detectors may be selected per match");
        foreach (var state in _states)
        {
            var definition = state.Detector.Definition;
            if (!definition.Enabled) Disable(state, "unavailable", definition.UnavailableReason);
            else if (definition.History < TimeSpan.Zero || definition.History > TimeSpan.FromMinutes(2)
                || definition.ProcessingBudget <= TimeSpan.Zero || definition.ProcessingBudget > TimeSpan.FromMilliseconds(50))
                Disable(state, "unavailable", "Detector exceeds bounded history/callback budget");
            else if (definition.Inputs.Any(i => !_sources.Values.Any(s =>
                s.AvailableKinds.TryGetValue(i.Kind, out var cadence) && cadence <= i.MaximumInterval)))
                Disable(state, "unavailable", "Required source capability or cadence unavailable");
        }
        SamplingPlan = _states.Where(s => s.Status == "active").SelectMany(s => s.Detector.Definition.Inputs)
            .GroupBy(i => i.Kind).ToDictionary(g => g.Key, g => g.Min(i => i.MaximumInterval));
        _history = new ObservationWindow(_states.Where(s => s.Status == "active")
            .Select(s => s.Detector.Definition.History).DefaultIfEmpty(TimeSpan.Zero).Max(), facts: sharedFacts);
        _queue = Channel.CreateBounded<Observation>(new BoundedChannelOptions(queueCapacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _worker = Task.Run(RunAsync);
    }

    public bool Publish(Observation observation)
    {
        if (Volatile.Read(ref _closed) != 0) return false;
        if (!SamplingPlan.ContainsKey(observation.Kind)) return true;
        try
        {
            if (!_registry.Accepts(observation) || observation.Id.Length > 256 || observation.SubjectId.Length > 256
                || PayloadRegistry.Serialize(observation.Payload).GetRawText().Length > 4096)
            { ReportGap(observation.Kind, "Unknown payload contract or observation byte budget exceeded"); return false; }
        }
        catch { ReportGap(observation.Kind, "Invalid observation payload"); return false; }
        if (_queue.Writer.TryWrite(observation)) return true;
        Interlocked.Increment(ref _dropped);
        ReportGap(observation.Kind, "Observation queue overflow");
        return false;
    }

    public void ReportGap(string kind, string reason)
    {
        lock (_gate)
            foreach (var state in _states.Where(s => s.Status == "active"
                && s.Detector.Definition.Inputs.Any(i => i.Kind == kind)))
                Disable(state, "incomplete", reason);
    }

    private async Task RunAsync()
    {
        await foreach (var observation in _queue.Reader.ReadAllAsync())
        {
            if (!_registry.Accepts(observation) || !_sources.TryGetValue(observation.SourceId, out var source)
                || source.Version != observation.SourceVersion || !source.AvailableKinds.ContainsKey(observation.Kind)
                || !observation.Complete || observation.GameTime < TimeSpan.Zero
                || observation.ClockUncertainty < TimeSpan.Zero || string.IsNullOrWhiteSpace(observation.Id))
            { ReportGap(observation.Kind, "Unsupported, incomplete or invalid observation"); continue; }
            var key = (observation.SourceId, observation.Kind);
            if (_last.TryGetValue(key, out var last) && observation.GameTime < last)
            { ReportGap(observation.Kind, "Clock regression"); continue; }
            lock (_gate)
                foreach (var state in _states.Where(s => s.Status == "active"))
                    foreach (var requirement in state.Detector.Definition.Inputs.Where(i => i.Kind == observation.Kind))
                        if (observation.ClockUncertainty > requirement.MaximumClockUncertainty
                            || (_last.TryGetValue(key, out last) && observation.GameTime - last > requirement.MaximumInterval))
                            Disable(state, "incomplete", "Sampling gap or clock uncertainty");
            _last[key] = observation.GameTime;
            if (!_history.Add(observation))
            { ReportGap(observation.Kind, "Shared history capacity exceeded"); continue; }
            var frozen = _history.Snapshot();
            foreach (var state in _states)
                if (state.Detector.Definition.Inputs.Any(i => i.Kind == observation.Kind))
                    await InvokeAsync(state, frozen, history => state.Detector.Observe(observation, history));
        }
        var finalHistory = _history.Snapshot();
        foreach (var state in _states) await InvokeAsync(state, finalHistory, history => state.Detector.Complete(history));
    }

    private async Task InvokeAsync(State state, ObservationWindow snapshot, Func<ObservationWindow, IEnumerable<EventCandidate>> callback)
    {
        lock (_gate) if (state.Status != "active" || _report is not null) return;
        // Snapshot isolates a timed-out callback from subsequent history mutations.
        try
        {
            var budget = state.Detector.Definition.ProcessingBudget;
            var work = Task.Run(() =>
            {
                var started = Stopwatch.GetTimestamp();
                var candidates = callback(snapshot).Take(65).ToArray();
                return (Candidates: candidates, Elapsed: Stopwatch.GetElapsedTime(started));
            });
            (EventCandidate[] Candidates, TimeSpan Elapsed) result;
            try { result = await work.WaitAsync(budget); }
            catch (TimeoutException)
            {
                _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                lock (_gate) Disable(state, "disabled", "Callback deadline exceeded");
                return;
            }
            lock (_gate)
            {
                if (state.Status != "active" || _report is not null) return;
                // Timer callbacks can be delayed under thread-pool pressure. A late
                // result must not become valid merely by winning the WaitAsync race.
                if (result.Elapsed > budget) { Disable(state, "disabled", "Callback deadline exceeded"); return; }
                var candidates = result.Candidates;
                if (candidates.Length > 64) { Disable(state, "disabled", "Candidate batch limit exceeded"); return; }
                foreach (var candidate in candidates)
                {
                    if (candidate.Verification != VerificationStatus.Supported) continue;
                    if (!_registry.Accepts(candidate) || !state.Detector.Definition.OutputTokens.Contains(candidate.EventType, StringComparer.OrdinalIgnoreCase)
                        || string.IsNullOrWhiteSpace(candidate.Key) || candidate.Key.Length > 256
                        || candidate.Start < TimeSpan.Zero || candidate.End < candidate.Start || candidate.End.TotalSeconds > int.MaxValue
                        || candidate.EvidenceIds.Count == 0 || candidate.EvidenceIds.Count > 64
                        || candidate.EvidenceIds.Any(id => _history.Find(id) is null)
                        || state.Detector.Definition.Inputs.Any(input => !candidate.EvidenceIds.Any(id => _history.Find(id)?.Kind == input.Kind)))
                    { Disable(state, "disabled", "Invalid candidate contract or missing evidence"); break; }
                    if (state.Events.Count >= 512 && !state.Events.ContainsKey(candidate.Key))
                    { Disable(state, "disabled", "Match candidate capacity exceeded"); break; }
                    var tokens = candidate.TrackedTokens ?? [candidate.EventType];
                    if (!tokens.Contains(candidate.EventType, StringComparer.OrdinalIgnoreCase)
                        || tokens.Any(t => !state.Detector.Definition.OutputTokens.Contains(t, StringComparer.OrdinalIgnoreCase)))
                    { Disable(state, "disabled", "Undeclared output token"); break; }
                    var objectiveIds = _subscriptions.Where(s => tokens.Contains(s.Token, StringComparer.OrdinalIgnoreCase))
                        .Select(s => s.ObjectiveId).Distinct().Order().ToArray();
                    var details = JsonSerializer.Serialize(new
                    {
                        processing = new { version = 1, detector = state.Detector.Definition.Id,
                            detectorVersion = state.Detector.Definition.Version, verification = "supported", shadow = _shadow,
                            payloadVersion = candidate.PayloadVersion,
                            objectiveIds, tokens,
                            evidence = candidate.EvidenceIds.Select(id => _history.Find(id)!).Select(o => new
                            { o.Id, o.SourceId, o.SourceVersion, o.Kind, o.PayloadVersion, o.SubjectId,
                                gameTimeMs = o.GameTime.TotalMilliseconds, o.ReceivedAt,
                                clockUncertaintyMs = o.ClockUncertainty.TotalMilliseconds, payload = PayloadRegistry.Serialize(o.Payload) }) },
                        start_s = (int)candidate.Start.TotalSeconds, end_s = (int)candidate.End.TotalSeconds,
                        attributes = PayloadRegistry.Serialize(candidate.Payload)
                    });
                    var bytes = details.Length * sizeof(char);
                    var previousBytes = state.Events.TryGetValue(candidate.Key, out var previous) ? previous.Details.Length * sizeof(char) : 0;
                    if (bytes > 32768 || _states.Sum(s => s.EvidenceBytes) + bytes - previousBytes > 8 * 1024 * 1024)
                    { Disable(state, "disabled", "Evidence byte budget exceeded"); break; }
                    state.EvidenceBytes += bytes - previousBytes;
                    state.Events[candidate.Key] = new GameEvent { EventType = candidate.EventType,
                        GameTimeS = (int)candidate.Start.TotalSeconds, Details = details,
                        EventKey = $"stream:{state.Detector.Definition.Id}:{state.Detector.Definition.Version}:{candidate.Key}" };
                }
            }
        }
        catch (Exception ex) { lock (_gate) Disable(state, "disabled", $"Detector fault: {ex.GetType().Name}"); }
    }

    private static void Disable(State state, string status, string reason)
    { state.Status = status; state.Reason = reason; state.Events.Clear(); state.EvidenceBytes = 0; }

    public async Task<ProcessingReport> CompleteAsync(TimeSpan? budget = null)
    {
        var timer = Stopwatch.StartNew();
        Interlocked.Exchange(ref _closed, 1);
        _queue.Writer.TryComplete();
        try { await _worker.WaitAsync(budget ?? TimeSpan.FromSeconds(1)); }
        catch (TimeoutException)
        {
            lock (_gate) foreach (var state in _states.Where(s => s.Status == "active"))
                Disable(state, "incomplete", "Finalization deadline exceeded");
        }
        lock (_gate)
            return _report ??= new ProcessingReport(1, _shadow, Interlocked.Read(ref _dropped), timer.Elapsed.TotalMilliseconds,
                _states.Select(s => new DetectorCoverage(s.Detector.Definition.Id, s.Status, s.Reason)).ToArray(),
                _states.Where(s => s.Status == "active").SelectMany(s => s.Events.Values).OrderBy(e => e.GameTimeS).ToArray(),
                _sources.Values.ToArray(), _subscriptions);
    }
}

using System.Text.Json;

namespace Revu.Core.Services.EventProcessing;

public interface IObservationPayload { }
public interface IEventPayload { }
public interface ISharedFact { }
public sealed record PlayerHealth(double Current, double Maximum) : IObservationPayload;
public sealed record FeedEvent(string Type, string Details) : IObservationPayload;
public sealed record SourceCapabilities(string SourceId, int Version,
    IReadOnlyDictionary<string, TimeSpan> AvailableKinds, string UnavailableReason = "");

/// <summary>Push-source adapter. Sampling requirements are fixed for one match.</summary>
public interface IObservationSource
{
    SourceCapabilities Capabilities { get; }
    Task StartAsync(CancellationToken cancellationToken);
}
public sealed record Observation(string Id, string SourceId, int SourceVersion, string Kind,
    int PayloadVersion, string SubjectId, TimeSpan GameTime, DateTimeOffset ReceivedAt,
    TimeSpan ClockUncertainty, bool Complete, IObservationPayload Payload);
public sealed record InputRequirement(string Kind, TimeSpan MaximumInterval, TimeSpan MaximumClockUncertainty);
public sealed record DetectorDefinition(string Id, int Version, IReadOnlyList<InputRequirement> Inputs,
    IReadOnlyList<string> OutputTokens, TimeSpan History, TimeSpan ProcessingBudget,
    bool Enabled = false, string UnavailableReason = "Awaiting source and accuracy validation");
public sealed record ObjectiveSubscription(long ObjectiveId, string Token);
public enum VerificationStatus { Unverified, Supported }
public sealed record EventCandidate(string Key, string EventType, TimeSpan Start, TimeSpan End,
    IReadOnlyList<string> EvidenceIds, IEventPayload Payload, VerificationStatus Verification,
    IReadOnlyList<string>? TrackedTokens = null, int PayloadVersion = 1);
public sealed record DetectorCoverage(string DetectorId, string Status, string Reason);
public sealed record ProcessingReport(int Version, bool Shadow, long DroppedObservations,
    double FinalizationMilliseconds, IReadOnlyList<DetectorCoverage> Coverage,
    IReadOnlyList<Models.GameEvent> Events,
    IReadOnlyList<SourceCapabilities>? Sources = null,
    IReadOnlyList<ObjectiveSubscription>? Subscriptions = null,
    ProcessingReport? Recovery = null);

/// <summary>Code-registered contracts only. Unknown versions fail closed at ingestion.</summary>
public sealed class PayloadRegistry
{
    private readonly Dictionary<(string Kind, int Version), Type> _observations = [];
    private readonly Dictionary<(string, int), Type> _events = [];
    public PayloadRegistry Observation<T>(string kind, int version) where T : IObservationPayload
    { _observations.Add((kind, version), typeof(T)); return this; }
    public PayloadRegistry Event<T>(string token, int version = 1) where T : IEventPayload
    { _events.Add((token.ToUpperInvariant(), version), typeof(T)); return this; }
    public bool Accepts(Observation observation) =>
        _observations.TryGetValue((observation.Kind, observation.PayloadVersion), out var type)
        && type == observation.Payload.GetType();
    public bool Accepts(EventCandidate candidate) =>
        _events.TryGetValue((candidate.EventType.ToUpperInvariant(), candidate.PayloadVersion), out var type) && type == candidate.Payload.GetType();
    public static JsonElement Serialize(object payload) => JsonSerializer.SerializeToElement(payload, payload.GetType());
}

public interface IEventDetector
{
    DetectorDefinition Definition { get; }
    IEnumerable<EventCandidate> Observe(Observation observation, ObservationWindow history);
    IEnumerable<EventCandidate> Complete(ObservationWindow history);
}

/// <summary>Shared, bounded history; detectors cannot modify it or retain an unbounded feed.</summary>
public sealed class ObservationWindow
{
    private readonly Queue<Observation> _items = new();
    private readonly TimeSpan _duration;
    private readonly int _capacity;
    private readonly SharedFactRegistry _facts;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string, int), Lazy<ISharedFact>> _cache = new();
    public ObservationWindow(TimeSpan duration, int capacity = 2048, SharedFactRegistry? facts = null)
    { _duration = duration; _capacity = capacity; _facts = facts ?? new(); }
    public T Fact<T>(string id, int version) where T : ISharedFact =>
        (T)_cache.GetOrAdd((id, version), key => new Lazy<ISharedFact>(() => _facts.Compute(key, this))).Value;
    public IReadOnlyList<Observation> For(string kind) => _items.Where(o => o.Kind == kind).ToArray();
    internal bool Add(Observation item)
    {
        while (_items.TryPeek(out var first) && item.GameTime - first.GameTime > _duration) _items.Dequeue();
        if (_items.Count >= _capacity) return false;
        _items.Enqueue(item);
        return true;
    }
    internal Observation? Find(string id) => _items.FirstOrDefault(o => o.Id == id);
    internal ObservationWindow Snapshot()
    {
        var snapshot = new ObservationWindow(_duration, _capacity, _facts);
        foreach (var observation in _items) snapshot._items.Enqueue(observation);
        return snapshot;
    }
}

/// <summary>
/// Pure code-registered processors. A frozen history snapshot memoizes each fact once
/// across detectors. Work is charged to the requesting detector's callback budget.
/// Input kinds must also be declared in each consuming detector's Inputs.
/// </summary>
public sealed class SharedFactRegistry
{
    private readonly Dictionary<(string, int), Func<ObservationWindow, ISharedFact>> _processors = [];
    public SharedFactRegistry Register<T>(string id, int version, Func<ObservationWindow, T> compute) where T : ISharedFact
    { _processors.Add((id, version), history => compute(history)); return this; }
    internal ISharedFact Compute((string, int) key, ObservationWindow history) => _processors[key](history);
}

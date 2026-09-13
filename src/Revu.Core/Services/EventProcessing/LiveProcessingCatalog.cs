namespace Revu.Core.Services.EventProcessing;

/// <summary>Capability declarations are deliberately narrower than desired detector inputs.</summary>
public static class LiveProcessingCatalog
{
    public const string SourceId = "riot.live-client";
    public const string HealthKind = "player.health";
    public const string FeedKind = "game.feed";
    public static SourceCapabilities LiveSource => new(SourceId, 1,
        new Dictionary<string, TimeSpan> { [HealthKind] = TimeSpan.FromSeconds(2), [FeedKind] = TimeSpan.FromSeconds(15) });

    public static PayloadRegistry Registry() => new PayloadRegistry()
        .Observation<PlayerHealth>(HealthKind, 1).Observation<FeedEvent>(FeedKind, 1);

    public static IReadOnlyList<IEventDetector> Detectors() =>
    [
        new UnavailableDetector(new("champion-exchanges", 1,
            [new("champion.damage-attributed", TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(250))],
            ["TRADE", "SHORT_TRADE", "EXTENDED_TRADE", "ALL_IN"], TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(10),
            UnavailableReason: "No validated live attacker/target damage source; health loss is insufficient")),
        new UnavailableDetector(new("fight-membership", 1,
            [new("champion.positions", TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(250)),
             new("champion.damage-attributed", TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(250)),
             new("champion.casts", TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(250))],
            ["TEAMFIGHT", "OUTNUMBERED_TEAMFIGHT", "EVEN_TEAMFIGHT", "NUMBERS_UP_TEAMFIGHT", "ABSENT_TEAMFIGHT"],
            TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(10),
            UnavailableReason: "No validated live all-champion position and interaction source"))
    ];

    public static ProcessingSession Create(IEnumerable<ObjectiveSubscription> subscriptions) =>
        new(Registry(), [LiveSource], Detectors(), subscriptions);

    private sealed class UnavailableDetector(DetectorDefinition definition) : IEventDetector
    {
        public DetectorDefinition Definition => definition;
        public IEnumerable<EventCandidate> Observe(Observation observation, ObservationWindow history) => [];
        public IEnumerable<EventCandidate> Complete(ObservationWindow history) => [];
    }
}

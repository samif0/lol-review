using System.Text.Json;
using Revu.Core.Models;
using Revu.Core.Services;
using Revu.Core.Services.EventProcessing;

namespace Revu.Core.Tests;

public sealed class EventProcessingTests
{
    private sealed record LowHealth(double Fraction) : IEventPayload;
    private sealed record HealthFact(double Fraction) : ISharedFact;
    private sealed class Detector(string id = "health-check", bool fail = false, int delayMs = 0, bool shared = false) : IEventDetector
    {
        public int Calls;
        public DetectorDefinition Definition => new(id, 1,
            [new("hp", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1))], ["LOW_HEALTH"],
            TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(50), true);
        public IEnumerable<EventCandidate> Observe(Observation observation, ObservationWindow history)
        {
            Interlocked.Increment(ref Calls);
            if (fail) throw new InvalidOperationException();
            if (delayMs > 0) Thread.Sleep(delayMs);
            var hp = (PlayerHealth)observation.Payload;
            if (shared) Assert.Equal(.1, history.Fact<HealthFact>("health-fraction", 1).Fraction);
            if (hp.Current / hp.Maximum >= .2) return [];
            return [new("dip", "LOW_HEALTH", observation.GameTime, observation.GameTime,
                [observation.Id], new LowHealth(hp.Current / hp.Maximum), VerificationStatus.Supported)];
        }
        public IEnumerable<EventCandidate> Complete(ObservationWindow history) => [];
    }
    private sealed class SlowEnumerableDetector : IEventDetector
    {
        private readonly Detector _inner = new();
        public readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DetectorDefinition Definition => _inner.Definition;
        public IEnumerable<EventCandidate> Observe(Observation observation, ObservationWindow history)
        {
            try
            {
                // Callback return alone is insufficient: deferred enumeration is
                // detector work too, and can still produce candidates after timeout.
                Thread.Sleep(200);
                foreach (var candidate in _inner.Observe(observation, history)) yield return candidate;
            }
            finally { Finished.TrySetResult(); }
        }
        public IEnumerable<EventCandidate> Complete(ObservationWindow history) => [];
    }
    private static ProcessingSession Session(IEnumerable<IEventDetector> detectors,
        IEnumerable<ObjectiveSubscription>? subscriptions = null, bool shadow = false, int capacity = 256,
        SharedFactRegistry? facts = null) =>
        new(new PayloadRegistry().Observation<PlayerHealth>("hp", 1).Event<LowHealth>("LOW_HEALTH"),
            [new("test", 1, new Dictionary<string, TimeSpan> { ["hp"] = TimeSpan.FromSeconds(1) })], detectors,
            subscriptions ?? [new(1, "LOW_HEALTH")], shadow, capacity, facts);
    private static Observation Hp(int seconds = 1, int version = 1, bool complete = true) =>
        new($"hp:{seconds}", "test", 1, "hp", version, "self", TimeSpan.FromSeconds(seconds),
            DateTimeOffset.UtcNow, TimeSpan.Zero, complete, new PlayerHealth(10, 100));

    [Fact]
    public async Task NewDetector_DeduplicatesSubscriptionsAndEvents_PersistsTypedEvidence()
    {
        var detector = new Detector();
        var subscriptions = new List<ObjectiveSubscription> { new(1, "LOW_HEALTH"), new(1, "LOW_HEALTH"), new(2, "LOW_HEALTH") };
        var session = Session([detector], subscriptions);
        subscriptions.Add(new(3, "LOW_HEALTH")); // next-match only
        Assert.Single(session.SamplingPlan);
        session.Publish(Hp());
        session.Publish(Hp(2));
        var report = await session.CompleteAsync();
        var item = Assert.Single(report.Events);
        Assert.Equal(2, detector.Calls);
        Assert.True(EventEligibility.IsEligible(item));
        Assert.True(EventEligibility.WasSubscribed(item, 1));
        Assert.True(EventEligibility.WasSubscribed(item, 2));
        Assert.False(EventEligibility.WasSubscribed(item, 3));
        Assert.Equal(item.EventKey, Assert.Single(EventIdentity.KeyForBatch([item])));
        using var json = JsonDocument.Parse(item.Details);
        var evidence = json.RootElement.GetProperty("processing").GetProperty("evidence")[0];
        Assert.Equal("test", evidence.GetProperty("SourceId").GetString());
        Assert.Equal(10, evidence.GetProperty("payload").GetProperty("Current").GetDouble());
    }

    [Fact]
    public async Task UntrackedDetector_DoesNoWork()
    {
        var detector = new Detector();
        var session = Session([detector], []);
        session.Publish(Hp());
        var report = await session.CompleteAsync();
        Assert.Empty(session.SamplingPlan);
        Assert.Empty(report.Events);
        Assert.Equal(0, detector.Calls);
    }

    [Fact]
    public async Task SharedFact_ComputedOnceAcrossDetectors()
    {
        var computations = 0;
        var facts = new SharedFactRegistry().Register("health-fraction", 1, history =>
        {
            Interlocked.Increment(ref computations);
            var hp = (PlayerHealth)history.For("hp").Last().Payload;
            return new HealthFact(hp.Current / hp.Maximum);
        });
        var session = Session([new Detector("a", shared: true), new Detector("b", shared: true)], facts: facts);
        session.Publish(Hp());
        var report = await session.CompleteAsync();
        Assert.Equal(2, report.Events.Count);
        Assert.Equal(1, computations);
    }

    [Fact]
    public async Task ProcessingReport_RoundTripsWithShadowEvidence_WithoutExposingIt()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(90210));
        var session = Session([new Detector()], shadow: true);
        session.Publish(Hp());
        var report = await session.CompleteAsync();
        await scope.GameEvents.SaveProcessingReportAsync(90210, report);
        await scope.GameEvents.SaveProcessingReportAsync(90210, report with { Recovery = report });
        var loaded = await scope.GameEvents.GetProcessingReportAsync(90210);
        Assert.NotNull(loaded);
        Assert.NotNull(loaded.Recovery);
        Assert.Equal(report.Subscriptions, loaded.Subscriptions);
        Assert.Equal(Assert.Single(report.Events).EventKey, Assert.Single(loaded.Recovery.Events).EventKey);
        Assert.Null(await scope.GameEvents.GetProcessingReportAsync(99999));
        using var connection = scope.OpenConnection();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT report_json FROM event_processing_reports WHERE game_id=90210";
        var json = (string)(await query.ExecuteScalarAsync())!;
        Assert.Contains("health-check", json);
        Assert.Contains("hp:1", json);
        Assert.Empty(await ((Revu.Core.Data.Repositories.IGameEventsRepository)scope.GameEvents).GetEligibleEventsAsync(90210));
    }

    [Fact]
    public async Task ShadowEvents_RemainAuditableButCannotReachConsumers()
    {
        var session = Session([new Detector()], shadow: true);
        session.Publish(Hp());
        var report = await session.CompleteAsync();
        Assert.Single(report.Events);
        Assert.Empty(EventEligibility.ForConsumers(report.Events));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(1, false)]
    public async Task UnsupportedVersionOrIncompleteObservation_DisablesDependentDetector(int version, bool complete)
    {
        var session = Session([new Detector()]);
        session.Publish(Hp(version: version, complete: complete));
        var report = await session.CompleteAsync();
        Assert.Empty(report.Events);
        Assert.Equal("incomplete", Assert.Single(report.Coverage).Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public async Task ClockRegressionOrGap_RetractsAffectedCandidates(int second)
    {
        var session = Session([new Detector()]);
        session.Publish(Hp());
        session.Publish(Hp(second));
        var report = await session.CompleteAsync();
        Assert.Empty(report.Events);
        Assert.Equal("incomplete", Assert.Single(report.Coverage).Status);
    }

    [Fact]
    public async Task DetectorFault_IsolatedFromHealthyDetector()
    {
        var session = Session([new Detector("bad", fail: true), new Detector("good")]);
        session.Publish(Hp());
        var report = await session.CompleteAsync();
        Assert.Single(report.Events);
        Assert.Equal("disabled", report.Coverage.Single(c => c.DetectorId == "bad").Status);
    }

    [Fact]
    public async Task SlowDetector_IsDisabledAndLateOutputCannotEscape()
    {
        var session = Session([new Detector(delayMs: 200)]);
        session.Publish(Hp());
        var report = await session.CompleteAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(report.Events);
        Assert.Equal("disabled", Assert.Single(report.Coverage).Status);
        Assert.Same(report, await session.CompleteAsync());
        Assert.False(session.Publish(Hp(2)));
    }

    [Fact]
    public async Task SlowCandidateEnumeration_IsDisabledAndCannotChangeCompletedReport()
    {
        var detector = new SlowEnumerableDetector();
        var session = Session([detector]);
        session.Publish(Hp());
        var report = await session.CompleteAsync(TimeSpan.FromSeconds(5));
        await detector.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(report.Events);
        Assert.Equal("disabled", Assert.Single(report.Coverage).Status);
        Assert.Equal("Callback deadline exceeded", Assert.Single(report.Coverage).Reason);
        Assert.Same(report, await session.CompleteAsync());
    }

    [Fact]
    public async Task QueueOverflow_FailsClosed()
    {
        var session = Session([new Detector(delayMs: 20)], capacity: 1);
        for (var i = 0; i < 1000; i++) session.Publish(Hp(i));
        var report = await session.CompleteAsync();
        Assert.True(report.DroppedObservations > 0);
        Assert.Empty(report.Events);
    }

    [Fact]
    public async Task ProductionCatalog_DoesNotPretendHealthProvesTradesOrPositions()
    {
        var session = LiveProcessingCatalog.Create([new(1, "TRADE"), new(2, "TEAMFIGHT")]);
        var report = await session.CompleteAsync();
        Assert.Empty(session.SamplingPlan);
        Assert.Equal(2, report.Coverage.Count);
        Assert.All(report.Coverage, c => Assert.Equal("unavailable", c.Status));
    }

    [Theory]
    [InlineData("TRADE")]
    [InlineData("TEAMFIGHT")]
    [InlineData("RECALL")]
    [InlineData("JUNGLE_PROXIMITY")]
    [InlineData("FUTURE_GUESS")]
    public void LegacyGuessesHidden_ManualCorrectionsPreserved(string type)
    {
        var item = new GameEvent { EventType = type, Details = "{\"detected\":true}" };
        Assert.False(EventEligibility.IsEligible(item));
        item.Details = "{\"correction\":{\"id\":\"review\"}}";
        Assert.True(EventEligibility.IsEligible(item));
    }

    [Fact]
    public void DeathFactSurvives_HeuristicAttributesDoNot_AndRawRowIsUnchanged()
    {
        var raw = new GameEvent { Id = 8, EventType = "DEATH", Details = "{\"killer\":\"A\",\"fog_death\":true,\"fight_numbers\":\"2v3\"}" };
        var eligible = Assert.Single(EventEligibility.ForConsumers([raw]));
        Assert.Equal(8, eligible.Id);
        Assert.DoesNotContain("fog_death", eligible.Details);
        Assert.Contains("fight_numbers", raw.Details);
    }

    [Fact]
    public void SyntheticFightCannotReappearThroughResolver()
    {
        var events = new[] { "KILL", "ASSIST", "DEATH" }.Select((type, i) => new GameEvent
            { Id = i + 1, GameTimeS = 600 + i, EventType = type }).ToArray();
        Assert.Single(TeamfightClustering.SyntheticClusters(events));
        Assert.Empty(TeamfightClustering.Resolve(events));
        var resolver = ObjectiveEventTieResolver.FromTies([("TEAMFIGHT", 1L, "Fight")]);
        Assert.All(resolver.ResolveForGame(events).Values, Assert.Empty);
    }

    [Fact]
    public void FightAssociations_RespectMatchSnapshot_AndAllowManualCorrection()
    {
        var fight = ConfirmedEventFixture.Fight(1, 600, 620);
        fight.Details = """{"start_s":600,"end_s":620,"self":"in","processing":{"version":1,"verification":"supported","shadow":false,"evidence":[{}],"objectiveIds":[1]}}""";
        var resolver = ObjectiveEventTieResolver.FromTies([("TEAMFIGHT", 1L, "Original"), ("TEAMFIGHT", 2L, "Later")]);
        var cluster = Assert.Single(resolver.ResolveTeamfightClusters([fight]));
        Assert.Equal(1L, Assert.Single(cluster.Objectives).ObjectiveId);
        fight.Details = ConfirmedEventFixture.Reviewed(fight.Details);
        Assert.Equal(2, Assert.Single(resolver.ResolveTeamfightClusters([fight])).Objectives.Count);
    }
}

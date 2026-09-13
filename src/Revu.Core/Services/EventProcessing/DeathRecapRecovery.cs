using System.Text.Json;

namespace Revu.Core.Services.EventProcessing;

/// <summary>Damage totals in one death record, not a sequence of timestamped hits.</summary>
public sealed record RecapExchange(int VictimId, int OpponentId, string Opponent,
    double DamageDealt, double DamageReceived, string RecordPath, string TimelineSha256, string GameVersion) : IObservationPayload;
public sealed record RecordedExchange(string Opponent, double DamageDealt, double DamageReceived,
    string Timing = "Death-record timestamp; exchange start and duration unknown") : IEventPayload;

public sealed class DeathRecapExchangeDetector : IEventDetector
{
    public const string Kind = "match-v5.death-recap-exchange";
    public const string EventType = "RECORDED_EXCHANGE";
    public DetectorDefinition Definition { get; } = new("death-recap-exchanges", 1,
        [new(Kind, TimeSpan.MaxValue, TimeSpan.Zero)], [EventType, "TRADE"],
        TimeSpan.Zero, TimeSpan.FromMilliseconds(50), true, "");

    public IEnumerable<EventCandidate> Observe(Observation observation, ObservationWindow history)
    {
        if (observation.Payload is not RecapExchange p
            || !double.IsFinite(p.DamageDealt) || !double.IsFinite(p.DamageReceived)
            || p.DamageDealt <= 0 || p.DamageReceived <= 0) return [];
        return [new(observation.Id, EventType, observation.GameTime, observation.GameTime,
            [observation.Id], new RecordedExchange(p.Opponent, p.DamageDealt, p.DamageReceived),
            VerificationStatus.Supported, [EventType, "TRADE"])];
    }

    public IEnumerable<EventCandidate> Complete(ObservationWindow history) => [];
}

/// <summary>
/// Bounded deterministic replay through the same engine used during gameplay.
/// Always shadow until labeled-match and performance release gates pass. Does not
/// infer nonfatal trades, trade durations, readiness, or fight-start counts.
/// </summary>
public static class DeathRecapRecovery
{
    private const string SourceId = "riot-match-v5-death-recap";
    private const int MaxRecords = 256;
    private sealed record Player(int Id, int Team, string Champion);
    private sealed class AmbiguousRecapException : Exception;
    private sealed record Parsed(IReadOnlyList<Observation> Observations, int Rejected, int Missing, int Usable);

    public static async Task<ProcessingReport> ProcessAsync(JsonElement match, JsonElement timeline,
        string puuid, IReadOnlyList<ObjectiveSubscription> subscriptions)
    {
        var registry = new PayloadRegistry().Observation<RecapExchange>(DeathRecapExchangeDetector.Kind, 1)
            .Event<RecordedExchange>(DeathRecapExchangeDetector.EventType);
        // Event-driven records have no periodic sampling requirement.
        var source = new SourceCapabilities(SourceId, 1, new Dictionary<string, TimeSpan>
            { [DeathRecapExchangeDetector.Kind] = TimeSpan.MaxValue });
        var session = new ProcessingSession(registry, [source], [new DeathRecapExchangeDetector()],
            subscriptions, shadow: true, queueCapacity: MaxRecords);
        if (session.SamplingPlan.Count == 0) return await session.CompleteAsync();
        Parsed? parsed = null;
        try
        {
            parsed = Parse(match, timeline, puuid);
            foreach (var observation in parsed.Observations) session.Publish(observation);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException
            or FormatException or OverflowException or ArgumentException)
        {
            session.ReportGap(DeathRecapExchangeDetector.Kind, $"Unsupported death-recap payload: {ex.GetType().Name}");
        }
        var report = await session.CompleteAsync(TimeSpan.FromSeconds(2));
        if (parsed is { Usable: 0 }) return report with
        {
            Sources = [source with { AvailableKinds = new Dictionary<string, TimeSpan>(), UnavailableReason = "No usable death-recap damage records" }],
            Coverage = report.Coverage.Select(c => c with { Status = "unavailable",
                Reason = $"No usable death-recap damage records; {parsed.Rejected} ambiguous, {parsed.Missing} missing." }).ToArray()
        };
        return parsed is null ? report : report with { Coverage = report.Coverage.Select(c => c.Status == "active"
            ? c with { Reason = $"Death-record evidence only; no exchange timing or nonfatal coverage. {parsed.Rejected} ambiguous records rejected; {parsed.Missing} records lack damage arrays." }
            : c).ToArray() };
    }

    private static Parsed Parse(JsonElement match, JsonElement timeline, string puuid)
    {
        var matchId = match.GetProperty("metadata").GetProperty("matchId").GetString();
        if (string.IsNullOrWhiteSpace(matchId) || matchId != timeline.GetProperty("metadata").GetProperty("matchId").GetString())
            throw new InvalidOperationException("Match identity mismatch");
        var participants = match.GetProperty("info").GetProperty("participants");
        if (participants.GetArrayLength() != 10) throw new InvalidOperationException("Unsupported roster");
        var roster = participants.EnumerateArray().Select(p => new Player(p.GetProperty("participantId").GetInt32(),
            p.GetProperty("teamId").GetInt32(), p.GetProperty("championName").GetString()!)).ToDictionary(p => p.Id);
        if (roster.Values.Any(p => p.Id < 1 || p.Id > 10 || p.Team is not (100 or 200) || string.IsNullOrWhiteSpace(p.Champion)))
            throw new InvalidOperationException("Invalid roster");
        var self = participants.EnumerateArray().Single(p => p.GetProperty("puuid").GetString() == puuid)
            .GetProperty("participantId").GetInt32();
        var frames = timeline.GetProperty("info").GetProperty("frames");
        if (frames.GetArrayLength() > 180) throw new InvalidOperationException("Frame budget exceeded");
        var output = new Dictionary<string, Observation>(StringComparer.Ordinal);
        var receivedAt = DateTimeOffset.UtcNow;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(timeline.GetRawText())));
        var patch = match.GetProperty("info").TryGetProperty("gameVersion", out var version) ? version.GetString() ?? "unknown" : "unknown";
        int frameIndex = 0, scanned = 0, rejected = 0, missing = 0, usable = 0;
        foreach (var frame in frames.EnumerateArray())
        {
            int eventIndex = 0;
            foreach (var e in frame.GetProperty("events").EnumerateArray())
            {
                var path = $"info.frames[{frameIndex}].events[{eventIndex++}]";
                if (++scanned > 20000) throw new InvalidOperationException("Record budget exceeded");
                if (e.GetProperty("type").GetString() != "CHAMPION_KILL") continue;
                int victim = e.GetProperty("victimId").GetInt32();
                if (!roster.TryGetValue(victim, out var dead)) throw new InvalidOperationException("Unknown victim");
                if (!e.TryGetProperty("victimDamageDealt", out var dealt)
                    || !e.TryGetProperty("victimDamageReceived", out var received)) { missing++; continue; }
                Dictionary<int, double> outgoing, incoming;
                try
                {
                    outgoing = Totals(dealt, dead, roster, outgoing: true);
                    incoming = Totals(received, dead, roster, outgoing: false);
                }
                catch (AmbiguousRecapException) { rejected++; continue; }
                usable++;
                var timestamp = e.GetProperty("timestamp").GetInt64();
                if (timestamp < 0 || timestamp > 10800000) throw new InvalidOperationException("Invalid game clock");
                foreach (var (opponentId, damage) in outgoing)
                {
                    if (victim != self && opponentId != self) continue;
                    if (roster[opponentId].Team == dead.Team || damage <= 0
                        || !incoming.TryGetValue(opponentId, out var taken) || taken <= 0) continue;
                    int other = victim == self ? opponentId : victim;
                    var id = $"{matchId}:{timestamp}:{victim}:{opponentId}";
                    var payload = new RecapExchange(victim, other, roster[other].Champion,
                        victim == self ? damage : taken, victim == self ? taken : damage, path, hash, patch);
                    var observation = new Observation(id, SourceId, 1, DeathRecapExchangeDetector.Kind, 1,
                        self.ToString(System.Globalization.CultureInfo.InvariantCulture), TimeSpan.FromMilliseconds(timestamp),
                        receivedAt, TimeSpan.Zero, true, payload);
                    if (output.TryGetValue(id, out var previous)
                        && previous.Payload is RecapExchange prior && prior with { RecordPath = path } != payload)
                        throw new InvalidOperationException("Conflicting duplicate death record");
                    output[id] = observation;
                    if (output.Count > MaxRecords) throw new InvalidOperationException("Observation budget exceeded");
                }
            }
            frameIndex++;
        }
        return new(output.Values.OrderBy(o => o.GameTime).ThenBy(o => o.Id, StringComparer.Ordinal).ToArray(), rejected, missing, usable);
    }

    private static Dictionary<int, double> Totals(JsonElement entries, Player victim,
        IReadOnlyDictionary<int, Player> roster, bool outgoing)
    {
        if (entries.GetArrayLength() > 128) throw new InvalidOperationException("Damage record budget exceeded");
        var result = new Dictionary<int, double>();
        foreach (var entry in entries.EnumerateArray())
        {
            int participant = entry.GetProperty("participantId").GetInt32();
            if (participant == 0) continue; // neutral/environmental damage is not champion evidence
            if (!roster.TryGetValue(participant, out var player)) throw new AmbiguousRecapException();
            // Current Match-V5 marks champion spell/attack records OTHER, not CHAMPION.
            // Resolve actors against the roster rather than guessing from that label.
            if (entry.GetProperty("type").GetString() is not ("OTHER" or "CHAMPION"))
                throw new InvalidOperationException("Unknown attributed damage type");
            // In outgoing records, name identifies the victim/dealer, ID identifies the target.
            // In incoming records, both identify the dealer. Never infer direction from name alone.
            if (!string.Equals(entry.GetProperty("name").GetString(), outgoing ? victim.Champion : player.Champion,
                StringComparison.OrdinalIgnoreCase)) throw new AmbiguousRecapException();
            double amount = 0;
            foreach (var field in new[] { "magicDamage", "physicalDamage", "trueDamage" })
            {
                double value = entry.GetProperty(field).GetDouble();
                if (!double.IsFinite(value) || value < 0 || value > 1000000) throw new InvalidOperationException("Invalid damage");
                amount += value;
            }
            result[participant] = result.GetValueOrDefault(participant) + amount;
        }
        return result;
    }
}

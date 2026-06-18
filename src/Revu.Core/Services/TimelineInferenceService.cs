#nullable enable

using Revu.Core.Models;

namespace Revu.Core.Services;

public sealed record InferredTimelineRegion(
    string Name,
    string SourceKey,
    int StartTimeSeconds,
    int EndTimeSeconds,
    string Color,
    string Tooltip,
    int Priority);

public static class TimelineInferenceService
{
    private const int LanePhaseSeconds = 14 * 60;

    private static readonly HashSet<string> CombatTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        GameEvent.EventTypes.Kill,
        GameEvent.EventTypes.Death,
        GameEvent.EventTypes.Assist,
        GameEvent.EventTypes.FirstBlood,
        GameEvent.EventTypes.MultiKill,
    };

    private static readonly HashSet<string> ObjectiveTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        GameEvent.EventTypes.Dragon,
        GameEvent.EventTypes.Baron,
        GameEvent.EventTypes.Herald,
    };

    public static IReadOnlyList<InferredTimelineRegion> Infer(IReadOnlyList<GameEvent> events)
    {
        if (events.Count == 0)
        {
            return [];
        }

        var ordered = events.OrderBy(static e => e.GameTimeS).ToList();
        var combatEvents = ordered.Where(static e => CombatTypes.Contains(e.EventType)).ToList();
        var objectiveEvents = ordered.Where(static e => ObjectiveTypes.Contains(e.EventType)).ToList();

        var candidates = new List<InferredTimelineRegion>();
        candidates.AddRange(InferObjectiveFights(objectiveEvents, combatEvents));
        candidates.AddRange(InferDeathsBeforeObjectives(objectiveEvents, combatEvents));
        candidates.AddRange(InferFirstPersonalCombatMoments(combatEvents));
        candidates.AddRange(InferCombatClusters(combatEvents));

        return candidates
            .OrderByDescending(static region => region.Priority)
            .ThenByDescending(static region => region.EndTimeSeconds - region.StartTimeSeconds)
            .ThenBy(static region => region.StartTimeSeconds)
            .Aggregate(new List<InferredTimelineRegion>(), AddIfNotOverlapping)
            .OrderBy(static region => region.StartTimeSeconds)
            .ToArray();
    }

    private static IEnumerable<InferredTimelineRegion> InferObjectiveFights(
        IReadOnlyList<GameEvent> objectiveEvents,
        IReadOnlyList<GameEvent> combatEvents)
    {
        foreach (var objective in objectiveEvents)
        {
            var nearby = combatEvents
                .Where(e => Math.Abs(e.GameTimeS - objective.GameTimeS) <= 30)
                .OrderBy(static e => e.GameTimeS)
                .ToArray();

            if (nearby.Length == 0)
            {
                continue;
            }

            var objectiveLabel = objective.EventType.ToUpperInvariant() switch
            {
                GameEvent.EventTypes.Baron => "Baron",
                GameEvent.EventTypes.Herald => "Herald",
                _ => "Dragon",
            };

            var outcome = CombatOutcome(nearby);
            var name = string.IsNullOrEmpty(outcome)
                ? $"{objectiveLabel} fight"
                : $"{outcome} {objectiveLabel} fight";
            var start = Math.Min(objective.GameTimeS, nearby.Min(static e => e.GameTimeS));
            var end = Math.Max(objective.GameTimeS, nearby.Max(static e => e.GameTimeS));

            yield return new InferredTimelineRegion(
                Name: name,
                SourceKey: BuildSourceKey("objective", objectiveLabel, Math.Max(0, start - 4), end + 6),
                StartTimeSeconds: Math.Max(0, start - 4),
                EndTimeSeconds: end + 6,
                Color: objective.EventType.Equals(GameEvent.EventTypes.Baron, StringComparison.OrdinalIgnoreCase)
                    ? "#8b5cf6"
                    : "#c89b3c",
                Tooltip: $"{name}: {nearby.Length} player-involved combat event(s) within 30s of {objectiveLabel.ToLowerInvariant()}",
                Priority: 100);
        }
    }

    private static IEnumerable<InferredTimelineRegion> InferDeathsBeforeObjectives(
        IReadOnlyList<GameEvent> objectiveEvents,
        IReadOnlyList<GameEvent> combatEvents)
    {
        var deaths = combatEvents
            .Where(static e => e.EventType.Equals(GameEvent.EventTypes.Death, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var objective in objectiveEvents)
        {
            var death = deaths
                .Where(e =>
                {
                    var delta = objective.GameTimeS - e.GameTimeS;
                    return delta is >= 15 and <= 75;
                })
                .OrderBy(e => objective.GameTimeS - e.GameTimeS)
                .FirstOrDefault();

            if (death is null)
            {
                continue;
            }

            var objectiveLabel = objective.EventType.ToUpperInvariant() switch
            {
                GameEvent.EventTypes.Baron => "Baron",
                GameEvent.EventTypes.Herald => "Herald",
                _ => "Dragon",
            };
            var start = Math.Max(0, death.GameTimeS - 3);
            var end = objective.GameTimeS + 3;
            var name = $"Death before {objectiveLabel}";

            yield return new InferredTimelineRegion(
                Name: name,
                SourceKey: BuildSourceKey("objective-death", objectiveLabel, start, end),
                StartTimeSeconds: start,
                EndTimeSeconds: end,
                Color: "#D38C90",
                Tooltip: $"{name}: player death {objective.GameTimeS - death.GameTimeS}s before {objectiveLabel.ToLowerInvariant()}",
                Priority: 70);
        }
    }

    private static IEnumerable<InferredTimelineRegion> InferFirstPersonalCombatMoments(
        IReadOnlyList<GameEvent> combatEvents)
    {
        var firstKill = combatEvents
            .Where(static e => e.EventType.Equals(GameEvent.EventTypes.FirstBlood, StringComparison.OrdinalIgnoreCase)
                || e.EventType.Equals(GameEvent.EventTypes.Kill, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static e => e.GameTimeS)
            .ThenBy(static e => e.EventType.Equals(GameEvent.EventTypes.FirstBlood, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();

        if (firstKill is not null)
        {
            yield return CreateFirstMomentRegion(
                firstKill,
                firstKill.EventType.Equals(GameEvent.EventTypes.FirstBlood, StringComparison.OrdinalIgnoreCase)
                    ? "First Blood"
                    : "First kill",
                "#28c76f",
                earlyPriority: 95,
                latePriority: 45);
        }

        var firstDeath = combatEvents
            .Where(static e => e.EventType.Equals(GameEvent.EventTypes.Death, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static e => e.GameTimeS)
            .FirstOrDefault();

        if (firstDeath is not null)
        {
            yield return CreateFirstMomentRegion(
                firstDeath,
                "First death",
                "#ea5455",
                earlyPriority: 92,
                latePriority: 42);
        }
    }

    private static InferredTimelineRegion CreateFirstMomentRegion(
        GameEvent evt,
        string label,
        string color,
        int earlyPriority,
        int latePriority)
    {
        var isEarly = evt.GameTimeS < LanePhaseSeconds;
        var phase = isEarly ? "lane phase" : "game";
        return new InferredTimelineRegion(
            Name: label,
            SourceKey: BuildSourceKey("first-combat", label, Math.Max(0, evt.GameTimeS - 6), evt.GameTimeS + 8),
            StartTimeSeconds: Math.Max(0, evt.GameTimeS - 6),
            EndTimeSeconds: evt.GameTimeS + 8,
            Color: color,
            Tooltip: $"{label}: first player-involved {label.ToLowerInvariant()} of the {phase}",
            Priority: isEarly ? earlyPriority : latePriority);
    }

    private static IEnumerable<InferredTimelineRegion> InferCombatClusters(IReadOnlyList<GameEvent> combatEvents)
    {
        var index = 0;
        while (index < combatEvents.Count)
        {
            var start = combatEvents[index].GameTimeS;
            var cluster = combatEvents
                .Skip(index)
                .TakeWhile(e => e.GameTimeS - start <= 25)
                .ToArray();

            // Combat floor for a "Teamfight" is 5 combat events within 25s — a
            // deliberate, documented one-step widening from 6 (brief 2026-06-17-10/12:
            // ≥6 surfaced ~25 games, ≥5 surfaces ~65, roughly doubling coverage so
            // more teamfights reach the timeline). This is the HARDCODED inference
            // path; the user-customizable Teamfight derived-event default (≥3 combat
            // events / 15s window, seeded in derived_event_definitions) is the path
            // users can tune — keep the two in the same spirit but don't conflate them.
            if (cluster.Length >= 5)
            {
                yield return CreateCombatRegion("Teamfight", cluster, "#ff6b6b", priority: 80);
                index += cluster.Length;
            }
            else if (cluster.Length >= 3)
            {
                yield return CreateCombatRegion("3v3 skirmish", cluster, "#ffa07a", priority: 60);
                index += cluster.Length;
            }
            else if (cluster.Length == 2 && cluster[^1].GameTimeS - cluster[0].GameTimeS <= 12)
            {
                yield return CreateCombatRegion("2v2 skirmish", cluster, "#06b6d4", priority: 50);
                index += cluster.Length;
            }
            else
            {
                var single = cluster[0];
                // A lone death is titled just "Death" — NOT "Isolated death". We
                // can't prove isolation: the Live Client Data API gives only a
                // kill-feed ({killer, time}) with no champion positions, so
                // whether the player was actually alone/outnumbered is unknowable.
                // The old "Isolated death" label was guessed from event spacing
                // and produced false positives. A lone kill is still a "Pick"
                // (a kill with no surrounding teamfight cluster — which the events
                // do support).
                var isDeath = single.EventType.Equals(GameEvent.EventTypes.Death, StringComparison.OrdinalIgnoreCase);
                var label = isDeath ? "Death" : "Pick";
                yield return new InferredTimelineRegion(
                    Name: label,
                    SourceKey: BuildSourceKey("combat", label, Math.Max(0, single.GameTimeS - 3), single.GameTimeS + 5),
                    StartTimeSeconds: Math.Max(0, single.GameTimeS - 3),
                    EndTimeSeconds: single.GameTimeS + 5,
                    Color: isDeath ? "#ea5455" : "#28c76f",
                    Tooltip: isDeath
                        ? "Death: no teammate kills or assists clustered around it"
                        : "Pick: a kill with no surrounding teamfight",
                    Priority: 20);
                index++;
            }
        }
    }

    private static InferredTimelineRegion CreateCombatRegion(
        string baseName,
        IReadOnlyList<GameEvent> cluster,
        string color,
        int priority)
    {
        var outcome = CombatOutcome(cluster);
        var name = string.IsNullOrEmpty(outcome) ? baseName : $"{outcome} {baseName}";
        return new InferredTimelineRegion(
            Name: name,
            SourceKey: BuildSourceKey("combat", name, Math.Max(0, cluster[0].GameTimeS - 3), cluster[^1].GameTimeS + 5),
            StartTimeSeconds: Math.Max(0, cluster[0].GameTimeS - 3),
            EndTimeSeconds: cluster[^1].GameTimeS + 5,
            Color: color,
            Tooltip: $"{name}: {cluster.Count} player-involved combat event(s)",
            Priority: priority);
    }

    private static string CombatOutcome(IReadOnlyList<GameEvent> cluster)
    {
        var positive = cluster.Count(e =>
            e.EventType.Equals(GameEvent.EventTypes.Kill, StringComparison.OrdinalIgnoreCase)
            || e.EventType.Equals(GameEvent.EventTypes.Assist, StringComparison.OrdinalIgnoreCase)
            || e.EventType.Equals(GameEvent.EventTypes.FirstBlood, StringComparison.OrdinalIgnoreCase)
            || e.EventType.Equals(GameEvent.EventTypes.MultiKill, StringComparison.OrdinalIgnoreCase));
        var deaths = cluster.Count(e => e.EventType.Equals(GameEvent.EventTypes.Death, StringComparison.OrdinalIgnoreCase));

        if (positive >= deaths + 2)
        {
            return "Won";
        }

        if (deaths > positive)
        {
            return "Lost";
        }

        return "";
    }

    private static string BuildSourceKey(string scope, string label, int startTimeSeconds, int endTimeSeconds)
    {
        var normalizedLabel = new string(label
            .ToLowerInvariant()
            .Select(static ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray())
            .Trim('-');
        return $"{scope}:{normalizedLabel}:{startTimeSeconds}:{endTimeSeconds}";
    }

    private static List<InferredTimelineRegion> AddIfNotOverlapping(
        List<InferredTimelineRegion> accepted,
        InferredTimelineRegion candidate)
    {
        var overlaps = accepted.Any(region =>
            candidate.StartTimeSeconds <= region.EndTimeSeconds
            && candidate.EndTimeSeconds >= region.StartTimeSeconds);

        if (!overlaps)
        {
            accepted.Add(candidate);
        }

        return accepted;
    }
}

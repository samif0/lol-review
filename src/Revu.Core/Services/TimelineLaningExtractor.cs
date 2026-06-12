#nullable enable

using System.Text.Json;

namespace Revu.Core.Services;

/// <summary>Laning numbers at the 10-minute mark, extracted from a Match-V5 timeline.</summary>
public sealed record LaningAt10(
    double CsAt10,
    int? GoldDiffAt10,
    double? CsDiffAt10);

/// <summary>
/// v2.18: pure parser for Riot's Match-V5 timeline payload. The timeline has
/// per-minute participantFrames keyed "1".."10" (totalGold, minionsKilled,
/// jungleMinionsKilled) but no team/position info — so lane-opponent identity
/// comes from the match payload (teamPosition), the same way
/// <see cref="EnemyLanerBackfillService.ExtractEnemyLaner"/> resolves it.
/// </summary>
public static class TimelineLaningExtractor
{
    private const long TenMinutesMs = 600_000;

    /// <summary>
    /// Returns null when the game has no 10-minute frame (remakes, short
    /// games) or the player isn't in the match. GoldDiff/CsDiff are null when
    /// the lane opponent can't be identified (ARAM, off-role payloads).
    /// </summary>
    public static LaningAt10? Extract(JsonElement match, JsonElement timeline, string puuid)
    {
        var selfId = ParticipantIdFor(timeline, puuid);
        if (selfId is null)
        {
            return null;
        }

        var opponentId = LaneOpponentParticipantId(match, puuid);

        var frame = FrameAtOrAfter(timeline, TenMinutesMs);
        if (frame is null)
        {
            return null;
        }

        var self = ParticipantFrame(frame.Value, selfId.Value);
        if (self is null)
        {
            return null;
        }

        var selfCs = MinionsOf(self.Value);
        var selfGold = GoldOf(self.Value);

        int? goldDiff = null;
        double? csDiff = null;
        if (opponentId is not null)
        {
            var opponent = ParticipantFrame(frame.Value, opponentId.Value);
            if (opponent is not null)
            {
                goldDiff = selfGold - GoldOf(opponent.Value);
                csDiff = selfCs - MinionsOf(opponent.Value);
            }
        }

        return new LaningAt10(selfCs, goldDiff, csDiff);
    }

    /// <summary>metadata.participants[] is puuid-ordered; participantId = index + 1.</summary>
    private static int? ParticipantIdFor(JsonElement timeline, string puuid)
    {
        if (!timeline.TryGetProperty("metadata", out var metadata)
            || !metadata.TryGetProperty("participants", out var participants)
            || participants.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var index = 0;
        foreach (var p in participants.EnumerateArray())
        {
            index++;
            if (p.ValueKind == JsonValueKind.String
                && string.Equals(p.GetString(), puuid, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }
        return null;
    }

    /// <summary>
    /// Same-position participant on the opposite team, from the MATCH payload
    /// (the timeline has no positions).
    /// </summary>
    private static int? LaneOpponentParticipantId(JsonElement match, string puuid)
    {
        if (!match.TryGetProperty("info", out var info)
            || !info.TryGetProperty("participants", out var participants)
            || participants.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var selfTeam = 0;
        var selfPos = "";
        foreach (var p in participants.EnumerateArray())
        {
            if (p.TryGetProperty("puuid", out var pPuuid)
                && pPuuid.ValueKind == JsonValueKind.String
                && string.Equals(pPuuid.GetString(), puuid, StringComparison.OrdinalIgnoreCase))
            {
                selfTeam = IntOf(p, "teamId");
                selfPos = StringOf(p, "teamPosition");
                break;
            }
        }
        if (selfTeam == 0 || selfPos.Length == 0)
        {
            return null;
        }

        foreach (var p in participants.EnumerateArray())
        {
            var team = IntOf(p, "teamId");
            if (team != 0 && team != selfTeam
                && string.Equals(StringOf(p, "teamPosition"), selfPos, StringComparison.Ordinal))
            {
                var id = IntOf(p, "participantId");
                return id > 0 ? id : null;
            }
        }
        return null;
    }

    private static JsonElement? FrameAtOrAfter(JsonElement timeline, long timestampMs)
    {
        if (!timeline.TryGetProperty("info", out var info)
            || !info.TryGetProperty("frames", out var frames)
            || frames.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var frame in frames.EnumerateArray())
        {
            if (frame.TryGetProperty("timestamp", out var ts)
                && ts.ValueKind == JsonValueKind.Number
                && ts.GetInt64() >= timestampMs)
            {
                return frame;
            }
        }
        return null;
    }

    private static JsonElement? ParticipantFrame(JsonElement frame, int participantId)
    {
        if (!frame.TryGetProperty("participantFrames", out var pf)
            || pf.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return pf.TryGetProperty(participantId.ToString(), out var self) ? self : null;
    }

    private static double MinionsOf(JsonElement participantFrame)
        => IntOf(participantFrame, "minionsKilled") + IntOf(participantFrame, "jungleMinionsKilled");

    private static int GoldOf(JsonElement participantFrame)
        => IntOf(participantFrame, "totalGold");

    private static int IntOf(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    private static string StringOf(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}

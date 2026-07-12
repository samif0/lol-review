#nullable enable

namespace Revu.Core.Services;

/// <summary>
/// Pure composer for the Settings "Scan for VODs" result text (P-041). Extracted
/// from the sidecar's /api/settings/scan-vods endpoint so the exact strings the
/// user sees are contract-tested; the endpoint just calls these.
/// </summary>
public static class VodScanMessages
{
    /// <summary>
    /// The P-007 silent-miss diagnostic suffix: recordings from the last 7 days
    /// that match no linked game. Empty when there are none.
    /// </summary>
    public static string UnmatchedRecentNote(int unmatchedRecent) =>
        unmatchedRecent > 0
            ? $" {unmatchedRecent} recording(s) from the last 7 days match no game."
            : "";

    /// <summary>The success-path text (any of the three healthy outcomes).</summary>
    public static string Success(int matched, int recordingCount, string unmatchedNote)
    {
        if (matched > 0)
            return $"Matched {matched} VOD(s) to games! ({recordingCount} recordings found){unmatchedNote}";
        if (recordingCount == 0)
            return "No video files found. Check that your Ascent folder is set and contains recordings.";
        return $"Found {recordingCount} recordings but no new matches. Games may already be linked or outside the match window.{unmatchedNote}";
    }

    /// <summary>
    /// The database-unavailable text. Distinguishes "your recordings are fine,
    /// the database is the problem" from a generic scan failure, and carries the
    /// user-actionable reason from SqliteOpenHealth.Describe.
    /// </summary>
    public static string DatabaseFailure(int recordingCount, string reason)
    {
        var found = recordingCount > 0
            ? $"Found {recordingCount} recording(s), but "
            : "";
        return $"{found}Revu couldn't open its database to link recordings to games: {reason}";
    }
}

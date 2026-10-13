namespace Revu.Core.Services;

public static class VodScanMessages
{
    public static string Success(int matched, int recordingCount, int waiting = 0, int alreadyLinked = 0,
        int unmatched = 0, int unrecognized = 0, int ambiguous = 0, int protectedMatches = 0)
    {
        var message = matched > 0
            ? matched == 1 ? "Linked 1 recording to a match." : $"Linked {matched} recordings to matches."
            : recordingCount == 0
                ? "No video files found in the selected Ascent folder."
                : $"Found {recordingCount} {(recordingCount == 1 ? "video" : "videos")}. No new matches to link.";
        if (alreadyLinked > 0)
            message += $" {alreadyLinked} {(alreadyLinked == 1 ? "recording is" : "recordings are")} already linked.";
        if (unmatched > 0)
            message += $" {unmatched} {(unmatched == 1 ? "recording could" : "recordings could")} not be matched to the latest 100 saved games. If matches are missing, keep League open and choose Recover missing matches, then scan again.";
        if (unrecognized > 0)
            message += $" {unrecognized} {(unrecognized == 1 ? "filename has" : "filenames have")} no recognized recording timestamp; use the original Ascent filenames.";
        if (ambiguous > 0)
            message += $" {ambiguous} {(ambiguous == 1 ? "recording has" : "recordings have")} more than one possible match and {(ambiguous == 1 ? "was" : "were")} left unlinked.";
        if (protectedMatches > 0)
            message += $" {protectedMatches} {(protectedMatches == 1 ? "recording matches a game" : "recordings match games")} with an existing or reserved recording.";
        if (waiting > 0)
            message += $" {waiting} {(waiting == 1 ? "file is" : "files are")} empty, busy, or still settling; try again after recording finishes.";
        return message;
    }
}

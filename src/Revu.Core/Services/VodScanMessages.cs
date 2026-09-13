namespace Revu.Core.Services;

public static class VodScanMessages
{
    public static string Success(int matched, int recordingCount, int waiting = 0)
    {
        var message = matched > 0
            ? matched == 1 ? "Linked 1 recording to a match." : $"Linked {matched} recordings to matches."
            : recordingCount == 0
                ? "No video files found in the selected Ascent folder."
                : $"Found {recordingCount} {(recordingCount == 1 ? "video" : "videos")}. No new matches to link. Videos may already be linked or lack a matching filename timestamp.";
        return waiting > 0 ? message + $" {waiting} {(waiting == 1 ? "file is" : "files are")} empty, busy, or still settling; try again after recording finishes." : message;
    }
}

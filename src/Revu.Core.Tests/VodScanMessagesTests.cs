using Revu.Core.Services;

namespace Revu.Core.Tests;

public sealed class VodScanMessagesTests
{
    [Fact]
    public void ResultsDistinguishNoFilesNoMatchAndIncompleteFiles()
    {
        Assert.Contains("No video files", VodScanMessages.Success(0, 0));
        Assert.Contains("No new matches", VodScanMessages.Success(0, 1));
        Assert.Equal("Linked 1 recording to a match.", VodScanMessages.Success(1, 1));
        Assert.Equal("Linked 2 recordings to matches.", VodScanMessages.Success(2, 2));
        Assert.Contains("1 file is empty, busy, or still settling", VodScanMessages.Success(0, 1, 1));
    }

    [Fact]
    public void AlreadyLinkedFilesDoNotSuggestRecoveryOrFilenameProblems()
    {
        var message = VodScanMessages.Success(0, 65, alreadyLinked: 65);
        Assert.Contains("65 recordings are already linked", message);
        Assert.DoesNotContain("Recover missing matches", message);
        Assert.DoesNotContain("timestamp", message);
    }

    [Fact]
    public void MultipleProblemTypesRemainVisibleWhenSomeRecordingsWereLinked()
    {
        var message = VodScanMessages.Success(1, 9, waiting: 2, unmatched: 2, unrecognized: 2, ambiguous: 2);
        Assert.StartsWith("Linked 1 recording to a match.", message);
        Assert.Contains("2 recordings could not be matched to the latest 100 saved games", message);
        Assert.Contains("2 filenames have no recognized recording timestamp", message);
        Assert.Contains("2 recordings have more than one possible match and were left unlinked", message);
        Assert.Contains("2 files are empty, busy, or still settling", message);
    }
}

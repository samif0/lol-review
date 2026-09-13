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
}

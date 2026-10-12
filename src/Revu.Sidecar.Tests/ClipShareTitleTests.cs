using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// A shared clip's public title (watch page, link previews) is never the bookmark note,
/// which is the player's private review note. Without a typed title it is neutral:
/// champion and game time.
/// </summary>
public sealed class ClipShareTitleTests
{
    [Fact]
    public async Task Share_WithoutATitle_PublishesChampionAndGameTime_NotTheNote()
    {
        using var h = new NarrationHarness();
        await h.InitAsync();
        h.SignIn();
        var (bm, _) = await h.ClipAsync(start: 1342, end: 1360);
        await h.Scope.Vod.UpdateBookmarkAsync(bm, note: "flamed my jungler, tilted, should have dodged");
        using var tap = new EventTap(h.Hub);
        h.Share.Start();

        var reply = await ClipShareRequests.UploadAsync(new ShareClipBody(NarrationHarness.GameId, bm, "Jinx", null),
            h.Scope.Vod, h.Narrations, h.Scope.Games, h.Scope.Config, h.Share, NullLogger.Instance);
        Assert.Equal(200, reply.Status);
        await tap.WaitForAsync("clipShareProgress", e => e.GetProperty("phase").GetString() == "done");

        Assert.Equal("Jinx clip, 22:22", Assert.Single(h.Upload.Titles));
    }

    [Fact]
    public async Task Share_WithATypedTitle_PublishesThatTitle()
    {
        using var h = new NarrationHarness();
        await h.InitAsync();
        h.SignIn();
        var (bm, _) = await h.ClipAsync();
        using var tap = new EventTap(h.Hub);
        h.Share.Start();

        await ClipShareRequests.UploadAsync(new ShareClipBody(NarrationHarness.GameId, bm, "Jinx", "  Clean flank  "),
            h.Scope.Vod, h.Narrations, h.Scope.Games, h.Scope.Config, h.Share, NullLogger.Instance);
        await tap.WaitForAsync("clipShareProgress", e => e.GetProperty("phase").GetString() == "done");

        Assert.Equal("Clean flank", Assert.Single(h.Upload.Titles));
    }

    [Theory]
    [InlineData("", 65, "League clip, 1:05")]
    [InlineData("Kai'Sa", 0, "Kai'Sa clip, 0:00")]
    [InlineData(" Ahri ", 3599, "Ahri clip, 59:59")]
    public void NeutralTitle_UsesChampionAndGameTime(string champion, int seconds, string expected)
    {
        Assert.Equal(expected, ClipShareRequests.ShareTitle(null, champion, seconds));
    }
}

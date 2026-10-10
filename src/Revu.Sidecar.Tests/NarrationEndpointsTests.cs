using Revu.Core.Data.Repositories;
using Revu.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// The narration routes (C6 6.4 to 6.7) and the share request (6.2, 6.2b), driven through
/// their ASP.NET-free handlers against a real temp DB with ffmpeg and the proxy stubbed.
/// </summary>
public sealed class NarrationEndpointsTests
{
    // ── Validation (pure) ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("3F2504E0-4F89-11D3-9A0C-0305E82C3301")]          // uppercase
    [InlineData("{3f2504e0-4f89-11d3-9a0c-0305e82c3301}")]        // braces
    [InlineData("3f2504e04f8911d39a0c0305e82c3301")]              // N format
    [InlineData("..\\..\\config")]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301\\..\\x")]
    [InlineData("")]
    public void Save_RejectsAnythingButALowercaseDGuid(string id)
    {
        var body = new SaveNarrationBody(1, 2, id, "audio/webm", 0, 5000, 0.8, 1, true);
        Assert.NotNull(NarrationRequestValidator.ValidateSave(body, out _));
    }

    [Theory]
    [InlineData("audio/ogg", 0, 5000, 0.8, 1.0)]
    [InlineData("video/webm", 0, 5000, 0.8, 1.0)]
    [InlineData("audio/webm", -10001, 5000, 0.8, 1.0)]
    [InlineData("audio/webm", 10001, 5000, 0.8, 1.0)]
    [InlineData("audio/webm", 0, 999, 0.8, 1.0)]
    [InlineData("audio/webm", 0, 625001, 0.8, 1.0)]
    [InlineData("audio/webm", 0, 5000, 1.6, 1.0)]
    [InlineData("audio/webm", 0, 5000, -0.1, 1.0)]
    [InlineData("audio/webm", 0, 5000, 0.8, 2.1)]
    [InlineData("audio/webm", 0, 5000, double.NaN, 1.0)]
    [InlineData("audio/webm", 0, 5000, 0.8, double.PositiveInfinity)]
    public void Save_RejectsOutOfRangeFields(string mime, int offset, int duration, double gameVolume, double voiceVolume)
    {
        var body = new SaveNarrationBody(1, 2, Guid.NewGuid().ToString("D"), mime, offset, duration, gameVolume, voiceVolume, true);
        Assert.NotNull(NarrationRequestValidator.ValidateSave(body, out _));
    }

    [Fact]
    public void Save_AcceptsTheLimits()
    {
        var id = Guid.NewGuid();
        var body = new SaveNarrationBody(1, 2, id.ToString("D"), "audio/webm;codecs=opus", -10000, 625000, 1.5, 2, false);
        Assert.Null(NarrationRequestValidator.ValidateSave(body, out var parsed));
        Assert.Equal(id, parsed);
    }

    [Theory]
    [InlineData(0, 600, null)]
    [InlineData(100, 701, "Clips can be up to 10 minutes. Trim the range and try again.")]
    public void Extract_OverTenMinutes_Is422WithTheExactText(int start, int end, string? expected)
    {
        Assert.Equal(expected, NarrationRequestValidator.ExtractRangeError(start, end));
    }

    // ── Save ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Save_RejectsAMissingFile_AndBadMagic_With400()
    {
        using var h = new NarrationHarness();
        await h.InitAsync();
        var (bm, _) = await h.ClipAsync();

        var missing = await h.Commands().SaveAsync(h.SaveBody(bm, Guid.NewGuid().ToString("D")), default);
        Assert.Equal(400, missing.Status);

        var bad = await h.Commands().SaveAsync(h.SaveBody(bm, h.Voice(validMagic: false)), default);
        Assert.Equal(400, bad.Status);
        Assert.Equal("Narration audio is not a WebM file.", Replies.Body(bad).GetProperty("error").GetString());
        Assert.Empty(h.Mixer.Plans);
    }

    [Fact]
    public async Task Save_MissingClipFile_Is404WithTheExactText()
    {
        using var h = new NarrationHarness();
        await h.InitAsync();
        var (bm, _) = await h.ClipAsync(onDisk: false);

        var reply = await h.Commands().SaveAsync(h.SaveBody(bm, h.Voice()), default);

        Assert.Equal(404, reply.Status);
        Assert.Equal("Clip file is missing. Save the clip again.", Replies.Body(reply).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Save_SignedOut_RendersStoresNeedsLogin_AndClearsAnExistingShare()
    {
        using var h = new NarrationHarness();
        await h.InitAsync();
        var (bm, clip) = await h.ClipAsync();
        await h.Scope.Vod.SetBookmarkShareUrlAsync(bm, "https://revu.lol/old1234");
        var voice = h.Voice();

        var reply = await h.Commands().SaveAsync(h.SaveBody(bm, voice, offsetMs: 120), default);

        Assert.Equal(200, reply.Status);
        var body = Replies.Body(reply);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.True(body.GetProperty("shareCleared").GetBoolean());
        var dto = body.GetProperty("narration");
        Assert.Equal("needs_login", dto.GetProperty("transcriptStatus").GetString());
        Assert.Equal(120, dto.GetProperty("offsetMs").GetInt32());
        var narrated = dto.GetProperty("narratedClipPath").GetString()!;
        Assert.True(File.Exists(narrated));
        Assert.Equal(Path.Combine(Path.GetDirectoryName(clip)!, "narrated"), Path.GetDirectoryName(narrated));
        Assert.Matches(@"_narrated_\d{8}_\d{6}(_\d+)?\.mp4$", narrated);

        var row = (await h.Narrations.GetAsync(bm))!;
        Assert.Equal(Path.Combine(h.NarrationDir, voice + ".webm"), row.AudioPath);
        Assert.Equal(clip, row.SourceClipPath);
        Assert.Equal(NarrationHarness.GameId, row.GameId);
        Assert.Equal("", (await h.Scope.Vod.GetBookmarksAsync(NarrationHarness.GameId)).Single().ShareUrl);
        // Signed out: the remote delete waits for the next sign-in.
        Assert.Contains("old1234", h.Cleanup.Snapshot());
        Assert.Empty(h.Upload.Deleted);
    }

    [Fact]
    public async Task Save_SignedIn_QueuesTranscription_AndReplacingDeletesThePreviousFiles()
    {
        using var h = new NarrationHarness();
        await h.InitAsync();
        h.SignIn();
        var (bm, _) = await h.ClipAsync();

        var first = await h.Commands().SaveAsync(h.SaveBody(bm, h.Voice()), default);
        Assert.Equal("pending", Replies.Body(first).GetProperty("narration").GetProperty("transcriptStatus").GetString());
        var firstRow = (await h.Narrations.GetAsync(bm))!;

        var second = await h.Commands().SaveAsync(h.SaveBody(bm, h.Voice()), default);
        Assert.Equal(200, second.Status);
        Assert.False(Replies.Body(second).GetProperty("shareCleared").GetBoolean());
        var secondRow = (await h.Narrations.GetAsync(bm))!;

        Assert.NotEqual(firstRow.AudioPath, secondRow.AudioPath);
        Assert.False(File.Exists(firstRow.AudioPath));
        Assert.False(File.Exists(firstRow.NarratedClipPath));
        Assert.True(File.Exists(secondRow.NarratedClipPath));
        Assert.True(secondRow.TranscriptGeneration > firstRow.TranscriptGeneration);
    }

    [Fact]
    public async Task Save_RenderFailure_Is422_AndWritesNoRow()
    {
        using var h = new NarrationHarness();
        await h.InitAsync();
        var (bm, _) = await h.ClipAsync();
        h.Mixer.Succeed = false;

        var reply = await h.Commands().SaveAsync(h.SaveBody(bm, h.Voice()), default);

        Assert.Equal(422, reply.Status);
        Assert.Equal("Revu could not render the narrated clip. Your recording was not saved.",
            Replies.Body(reply).GetProperty("error").GetString());
        Assert.Null(await h.Narrations.GetAsync(bm));
    }

    [Fact]
    public async Task Save_ClipDeletedDuringTheRender_Is404_WritesNoRow_AndDropsTheRender()
    {
        using var h = new NarrationHarness();
        await h.InitAsync();
        h.SignIn();
        var (bm, _) = await h.ClipAsync();
        h.Mixer.BeforeMix = async _ => await h.Scope.Vod.DeleteClipFullAsync(bm);

        var reply = await h.Commands().SaveAsync(h.SaveBody(bm, h.Voice()), default);

        Assert.Equal(404, reply.Status);
        Assert.Null(await h.Narrations.GetAsync(bm));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(h.ClipsDir, "narrated")));
    }

    [Fact]
    public async Task Save_WhileRendering_Is409_AndStopping_Is503WithNoRow()
    {
        using var h = new NarrationHarness();
        await h.InitAsync();
        var (bm, _) = await h.ClipAsync();
        using var stopping = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Mixer.BeforeMix = async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };

        var running = h.Commands(stopping.Token).SaveAsync(h.SaveBody(bm, h.Voice()), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var conflict = await h.Commands().SaveAsync(h.SaveBody(bm, h.Voice()), default);
        Assert.Equal(409, conflict.Status);
        Assert.Equal("This clip is already rendering.", Replies.Body(conflict).GetProperty("error").GetString());

        stopping.Cancel();
        var stopped = await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(503, stopped.Status);
        Assert.Equal("Revu is shutting down.", Replies.Body(stopped).GetProperty("error").GetString());
        Assert.Null(await h.Narrations.GetAsync(bm));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(h.ClipsDir, "narrated")));
    }

    // ── Mix / delete / transcribe ────────────────────────────────────────────

    [Fact]
    public async Task Mix_WithoutNarration_Is404_AndAnOffsetChangeResetsTheTranscript()
    {
        using var h = new NarrationHarness();
        await h.InitAsync();
        var (bm, _) = await h.ClipAsync();

        var none = await h.Commands().MixAsync(new MixNarrationBody(NarrationHarness.GameId, bm, 0, 1, 1, true), default);
        Assert.Equal(404, none.Status);
        Assert.Equal("No narration for this clip.", Replies.Body(none).GetProperty("error").GetString());

        await h.Commands().SaveAsync(h.SaveBody(bm, h.Voice()), default);
        var row = (await h.Narrations.GetAsync(bm))!;
        Assert.Equal(TranscriptStatuses.NeedsLogin, row.TranscriptStatus);

        var volumeOnly = await h.Commands().MixAsync(new MixNarrationBody(NarrationHarness.GameId, bm, 0, 1.2, 1.4, false), default);
        Assert.Equal(200, volumeOnly.Status);
        var afterVolume = (await h.Narrations.GetAsync(bm))!;
        Assert.Equal(row.TranscriptGeneration, afterVolume.TranscriptGeneration);
        Assert.False(File.Exists(row.NarratedClipPath));
        Assert.True(File.Exists(afterVolume.NarratedClipPath));
        Assert.False(h.Mixer.Plans[^1].Duck);

        var nudged = await h.Commands().MixAsync(new MixNarrationBody(NarrationHarness.GameId, bm, 100, 1.2, 1.4, false), default);
        Assert.Equal(200, nudged.Status);
        var afterNudge = (await h.Narrations.GetAsync(bm))!;
        Assert.Equal(afterVolume.TranscriptGeneration + 1, afterNudge.TranscriptGeneration);
        Assert.Equal(100, afterNudge.OffsetMs);
    }

    [Fact]
    public async Task Delete_RemovesRowAndFiles_IsIdempotent_AndClearsTheShare()
    {
        using var h = new NarrationHarness();
        await h.InitAsync();
        h.SignIn();
        var (bm, clip) = await h.ClipAsync();
        await h.Commands().SaveAsync(h.SaveBody(bm, h.Voice()), default);
        var row = (await h.Narrations.GetAsync(bm))!;
        await h.Scope.Vod.SetBookmarkShareUrlAsync(bm, "https://revu.lol/xyz9876");

        var deleted = await h.Commands().DeleteAsync(new NarrationTargetBody(NarrationHarness.GameId, bm));
        Assert.Equal(200, deleted.Status);
        Assert.True(Replies.Body(deleted).GetProperty("shareCleared").GetBoolean());
        Assert.Null(await h.Narrations.GetAsync(bm));
        Assert.False(File.Exists(row.AudioPath));
        Assert.False(File.Exists(row.NarratedClipPath));
        Assert.True(File.Exists(clip));
        Assert.Equal(new[] { "xyz9876" }, h.Upload.Deleted);

        var again = await h.Commands().DeleteAsync(new NarrationTargetBody(NarrationHarness.GameId, bm));
        Assert.Equal(200, again.Status);
        Assert.False(Replies.Body(again).GetProperty("shareCleared").GetBoolean());
    }

    [Fact]
    public async Task Transcribe_SignedOutNeedsLogin_SignedInPending_NoNarration404()
    {
        using var h = new NarrationHarness();
        await h.InitAsync();
        var (bm, _) = await h.ClipAsync();
        var target = new NarrationTargetBody(NarrationHarness.GameId, bm);

        Assert.Equal(404, (await h.Commands().TranscribeAsync(target)).Status);

        await h.Commands().SaveAsync(h.SaveBody(bm, h.Voice()), default);
        var signedOut = Replies.Body(await h.Commands().TranscribeAsync(target));
        Assert.False(signedOut.GetProperty("ok").GetBoolean());
        Assert.True(signedOut.GetProperty("needsLogin").GetBoolean());
        Assert.Equal("needs_login", signedOut.GetProperty("transcriptStatus").GetString());

        h.SignIn();
        var before = (await h.Narrations.GetAsync(bm))!.TranscriptGeneration;
        var signedIn = Replies.Body(await h.Commands().TranscribeAsync(target));
        Assert.True(signedIn.GetProperty("ok").GetBoolean());
        Assert.Equal("pending", signedIn.GetProperty("transcriptStatus").GetString());
        var after = (await h.Narrations.GetAsync(bm))!;
        Assert.Equal(TranscriptStatuses.Pending, after.TranscriptStatus);
        Assert.Equal(before + 1, after.TranscriptGeneration);
    }

    // ── Share request (6.2) and status (6.2b) ────────────────────────────────

    private static Task<ApiReply> Upload(NarrationHarness h, long bm) =>
        ClipShareRequests.UploadAsync(new ShareClipBody(NarrationHarness.GameId, bm, "Ahri", null), h.Scope.Vod, h.Narrations,
            h.Scope.Games, h.Scope.Config, h.Share, NullLogger.Instance);

    [Fact]
    public async Task Upload_OverTenMinutes_Is422WithTheExactText_AndNotRetryable()
    {
        using var h = new NarrationHarness();
        await h.InitAsync();
        h.SignIn();
        var (bm, _) = await h.ClipAsync(start: 100, end: 701);

        var reply = await Upload(h, bm);

        Assert.Equal(422, reply.Status);
        var body = Replies.Body(reply);
        Assert.Equal("Clips can be up to 10 minutes. Trim the range and save a new clip.", body.GetProperty("error").GetString());
        Assert.False(body.GetProperty("retryable").GetBoolean());
        Assert.False(h.Share.IsActive(bm));
    }

    [Theory]
    [InlineData(false, ".mp4", "Clip file is missing. Save the clip again.")]
    [InlineData(true, ".mkv", "Only MP4 and WebM clips can be shared.")]
    public async Task Upload_FileProblems_Are422(bool onDisk, string ext, string expected)
    {
        using var h = new NarrationHarness();
        await h.InitAsync();
        h.SignIn();
        var (bm, _) = await h.ClipAsync(onDisk: onDisk, ext: ext);

        var reply = await Upload(h, bm);

        Assert.Equal(422, reply.Status);
        Assert.Equal(expected, Replies.Body(reply).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Upload_ReturnsAcceptedWithAJobId_Idempotently_OrNeedsLogin_OrAlreadyShared()
    {
        using var h = new NarrationHarness();
        await h.InitAsync();
        var (bm, _) = await h.ClipAsync();

        var signedOut = Replies.Body(await Upload(h, bm));
        Assert.True(signedOut.GetProperty("needsLogin").GetBoolean());

        h.SignIn();
        var accepted = await Upload(h, bm);
        Assert.Equal(200, accepted.Status);
        var body = Replies.Body(accepted);
        Assert.True(body.GetProperty("accepted").GetBoolean());
        Assert.Equal(bm.ToString(), body.GetProperty("jobId").GetString());
        Assert.False(body.GetProperty("narrated").GetBoolean());
        Assert.Equal("queued", h.Share.GetStatus(bm).State);

        var again = Replies.Body(await Upload(h, bm));
        Assert.True(again.GetProperty("accepted").GetBoolean());

        await h.Scope.Vod.SetBookmarkShareUrlAsync(bm, "https://revu.lol/done123");
        var shared = Replies.Body(await Upload(h, bm));
        Assert.True(shared.GetProperty("alreadyShared").GetBoolean());
        Assert.Equal("https://revu.lol/done123", shared.GetProperty("url").GetString());
        Assert.False(shared.GetProperty("transcriptAttached").GetBoolean());
    }

    [Fact]
    public async Task Upload_PrefersTheNarratedRender_EvenWhenTheSourceClipIsGone()
    {
        using var h = new NarrationHarness();
        await h.InitAsync();
        var (bm, clip) = await h.ClipAsync();
        await h.Commands().SaveAsync(h.SaveBody(bm, h.Voice()), default);
        File.Delete(clip);
        h.SignIn();

        var body = Replies.Body(await Upload(h, bm));

        Assert.True(body.GetProperty("accepted").GetBoolean());
        Assert.True(body.GetProperty("narrated").GetBoolean());
    }

    [Fact]
    public void ShareStatus_IsIdleForAnUnknownBookmark()
    {
        using var h = new NarrationHarness();
        var status = h.Share.GetStatus(999_999);
        Assert.Equal("idle", status.State);
        Assert.Null(status.Phase);
        Assert.Null(status.Url);
        Assert.Null(status.Error);
        Assert.Equal(0, status.SentBytes);
    }
}

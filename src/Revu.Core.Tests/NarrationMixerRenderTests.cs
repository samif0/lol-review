using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// Real ffmpeg renders (skipped without ffmpeg): the narrated MP4 keeps the full clip
/// length even when the voice is shorter, in every audio branch.
/// </summary>
public sealed class NarrationMixerRenderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Revu.Core.Tests", "mix-" + Guid.NewGuid().ToString("N"));
    private readonly NarrationMixer _mixer = new(NullLogger<NarrationMixer>.Instance);

    public NarrationMixerRenderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private async Task<(string Clip, string Voice)> MediaAsync(bool clipAudio)
    {
        var clip = Path.Combine(_dir, clipAudio ? "clip.mp4" : "silent.mp4");
        if (clipAudio)
        {
            await FfmpegTestMedia.RunAsync("-hide_banner", "-y", "-f", "lavfi", "-i", "testsrc=size=320x180:rate=30:duration=8",
                "-f", "lavfi", "-i", "sine=frequency=220:sample_rate=48000:duration=8",
                "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-shortest", clip);
        }
        else
        {
            await FfmpegTestMedia.RunAsync("-hide_banner", "-y", "-f", "lavfi", "-i", "testsrc=size=320x180:rate=30:duration=8",
                "-c:v", "libx264", "-preset", "ultrafast", clip);
        }
        var voice = Path.Combine(_dir, "voice.webm");
        if (!File.Exists(voice))
        {
            await FfmpegTestMedia.RunAsync("-hide_banner", "-y", "-f", "lavfi", "-i",
                "sine=frequency=660:sample_rate=48000:duration=3", "-c:a", "libopus", voice);
        }
        return (clip, voice);
    }

    [FfmpegFact]
    public async Task Render_DuckOn_KeepsTheWholeClip_AndWritesAnMp4()
    {
        var (clip, voice) = await MediaAsync(clipAudio: true);
        var probe = await _mixer.ProbeAsync(clip, CancellationToken.None);
        Assert.True(probe!.HasAudio);

        var output = Path.Combine(_dir, "narrated", "clip_narrated.mp4");
        var ok = await _mixer.MixAsync(new MixPlan(clip, voice, output, 500, 0.8, 1.0, true, true, probe.DurationSeconds),
            CancellationToken.None);

        Assert.True(ok);
        Assert.True(File.Exists(output));
        Assert.False(File.Exists(output + ".part.mp4"));
        var rendered = await _mixer.ProbeAsync(output, CancellationToken.None);
        Assert.True(rendered!.HasAudio);
        Assert.InRange(rendered.DurationSeconds!.Value, 7.5, 8.6);
    }

    [FfmpegFact]
    public async Task Render_DuckOffAndNegativeOffset_KeepsTheWholeClip()
    {
        var (clip, voice) = await MediaAsync(clipAudio: true);
        var output = Path.Combine(_dir, "narrated", "clip_off.mp4");

        Assert.True(await _mixer.MixAsync(new MixPlan(clip, voice, output, -400, 1.0, 1.5, false, true, 8),
            CancellationToken.None));
        Assert.InRange((await _mixer.ProbeAsync(output, CancellationToken.None))!.DurationSeconds!.Value, 7.5, 8.6);
    }

    [FfmpegFact]
    public async Task Render_ClipWithoutAudio_PadsTheVoiceToTheClipLength()
    {
        var (clip, voice) = await MediaAsync(clipAudio: false);
        var probe = await _mixer.ProbeAsync(clip, CancellationToken.None);
        Assert.False(probe!.HasAudio);
        var output = Path.Combine(_dir, "narrated", "silent_narrated.mp4");

        Assert.True(await _mixer.MixAsync(new MixPlan(clip, voice, output, 0, 0.8, 1.0, true, false, probe.DurationSeconds),
            CancellationToken.None));
        var rendered = await _mixer.ProbeAsync(output, CancellationToken.None);
        Assert.True(rendered!.HasAudio);
        Assert.InRange(rendered.DurationSeconds!.Value, 7.5, 8.6);
    }

    [FfmpegFact]
    public async Task Render_Cancelled_DeletesPartialOutput_AndThrows()
    {
        var (clip, voice) = await MediaAsync(clipAudio: true);
        var output = Path.Combine(_dir, "narrated", "cancelled.mp4");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _mixer.MixAsync(new MixPlan(clip, voice, output, 0, 0.8, 1.0, true, true, 8), cts.Token));
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(output + ".part.mp4"));
    }

    [FfmpegFact]
    public async Task Render_BadInput_ReturnsFalse_WithNoOutput()
    {
        var bogus = Path.Combine(_dir, "bogus.mp4");
        await File.WriteAllTextAsync(bogus, "not a video");
        var (_, voice) = await MediaAsync(clipAudio: true);
        var output = Path.Combine(_dir, "narrated", "bogus_narrated.mp4");

        Assert.False(await _mixer.MixAsync(new MixPlan(bogus, voice, output, 0, 0.8, 1.0, true, true, 8), CancellationToken.None));
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(output + ".part.mp4"));
    }
}

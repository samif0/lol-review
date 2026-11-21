using System.Text.Json;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// The HEVC choice of a full-game share: it goes up as HLS segments from a hardware encoder at about 0.6 of the
/// H.264 size, and becomes H.264 (with a reason the sheet shows) when this PC has no such encoder or the share
/// goes up as one MP4. The sheet's status says what it needs to offer the choice.
/// </summary>
public sealed partial class VodShareWorkerTests
{
    private static async Task<JsonElement> StartHevcAsync(VodShareHarness h, string codec = "hevc")
    {
        var reply = await h.Worker.StartAsync(GameId, "1080p", false, CancellationToken.None, codec);
        Assert.Equal(200, reply.Status);
        return Body(reply);
    }

    [Fact]
    public async Task AnHevcShare_EncodesHevc_ReservesItsSmallerSize_AndSaysSoEverywhere()
    {
        var (h, tap) = await StartHlsAsync();
        using (h)
        using (tap)
        {
            var accepted = await StartHevcAsync(h);
            Assert.Equal(("1080p", "hevc", JsonValueKind.Null), (accepted.GetProperty("quality").GetString(),
                accepted.GetProperty("codec").GetString(), accepted.GetProperty("codecReason").ValueKind));
            var done = await WaitPhaseAsync(tap, "done");
            Assert.Equal(("hls", "hevc", JsonValueKind.Null), (done.GetProperty("format").GetString(),
                done.GetProperty("codec").GetString(), done.GetProperty("codecReason").ValueKind));

            var (request, declared) = Assert.Single(h.Client.HlsStarts);
            // The proxy is told the resolution and frame rate, as for H.264: it never needs the codec.
            Assert.Equal(new VodUploadRequest("1080p", 2256, 30), request);
            var reserved = VodShareEncoderArgs.ReserveBytes(VodShareQuality.P1080, 30, 2255.4, VodShareCodec.Hevc);
            Assert.Equal(reserved, declared);
            Assert.True(reserved < 0.66 * VodShareEncoderArgs.ReserveBytes(VodShareQuality.P1080, 30, 2255.4));
            Assert.Equal(VodShareCodec.Hevc, Assert.Single(h.Encoder.HlsEncodes).Codec);

            Assert.Equal("hevc", h.Store.Get(GameId)!.Codec);
            Assert.Equal("hevc", VodShareLinkDto.From(h.Store.Get(GameId)!).Codec);
            Assert.Equal("hevc", h.Memory.PreferredCodec());
            Assert.Equal("hevc", (await h.Worker.GetStatusAsync(GameId, CancellationToken.None)).PreferredCodec);
        }
    }

    [Fact]
    public async Task WithoutAHardwareHevcEncoder_TheShareIsH264_AndTheReasonIsTheEncoder()
    {
        var (h, tap) = await StartHlsAsync();
        using (h)
        using (tap)
        {
            h.Encoder.HevcEncoder = false;
            var accepted = await StartHevcAsync(h);
            Assert.Equal(("h264", "encoder"), (accepted.GetProperty("codec").GetString(), accepted.GetProperty("codecReason").GetString()));
            var done = await WaitPhaseAsync(tap, "done");
            Assert.Equal(("hls", "h264", "encoder"), (done.GetProperty("format").GetString(), done.GetProperty("codec").GetString(),
                done.GetProperty("codecReason").GetString()));

            Assert.Equal(VodShareEncoderArgs.ReserveBytes(VodShareQuality.P1080, 30, 2255.4), Assert.Single(h.Client.HlsStarts).Declared);
            Assert.Equal(VodShareCodec.H264, Assert.Single(h.Encoder.HlsEncodes).Codec);
            Assert.Equal("h264", h.Store.Get(GameId)!.Codec);
            // What the player picked is remembered, not what this PC could do.
            Assert.Equal("hevc", h.Memory.PreferredCodec());
        }
    }

    [Fact]
    public async Task AShareThatGoesUpAsOneMp4_IsH264_AndTheReasonIsTheFormat()
    {
        var (h, tap) = await StartAsync();   // the proxy offers no HLS here
        using (h)
        using (tap)
        {
            var accepted = await StartHevcAsync(h);
            Assert.Equal(("h264", "format"), (accepted.GetProperty("codec").GetString(), accepted.GetProperty("codecReason").GetString()));
            var done = await WaitPhaseAsync(tap, "done");
            Assert.Equal(("mp4", "h264", "format"), (done.GetProperty("format").GetString(), done.GetProperty("codec").GetString(),
                done.GetProperty("codecReason").GetString()));
            Assert.Empty(h.Client.HlsStarts);
            Assert.Empty(h.Encoder.HlsEncodes);
            Assert.Equal(VodShareQuality.P1080, h.Encoder.Encodes.Single().Quality);
        }
    }

    [Fact]
    public async Task AnH264Share_SaysH264_AndAnUnknownCodecIsABadRequest()
    {
        var (h, tap) = await StartHlsAsync();
        using (h)
        using (tap)
        {
            var bad = await h.Worker.StartAsync(GameId, "1080p", false, CancellationToken.None, "av1");
            Assert.Equal(400, bad.Status);
            Assert.Null(h.Memory.PreferredCodec());

            var accepted = await StartShareAsync(h);   // no codec: H.264, as before
            Assert.Equal(("h264", JsonValueKind.Null), (accepted.GetProperty("codec").GetString(), accepted.GetProperty("codecReason").ValueKind));
            await WaitPhaseAsync(tap, "done");
            Assert.Equal(VodShareCodec.H264, Assert.Single(h.Encoder.HlsEncodes).Codec);
            Assert.Equal("h264", h.Memory.PreferredCodec());
        }
    }

    [Fact]
    public async Task TheStatus_OffersHevcWithItsSizes_OnlyWhenTheShareWouldBeHls()
    {
        var (h, tap) = await StartHlsAsync();
        using (h)
        using (tap)
        {
            var first = await h.Worker.GetStatusAsync(GameId, CancellationToken.None);
            var hevc = first.Hevc!;
            // (2700 * 1.10 + 128) * 125 * 2255.4 at 1080p30, (1500 * 1.10 + 128) * 125 * 2255.4 at 720p30.
            Assert.Equal(new VodShareEstimatesDto(873_403_650, 501_262_650), hevc.Estimates);
            Assert.True(hevc.Estimates!.P1080Bytes < 0.66 * first.Estimates!.P1080Bytes);
            Assert.True(hevc.Pool!.Fits1080);
            Assert.Null(first.PreferredCodec);

            // Opening the sheet also looks for the HEVC encoder; the next read knows the answer.
            await WaitUntilAsync(() => Volatile.Read(ref h.Encoder.HevcPrewarms) == 1);
            Assert.True((await h.Worker.GetStatusAsync(GameId, CancellationToken.None)).Hevc!.Available);
        }

        var (noHls, tap2) = await StartAsync();
        using (noHls)
        using (tap2)
        {
            Assert.Null((await noHls.Worker.GetStatusAsync(GameId, CancellationToken.None)).Hevc);
        }
    }

    [Fact]
    public async Task TheStatus_SaysWhenThisPcHasNoHevcEncoder()
    {
        var (h, tap) = await StartHlsAsync();
        using (h)
        using (tap)
        {
            h.Encoder.HevcEncoder = false;
            await h.Worker.GetStatusAsync(GameId, CancellationToken.None);
            await WaitUntilAsync(() => Volatile.Read(ref h.Encoder.HevcPrewarms) == 1);
            Assert.False((await h.Worker.GetStatusAsync(GameId, CancellationToken.None)).Hevc!.Available);
        }
    }

    [Fact]
    public async Task ASlowUplink_Preselects720pForH264_ButNotForTheSmallerHevcCopy()
    {
        var (h, tap) = await StartHlsAsync();
        using (h)
        using (tap)
        {
            // An upload that carries 1.2x the HEVC 1080p rate: under H.264's 1.1x bar, over HEVC's.
            var rate = VodShareWorker.Rate1080BytesPerSecond(30, 2255.4, VodShareCodec.Hevc) * 1.2;
            Assert.True(rate < VodShareWorker.PreselectRatio * VodShareWorker.Rate1080BytesPerSecond(30, 2255.4));
            h.Memory.SetUplink(await h.Worker.AccountKeyAsync(), rate);
            var status = await h.Worker.GetStatusAsync(GameId, CancellationToken.None);
            Assert.Equal("720p", status.PreferredQuality);
            Assert.Null(status.Hevc!.PreferredQuality);
        }
    }
}

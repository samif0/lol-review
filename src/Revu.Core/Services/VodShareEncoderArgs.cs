#nullable enable

using System.Globalization;
using Revu.Core.Constants;

namespace Revu.Core.Services;

/// <summary>Share copy resolution. Wire strings are "1080p" and "720p".</summary>
public enum VodShareQuality { P1080, P720 }

/// <summary>
/// Pure ffmpeg argument building for the full-game share copy (contract 7.3): H.264 VBR at the
/// source's frame rate (60 or 30) with a 2 s GOP, AAC-LC 128k stereo, the moov at the end,
/// starting at the source's video time 0. Invariant culture throughout.
/// </summary>
public static class VodShareEncoderArgs
{
    /// <summary>Hardware first; libx264 (CPU) last.</summary>
    public static readonly IReadOnlyList<string> EncoderPriority =
        ["h264_nvenc", "h264_amf", "h264_qsv", "h264_mf", "libx264"];

    public static string Wire(VodShareQuality quality) => quality == VodShareQuality.P720 ? "720p" : "1080p";

    /// <summary>The resolution with its frame rate, as players see it: "1080p60", "720p30".</summary>
    public static string Label(VodShareQuality quality, int fps) =>
        Wire(quality) + Fps(fps).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The share copy's frame rate for a source running at <paramref name="sourceFps"/>: 60 from
    /// 50 fps up (a faster source is capped at 60), else 30. Unknown is 30.
    /// </summary>
    public static int Fps(double? sourceFps) => sourceFps is { } f && double.IsFinite(f) && f >= 50 ? 60 : 30;

    /// <summary>"1080p" or "720p" (case-insensitive, trimmed). Anything else is false.</summary>
    public static bool TryParseQuality(string? wire, out VodShareQuality quality)
    {
        switch ((wire ?? "").Trim().ToLowerInvariant())
        {
            case "1080p": quality = VodShareQuality.P1080; return true;
            case "720p": quality = VodShareQuality.P720; return true;
            default: quality = VodShareQuality.P1080; return false;
        }
    }

    /// <summary>The box the video is fitted into (never upscaled).</summary>
    public static (int Width, int Height) Box(VodShareQuality quality) =>
        quality == VodShareQuality.P720 ? (1280, 720) : (1920, 1080);

    public static string PixelFormat(string encoder) => encoder == "libx264" ? "yuv420p" : "nv12";

    /// <summary>The video bitrate before a long game is fitted (see the GameConstants rates).</summary>
    public static int NominalKbps(VodShareQuality quality, int fps) => (quality, Fps(fps)) switch
    {
        (VodShareQuality.P1080, 60) => GameConstants.VodShareVideoKbps1080p60,
        (VodShareQuality.P1080, _) => GameConstants.VodShareVideoKbps1080p30,
        (_, 60) => GameConstants.VodShareVideoKbps720p60,
        _ => GameConstants.VodShareVideoKbps720p30,
    };

    /// <summary>
    /// The highest video bitrate that keeps a game of <paramref name="durationSeconds"/> under
    /// 1.9 GiB: <c>floor(((2040109465 * 8 / d) / 1000 - 128) / 1.04)</c>; int.MaxValue when the
    /// length is unknown.
    /// </summary>
    public static int FitKbps(double durationSeconds)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0) return int.MaxValue;
        var fit = Math.Floor(((GameConstants.VodShareTargetMaxBytes * 8.0 / durationSeconds) / 1000.0
            - GameConstants.VodShareAudioKbps) / GameConstants.VodShareOverhead);
        return (int)Math.Clamp(fit, int.MinValue, int.MaxValue);
    }

    /// <summary>The share copy's video bitrate: the nominal rate, lowered to fit a long game under 1.9 GiB.</summary>
    public static int VideoKbps(VodShareQuality quality, int fps, double durationSeconds) =>
        Math.Min(NominalKbps(quality, fps), FitKbps(durationSeconds));

    /// <summary>True when the game is too long for even the lowest allowed 720p bitrate.</summary>
    public static bool IsTooLong(double durationSeconds) =>
        durationSeconds > GameConstants.MaxVodShareSeconds || FitKbps(durationSeconds) < GameConstants.VodShareMinVideoKbps;

    /// <summary>True when a long game would have to drop 1080p below its floor to fit (then it is shared at 720p).</summary>
    public static bool TooLongFor1080(int fps, double durationSeconds) =>
        VideoKbps(VodShareQuality.P1080, fps, durationSeconds) < GameConstants.VodShareMin1080VideoKbps;

    /// <summary>
    /// Expected copy size in bytes: <c>ceil(d * (kbps * 1.04 + 128) * 125)</c>, the bitrate plus
    /// the overshoot the fit allows for. Measured real copies land just under it.
    /// </summary>
    public static long EstimateBytes(VodShareQuality quality, int fps, double durationSeconds)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0) return 0;
        durationSeconds = Math.Min(durationSeconds, 1e9);   // a corrupt probe must not overflow
        // Decimal keeps the estimate exact (no 1-byte drift from binary fractions of a second).
        var kbps = VideoKbps(quality, fps, durationSeconds) * (decimal)GameConstants.VodShareOverhead
            + GameConstants.VodShareAudioKbps;
        return (long)Math.Ceiling((decimal)durationSeconds * kbps * 125m);
    }

    /// <summary>Video bitrate, maxrate and bufsize in kbps.</summary>
    public static (int Bitrate, int MaxRate, int BufSize) Rates(VodShareQuality quality, int fps, double durationSeconds)
    {
        var v = VideoKbps(quality, fps, durationSeconds);
        return (v, (int)Math.Round(1.5 * v, MidpointRounding.AwayFromZero), 2 * v);
    }

    /// <summary>
    /// The encoder-specific video arguments (the &lt;VIDEO ARGS&gt; block). NVENC runs preset p4
    /// without multipass: on a 1080p60 recording that encoded 1.7x faster than p5 with
    /// quarter-resolution multipass and scored the same VMAF (94.5 vs 94.2 at 6 Mbps).
    /// </summary>
    public static IReadOnlyList<string> VideoArgs(string encoder, VodShareQuality quality, int fps, double durationSeconds)
    {
        var (b, max, buf) = Rates(quality, fps, durationSeconds);
        string[] rates = ["-b:v", Kbps(b), "-maxrate", Kbps(max), "-bufsize", Kbps(buf)];
        var gop = (2 * Fps(fps)).ToString(CultureInfo.InvariantCulture);   // a keyframe every 2 s
        return encoder switch
        {
            "h264_nvenc" => ["-c:v", "h264_nvenc", "-preset", "p4", "-tune", "hq", "-rc", "vbr",
                .. rates, "-spatial_aq", "1", "-temporal_aq", "1", "-bf", "3", "-g", gop, "-profile:v", "high"],
            "h264_amf" => ["-c:v", "h264_amf", "-usage", "transcoding", "-quality", "balanced", "-rc", "vbr_peak",
                .. rates, "-g", gop, "-profile:v", "high"],
            "h264_qsv" => ["-c:v", "h264_qsv", "-preset", "medium", .. rates, "-g", gop, "-profile:v", "high"],
            "h264_mf" => ["-c:v", "h264_mf", "-rate_control", "pc_vbr", .. rates, "-g", gop],
            // -threads 8 is mandatory: default threading segfaulted twice on long inputs. veryfast:
            // medium managed 2.2x real time on 1080p60 with 8 cores, veryfast 5.1x for 2 VMAF less.
            "libx264" => ["-c:v", "libx264", "-preset", "veryfast", "-threads", "8", .. rates, "-g", gop, "-profile:v", "high"],
            _ => throw new ArgumentOutOfRangeException(nameof(encoder), encoder, "Unsupported share encoder."),
        };
    }

    /// <summary>The fps/scale/format filter: the share frame rate, fit inside the box, even dimensions.</summary>
    public static string VideoFilter(string encoder, VodShareQuality quality, int fps)
    {
        var (w, h) = Box(quality);
        return $"fps={Fps(fps).ToString(CultureInfo.InvariantCulture)},scale=w='min({w},iw)':h='min({h},ih)'"
            + $":force_original_aspect_ratio=decrease:force_divisible_by=2:flags=bicubic,format={PixelFormat(encoder)}";
    }

    /// <summary>A source start_time above this gets <c>-output_ts_offset</c> (contract 7.3).</summary>
    public const double StartOffsetThresholdSeconds = 0.05;

    /// <summary>
    /// The full encode argument list in contract order. No -ss, -t or -copyts: ffmpeg rebases the
    /// output to start at 0, which matches a built-in recording (start_time 0, checked within
    /// 0.05 s on a real one). A source that starts later (<paramref name="sourceStartSeconds"/>
    /// above 0.05 s, for example an imported recording) gets <c>-output_ts_offset</c> so the copy
    /// keeps the source's timeline and gameTimeAtVideoStart still lines the markers up.
    /// </summary>
    public static IReadOnlyList<string> EncodeArgs(string encoder, string sourcePath, string outputPath,
        VodShareQuality quality, int fps, double durationSeconds, double sourceStartSeconds = 0)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-nostats", "-progress", "pipe:1",
            "-i", sourcePath,
            "-map", "0:v:0", "-map", "0:a:0?",
            "-map_metadata", "-1", "-map_chapters", "-1", "-sn", "-dn",
            "-vf", VideoFilter(encoder, quality, fps),
        };
        args.AddRange(VideoArgs(encoder, quality, fps, durationSeconds));
        args.AddRange(["-c:a", "aac", "-b:a", "128k", "-ac", "2", "-ar", "48000"]);
        // No -avoid_negative_ts make_zero: with B-frames it shifts the whole
        // output by the reorder delay (0.1 s measured with NVENC -bf 3). Left alone, the mp4
        // muxer writes an edit list and the copy starts at 0.000 exactly like the source.
        args.AddRange(["-max_muxing_queue_size", "1024"]);
        if (double.IsFinite(sourceStartSeconds) && sourceStartSeconds > StartOffsetThresholdSeconds)
            args.AddRange(["-output_ts_offset", sourceStartSeconds.ToString("0.######", CultureInfo.InvariantCulture)]);
        // No +faststart: the moov goes at the end, so every byte past the header is final once
        // written and the upload can send parts while the encode runs. Browsers fetch the moov
        // with one extra range request.
        args.AddRange(["-f", "mp4", outputPath]);
        return args;
    }

    /// <summary>
    /// Bytes an upload that starts with the encode reserves: the estimate plus 3%, the same margin
    /// the sheet's pool check plans for. Measured copies landed between 3.5% under and 0.2% over
    /// the estimate (a fight-heavy 2-minute cut), so the copy is padded up to this size; one that
    /// still comes out larger is uploaded again on its own.
    /// </summary>
    public static long ReserveBytes(VodShareQuality quality, int fps, double durationSeconds) =>
        (long)Math.Ceiling(EstimateBytes(quality, fps, durationSeconds) * 1.03m);

    /// <summary>The 1 s functional probe of an encoder (synthetic black 720p, no output) with 1080p60 settings.</summary>
    public static IReadOnlyList<string> ProbeArgs(string encoder)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=1280x720:r=30",
            "-frames:v", "30", "-vf", "format=" + PixelFormat(encoder),
        };
        args.AddRange(VideoArgs(encoder, VodShareQuality.P1080, 60, 60));
        args.AddRange(["-an", "-f", "null", "-"]);
        return args;
    }

    /// <summary>Arguments that list a binary's encoders.</summary>
    public static IReadOnlyList<string> ListEncodersArgs { get; } = ["-hide_banner", "-encoders"];

    /// <summary>True when an <c>-encoders</c> line's second whitespace-separated token is <paramref name="encoder"/>.</summary>
    public static bool IsListed(IEnumerable<string> lines, string encoder) =>
        lines.Any(line =>
        {
            var tokens = (line ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return tokens.Length >= 2 && tokens[1] == encoder;
        });

    /// <summary>
    /// The first video stream's frame rate from an <c>ffmpeg -i</c> summary ("..., 60 fps, 60 tbr,"),
    /// falling back to its tbr; null when neither is there.
    /// </summary>
    public static double? ParseFrameRate(string? stderr)
    {
        var line = System.Text.RegularExpressions.Regex.Match(stderr ?? "", @"Stream #\d+:\d+[^\r\n]*?: Video:[^\r\n]*");
        if (!line.Success) return null;
        // "90k tbr" (a cover image, a raw stream) has a k suffix and never matches.
        var m = System.Text.RegularExpressions.Regex.Match(line.Value, @", (\d+(?:\.\d+)?) fps\b");
        if (!m.Success) m = System.Text.RegularExpressions.Regex.Match(line.Value, @", (\d+(?:\.\d+)?) tbr\b");
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            && double.IsFinite(v) && v > 0 ? v : null;
    }

    /// <summary>
    /// The container start time from an <c>ffmpeg -i</c> summary ("Duration: ..., start: 4.956000,");
    /// null when it is missing or N/A.
    /// </summary>
    public static double? ParseStartSeconds(string? stderr)
    {
        var m = System.Text.RegularExpressions.Regex.Match(stderr ?? "", @"Duration: [^,\r\n]*, start: (-?\d+(?:\.\d+)?)");
        if (!m.Success) return null;
        return double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? v : null;
    }

    /// <summary>Encode timeout: <c>clamp(ceil(d * 2) + 300, 600, 14700)</c> seconds.</summary>
    public static int TimeoutSeconds(double durationSeconds)
    {
        var d = double.IsFinite(durationSeconds) && durationSeconds > 0 ? durationSeconds : 0;
        return (int)Math.Clamp(Math.Ceiling(d * 2) + 300, 600, 14700);
    }

    /// <summary>
    /// The percent a <c>-progress</c> line reports: <c>out_time_us</c> as 0..99 of the duration,
    /// <c>progress=end</c> as 100; null for any other line (including <c>out_time_us=N/A</c>).
    /// </summary>
    public static int? ParseProgressPercent(string? line, double durationSeconds)
    {
        if (string.IsNullOrEmpty(line)) return null;
        line = line.Trim();
        if (line == "progress=end") return 100;
        const string key = "out_time_us=";
        if (!line.StartsWith(key, StringComparison.Ordinal)) return null;
        if (!long.TryParse(line.AsSpan(key.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us))
            return null;
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0) return 0;
        var pct = Math.Floor(us / 1e6 / durationSeconds * 100);
        return (int)Math.Clamp(pct, 0, 99);
    }

    private static string Kbps(int value) => value.ToString(CultureInfo.InvariantCulture) + "k";
}

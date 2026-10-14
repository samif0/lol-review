#nullable enable

using System.Globalization;
using Revu.Core.Constants;

namespace Revu.Core.Services;

/// <summary>Share copy quality. Wire strings are "1080p" and "720p".</summary>
public enum VodShareQuality { P1080, P720 }

/// <summary>
/// Pure ffmpeg argument building for the full-game share copy (contract 7.3): H.264 30 fps VBR
/// with a 2 s GOP, AAC-LC 128k stereo, +faststart, starting at the source's video time 0.
/// Invariant culture throughout.
/// </summary>
public static class VodShareEncoderArgs
{
    /// <summary>Hardware first; libx264 (CPU) last.</summary>
    public static readonly IReadOnlyList<string> EncoderPriority =
        ["h264_nvenc", "h264_amf", "h264_qsv", "h264_mf", "libx264"];

    public static string Wire(VodShareQuality quality) => quality == VodShareQuality.P720 ? "720p" : "1080p";

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

    /// <summary>
    /// The 720p video bitrate that keeps a game of <paramref name="durationSeconds"/> under
    /// 1.9 GiB: <c>min(2500, floor(((2040109465 * 8 / d) / 1000 - 128) / 1.04))</c>. Below
    /// <see cref="GameConstants.VodShareMinVideoKbps"/> the game is too long to share.
    /// </summary>
    public static int VideoKbps720(double durationSeconds)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0) return GameConstants.VodShareVideoKbps720;
        var fit = Math.Floor(((GameConstants.VodShareTargetMaxBytes * 8.0 / durationSeconds) / 1000.0
            - GameConstants.VodShareAudioKbps) / 1.04);
        return (int)Math.Min(GameConstants.VodShareVideoKbps720, Math.Max(fit, int.MinValue));
    }

    /// <summary>True when the game is too long for even the lowest allowed 720p bitrate.</summary>
    public static bool IsTooLong(double durationSeconds) =>
        durationSeconds > GameConstants.MaxVodShareSeconds || VideoKbps720(durationSeconds) < GameConstants.VodShareMinVideoKbps;

    /// <summary>Video bitrate, maxrate and bufsize in kbps.</summary>
    public static (int Bitrate, int MaxRate, int BufSize) Rates(VodShareQuality quality, double durationSeconds)
    {
        if (quality == VodShareQuality.P1080)
            return (GameConstants.VodShareVideoKbps1080, 6750, 9000);
        var v = VideoKbps720(durationSeconds);
        return (v, (int)Math.Round(1.5 * v, MidpointRounding.AwayFromZero), 2 * v);
    }

    /// <summary>The encoder-specific video arguments (the &lt;VIDEO ARGS&gt; block).</summary>
    public static IReadOnlyList<string> VideoArgs(string encoder, VodShareQuality quality, double durationSeconds)
    {
        var (b, max, buf) = Rates(quality, durationSeconds);
        string[] rates = ["-b:v", Kbps(b), "-maxrate", Kbps(max), "-bufsize", Kbps(buf)];
        return encoder switch
        {
            "h264_nvenc" => ["-c:v", "h264_nvenc", "-preset", "p5", "-tune", "hq", "-multipass", "qres", "-rc", "vbr",
                .. rates, "-spatial_aq", "1", "-temporal_aq", "1", "-bf", "3", "-g", "60", "-profile:v", "high"],
            "h264_amf" => ["-c:v", "h264_amf", "-usage", "transcoding", "-quality", "balanced", "-rc", "vbr_peak",
                .. rates, "-g", "60", "-profile:v", "high"],
            "h264_qsv" => ["-c:v", "h264_qsv", "-preset", "medium", .. rates, "-g", "60", "-profile:v", "high"],
            "h264_mf" => ["-c:v", "h264_mf", "-rate_control", "pc_vbr", .. rates, "-g", "60"],
            // -threads 8 is mandatory: default threading segfaulted twice on long inputs.
            "libx264" => ["-c:v", "libx264", "-preset", "medium", "-threads", "8", .. rates, "-g", "60", "-profile:v", "high"],
            _ => throw new ArgumentOutOfRangeException(nameof(encoder), encoder, "Unsupported share encoder."),
        };
    }

    /// <summary>The scale/fps/format filter: 30 fps, fit inside the box, even dimensions.</summary>
    public static string VideoFilter(string encoder, VodShareQuality quality)
    {
        var (w, h) = Box(quality);
        return $"fps=30,scale=w='min({w},iw)':h='min({h},ih)':force_original_aspect_ratio=decrease"
            + $":force_divisible_by=2:flags=bicubic,format={PixelFormat(encoder)}";
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
        VodShareQuality quality, double durationSeconds, double sourceStartSeconds = 0)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-nostats", "-progress", "pipe:1",
            "-i", sourcePath,
            "-map", "0:v:0", "-map", "0:a:0?",
            "-map_metadata", "-1", "-map_chapters", "-1", "-sn", "-dn",
            "-vf", VideoFilter(encoder, quality),
        };
        args.AddRange(VideoArgs(encoder, quality, durationSeconds));
        args.AddRange(["-c:a", "aac", "-b:a", "128k", "-ac", "2", "-ar", "48000"]);
        // No -avoid_negative_ts make_zero: with B-frames it shifts the whole
        // output by the reorder delay (0.1 s measured with NVENC -bf 3). Left alone, the mp4
        // muxer writes an edit list and the copy starts at 0.000 exactly like the source.
        args.AddRange(["-max_muxing_queue_size", "1024"]);
        if (double.IsFinite(sourceStartSeconds) && sourceStartSeconds > StartOffsetThresholdSeconds)
            args.AddRange(["-output_ts_offset", sourceStartSeconds.ToString("0.######", CultureInfo.InvariantCulture)]);
        args.AddRange(["-movflags", "+faststart"]);
        args.AddRange(["-f", "mp4", outputPath]);
        return args;
    }

    /// <summary>The 1 s functional probe of an encoder (synthetic black 720p, no output).</summary>
    public static IReadOnlyList<string> ProbeArgs(string encoder)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=1280x720:r=30",
            "-frames:v", "30", "-vf", "format=" + PixelFormat(encoder),
        };
        args.AddRange(VideoArgs(encoder, VodShareQuality.P1080, 60));
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

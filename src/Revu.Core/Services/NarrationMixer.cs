#nullable enable

using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Revu.Core.Constants;

namespace Revu.Core.Services;

/// <summary>What ffmpeg reports about a media file.</summary>
public sealed record MediaProbe(bool HasAudio, double? DurationSeconds);

/// <summary>
/// One narrated-clip render: the clip (input 0) mixed with the voice track (input 1).
/// <see cref="OffsetMs"/> shifts the voice on the clip timeline (positive delays it).
/// </summary>
public sealed record MixPlan(
    string ClipPath,
    string NarrationPath,
    string OutputPath,
    int OffsetMs,
    double GameVolume,
    double NarrationVolume,
    bool Duck,
    bool ClipHasAudio,
    double? ClipDurationSeconds);

public interface INarrationMixer
{
    /// <summary>Probe a media file; null when ffmpeg is missing or the file cannot be read.</summary>
    Task<MediaProbe?> ProbeAsync(string path, CancellationToken ct);

    /// <summary>
    /// Render <paramref name="plan"/>: writes <c>&lt;output&gt;.part.mp4</c> then renames it onto
    /// the output. Returns false on failure (partial output deleted); cancellation deletes the
    /// partial output and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<bool> MixAsync(MixPlan plan, CancellationToken ct);
}

/// <summary>
/// Narrated clip renderer. The video stream is copied untouched; the audio is the game
/// sound (optionally ducked under the voice by a sidechain compressor) mixed with the
/// voice track into 160 kbps AAC. The output is always .mp4.
/// </summary>
public sealed partial class NarrationMixer : INarrationMixer
{
    private const int ProbeTimeoutSeconds = 30;

    [GeneratedRegex(@"Stream #\d+:\d+.*: Audio:")]
    private static partial Regex AudioStreamRegex();

    [GeneratedRegex(@"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)")]
    private static partial Regex DurationRegex();

    private readonly ILogger<NarrationMixer> _logger;

    public NarrationMixer(ILogger<NarrationMixer> logger) => _logger = logger;

    public async Task<MediaProbe?> ProbeAsync(string path, CancellationToken ct)
    {
        var ffmpeg = await FfmpegRunner.FindFfmpegAsync(_logger).ConfigureAwait(false);
        if (ffmpeg is null || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        // No output file: ffmpeg exits non-zero by design and prints the input summary.
        var run = await FfmpegRunner.RunAsync(ffmpeg, ["-hide_banner", "-i", path], ProbeTimeoutSeconds, ct)
            .ConfigureAwait(false);
        if (run.TimedOut) return null;
        return ParseProbe(run.StderrTail);
    }

    /// <summary>Parse the <c>ffmpeg -i</c> summary. Duration is null for <c>N/A</c>.</summary>
    public static MediaProbe ParseProbe(string stderr)
    {
        stderr ??= "";
        var hasAudio = AudioStreamRegex().IsMatch(stderr);
        double? duration = null;
        var m = DurationRegex().Match(stderr);
        if (m.Success)
        {
            duration = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
                + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        }
        return new MediaProbe(hasAudio, duration);
    }

    /// <summary>The ffmpeg argument list for <paramref name="p"/>. Pure; invariant culture.</summary>
    public static IReadOnlyList<string> BuildMixArguments(MixPlan p)
    {
        const string format = "aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo";
        var trim = p.OffsetMs < 0 ? $"atrim=start={Num(-p.OffsetMs / 1000.0)},asetpts=PTS-STARTPTS," : "";
        var delay = p.OffsetMs > 0 ? $",adelay={p.OffsetMs.ToString(CultureInfo.InvariantCulture)}:all=1" : "";
        var voice = $"[1:a]asetpts=PTS-STARTPTS,{trim}highpass=f=80,{format},volume={Num(p.NarrationVolume)}{delay}";
        var game = $"[0:a]{format},volume={Num(p.GameVolume)}[game]";
        const string mix = "amix=inputs=2:duration=first:dropout_transition=0:normalize=0,alimiter=limit=0.95[aout]";

        var shortest = false;
        string graph;
        if (!p.ClipHasAudio)
        {
            if (p.ClipDurationSeconds is { } d && double.IsFinite(d) && d > 0)
            {
                graph = $"{voice},apad=whole_dur={Num(d)},atrim=end={Num(d)}[aout]";
            }
            else
            {
                // Unknown clip length: pad and let the video stream end the output.
                graph = $"{voice},apad[aout]";
                shortest = true;
            }
        }
        else if (p.Duck)
        {
            // apad BEFORE asplit is required: without it the mix ends with the voice.
            graph = $"{voice},apad,asplit=2[sc][voice];{game};"
                + "[game][sc]sidechaincompress=threshold=0.02:ratio=8:attack=15:release=350[ducked];"
                + $"[ducked][voice]{mix}";
        }
        else
        {
            graph = $"{voice},apad[voice];{game};[game][voice]{mix}";
        }

        var args = new List<string>
        {
            "-hide_banner", "-y",
            "-i", p.ClipPath,
            "-i", p.NarrationPath,
            "-filter_complex", graph,
            "-map", "0:v:0",
            "-map", "[aout]",
            "-c:v", "copy",
            "-c:a", "aac",
            "-b:a", "160k",
            "-movflags", "+faststart",
        };
        if (shortest) args.Add("-shortest");
        args.Add(p.OutputPath);
        return args;
    }

    public async Task<bool> MixAsync(MixPlan plan, CancellationToken ct)
    {
        var ffmpeg = await FfmpegRunner.FindFfmpegAsync(_logger).ConfigureAwait(false);
        if (ffmpeg is null)
        {
            _logger.LogError("Narration mix: ffmpeg not found");
            return false;
        }

        var output = Path.ChangeExtension(plan.OutputPath, ".mp4");
        var part = output + ".part.mp4";
        var dir = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var args = BuildMixArguments(plan with { OutputPath = part });
        var timeout = GameConstants.MixTimeoutSeconds(plan.ClipDurationSeconds ?? GameConstants.MaxClipSeconds);
        _logger.LogInformation("Narration mix: {Exe} {Args}", ffmpeg, string.Join(" ", args));

        FfmpegRunResult run;
        try
        {
            run = await FfmpegRunner.RunAsync(ffmpeg, args, timeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryDelete(part);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Narration mix failed to start");
            TryDelete(part);
            return false;
        }

        if (!run.Succeeded || !File.Exists(part) || new FileInfo(part).Length == 0)
        {
            var tail = run.StderrTail.Length > 800 ? run.StderrTail[^800..] : run.StderrTail;
            _logger.LogError("Narration mix failed (rc={Code}, timedOut={TimedOut}): {Stderr}", run.ExitCode, run.TimedOut, tail);
            TryDelete(part);
            return false;
        }

        try
        {
            File.Move(part, output, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Narration mix: could not move the render into place");
            TryDelete(part);
            return false;
        }
    }

    private static string Num(double value) =>
        Math.Round(value, 3, MidpointRounding.AwayFromZero).ToString("0.###", CultureInfo.InvariantCulture);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}

using System.Diagnostics;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// The runner's binary stdout mode (plan 6.1) with cmd.exe standing in for ffmpeg: a consumer fault kills the
/// process and comes back in the result, a consumer that outlives the process is awaited, cancellation and the
/// timeout kill it, and a consumer that stops early never stalls the process on a full pipe.
/// </summary>
public sealed class FfmpegRunnerBinaryTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    /// <summary>Writes forever (a 0 step never reaches the end).</summary>
    private static readonly string[] Endless = ["/c", "for /L %i in (1,0,2) do @echo 0123456789abcdef"];

    [Fact]
    public async Task AConsumerThatThrowsMidStream_KillsTheProcess_AndTheResultCarriesItsError()
    {
        var boom = new InvalidOperationException("a bad box");
        var watch = Stopwatch.StartNew();
        var result = await FfmpegRunner.RunAsync(Cmd, Endless, 120, CancellationToken.None, onStdout: async (stdout, ct) =>
        {
            var buffer = new byte[4096];
            _ = await stdout.ReadAsync(buffer, ct);
            throw boom;
        });

        Assert.Same(boom, result.ConsumerError);
        Assert.False(result.Succeeded);
        Assert.False(result.TimedOut);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task AConsumerThatFinishesAfterTheProcessExited_IsAwaited()
    {
        var done = false;
        var text = "";
        var result = await FfmpegRunner.RunAsync(Cmd, ["/c", "echo hello"], 120, CancellationToken.None, onStdout: async (stdout, ct) =>
        {
            using var reader = new StreamReader(stdout);
            text = await reader.ReadToEndAsync(ct);
            await Task.Delay(700, ct);
            done = true;
        });

        Assert.True(done);
        Assert.True(result.Succeeded, result.StderrTail);
        Assert.Contains("hello", text);
    }

    [Fact]
    public async Task Cancellation_KillsTheProcess_AndThrows()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FfmpegRunner.RunAsync(Cmd, Endless, 120, cts.Token,
            onStdout: (stdout, ct) => stdout.CopyToAsync(Stream.Null, ct)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task TheTimeout_KillsTheProcess_AndReportsTimedOut()
    {
        var result = await FfmpegRunner.RunAsync(Cmd, Endless, 1, CancellationToken.None,
            onStdout: (stdout, ct) => stdout.CopyToAsync(Stream.Null, ct));
        Assert.True(result.TimedOut);
        Assert.Null(result.ConsumerError);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task AConsumerThatStopsEarly_LeavesTheRestDrained()
    {
        // About 420 KB: far more than a pipe holds, so an undrained pipe would stall cmd until the timeout.
        string[] lots = ["/c", "for /L %i in (1,1,10000) do @echo 0123456789012345678901234567890123456789"];
        var result = await FfmpegRunner.RunAsync(Cmd, lots, 120, CancellationToken.None, onStdout: async (stdout, ct) =>
        {
            var buffer = new byte[100];
            _ = await stdout.ReadAsync(buffer, ct);
        });
        Assert.True(result.Succeeded, $"exit {result.ExitCode}, timed out {result.TimedOut}");
    }

    [Fact]
    public async Task ALineConsumerAndABinaryConsumer_AreNotBothAllowed()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => FfmpegRunner.RunAsync(Cmd, ["/c", "echo"], 10,
            CancellationToken.None, onStdoutLine: _ => { }, onStdout: (_, _) => Task.CompletedTask));
    }
}

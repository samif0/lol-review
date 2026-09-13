using System.Text.Json;
using Revu.Core.Services;

namespace Revu.Core.Tests;

public sealed class RecordingTimelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Revu.Timing.Tests", Guid.NewGuid().ToString("N"));
    private string Video => Path.Combine(_root, "match.mp4");
    public RecordingTimelineTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void LegacyVideoAndGeneratedClipKeepZeroOffsetWithoutCreatingMetadata()
    {
        Assert.Equal(0, RecordingTimeline.ReadGameTimeAtVideoStart(Video));
        Assert.Equal((10d, 20d), RecordingTimeline.ToMediaRange(Video, 10, 20));
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData(-40.125, 640.125, 655.125)]
    [InlineData(15.5, 584.5, 599.5)]
    [InlineData(0, 600, 615)]
    public void LoadingAndLateStartOffsetsTranslateGameRangeExactlyOnce(double offset, double start, double end)
    {
        Write(new(1, 42, "match.mp4", offset));
        Assert.Equal(offset, RecordingTimeline.ReadGameTimeAtVideoStart(Video, 42));
        Assert.Equal((start, end), RecordingTimeline.ToMediaRange(Video, 600, 615));
        Assert.Equal((600d, 615d), RecordingTimeline.ToMediaRange(Path.Combine(_root, "exported-clip.mp4"), 600, 615));
    }

    [Fact]
    public void UncapturedPrefixIsClampedAndEntirelyUncapturedMomentIsRejected()
    {
        Write(new(1, 42, "match.mp4", 15));
        Assert.Equal((0d, 5d), RecordingTimeline.ToMediaRange(Video, 10, 20));
        Assert.Throws<ArgumentException>(() => RecordingTimeline.ToMediaRange(Video, 1, 10));
    }

    [Fact]
    public void WrongMatchFilenameSchemaAndOutOfBoundsOffsetsAreRejected()
    {
        foreach (var timing in new[] { new RecordingTiming(1, 43, "match.mp4", 0), new(1, 42, "other.mp4", 0),
            new(2, 42, "match.mp4", 0), new(1, 42, "match.mp4", -601), new(1, 42, "match.mp4", 7201) })
        {
            Write(timing);
            Assert.Throws<InvalidDataException>(() => RecordingTimeline.ReadGameTimeAtVideoStart(Video, 42));
        }
    }

    [Fact]
    public void CorruptAndOversizedMetadataFailInsteadOfSilentlyMisaligningClips()
    {
        File.WriteAllText(Video + RecordingTimeline.Suffix, "{ broken");
        Assert.Throws<InvalidDataException>(() => RecordingTimeline.ToMediaRange(Video, 10, 20));
        File.WriteAllText(Video + RecordingTimeline.Suffix, new string(' ', 4097));
        Assert.Throws<InvalidDataException>(() => RecordingTimeline.ReadGameTimeAtVideoStart(Video));
    }

    private void Write(RecordingTiming timing) => File.WriteAllText(Video + RecordingTimeline.Suffix,
        JsonSerializer.Serialize(timing, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    public void Dispose() => Directory.Delete(_root, recursive: true);
}

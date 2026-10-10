using System.Text.Json;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>The C2 transcript document: normalization rules and exact JSON names.</summary>
public sealed class TranscriptDocumentTests
{
    [Fact]
    public void Normalize_ClampsDropsCollapsesSortsAndRounds()
    {
        var doc = TranscriptDocument.Normalize(new[]
        {
            new TranscriptSegment(30.12345, 31.98765, "  second\n line \t here "),
            new TranscriptSegment(-2, 1.5, "first"),
            new TranscriptSegment(5, 5, "zero length"),
            new TranscriptSegment(6, 4, "backwards"),
            new TranscriptSegment(7, 8, "   "),
            new TranscriptSegment(58, 75, "runs past the end"),
            new TranscriptSegment(61, 70, "starts past the end"),
        }, " EN ", clipDurationS: 60);

        Assert.Equal(1, doc.Version);
        Assert.Equal("en", doc.Language);
        Assert.Equal(new[]
        {
            new TranscriptSegment(0, 1.5, "first"),
            new TranscriptSegment(30.123, 31.988, "second line here"),
            new TranscriptSegment(58, 60, "runs past the end"),
        }, doc.Segments);
    }

    [Fact]
    public void Normalize_CapsTextAtFiveHundredChars_AndSegmentsAtThreeThousand()
    {
        var longText = new string('a', 700);
        var many = Enumerable.Range(0, 3200).Select(i => new TranscriptSegment(i, i + 0.5, i == 0 ? longText : "x"));

        var doc = TranscriptDocument.Normalize(many, "en", 10_000);

        Assert.Equal(3000, doc.Segments.Count);
        Assert.Equal(500, doc.Segments[0].Text.Length);
        Assert.Equal(2999, doc.Segments[^1].Start);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("e", "")]
    [InlineData("pt-br", "pt-br")]
    [InlineData("toolonglang", "")]
    public void Normalize_LanguageIsEmptyOrTwoToEightChars(string input, string expected)
    {
        Assert.Equal(expected, TranscriptDocument.Normalize(Array.Empty<TranscriptSegment>(), input, 10).Language);
    }

    [Fact]
    public void Json_UsesExactlyTheC2PropertyNames_AndRoundTrips()
    {
        var doc = TranscriptDocument.Normalize(new[] { new TranscriptSegment(1.234, 3.5, "first gank comes at three minutes") }, "en", 60);

        var json = doc.ToJson();

        Assert.Equal("{\"version\":1,\"language\":\"en\",\"segments\":[{\"start\":1.234,\"end\":3.5,\"text\":\"first gank comes at three minutes\"}]}", json);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(new[] { "version", "language", "segments" }, parsed.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { "start", "end", "text" },
            parsed.RootElement.GetProperty("segments")[0].EnumerateObject().Select(p => p.Name));

        var back = TranscriptDocument.TryParse(json);
        Assert.NotNull(back);
        Assert.Equal(doc.Segments, back!.Segments);
        Assert.Equal("en", back.Language);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"version\":1,\"language\":\"en\"}")]
    [InlineData("[1,2,3]")]
    public void TryParse_ReturnsNullForEmptyOrInvalidDocuments(string? json)
    {
        Assert.Null(TranscriptDocument.TryParse(json));
    }
}

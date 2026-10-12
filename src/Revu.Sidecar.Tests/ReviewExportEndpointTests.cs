using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// POST /api/settings/export saves the Markdown itself and answers with where it went, so
/// the export never crosses the renderer's 4 MiB IPC envelope.
/// </summary>
public sealed class ReviewExportEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Revu.Sidecar.Tests", "export-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private static ReviewExportService Service(SidecarWriteScope scope) =>
        new(scope.Games, scope.Objectives, scope.ConceptTags, scope.Prompts, scope.Vod, scope.MatchupNotes,
            scope.Evidence, scope.SessionLog, scope.Config);

    private static (int Status, JsonElement Body) Read(IResult result)
    {
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;
        return (status, JsonSerializer.SerializeToElement(value, SidecarJson.CreateOptions()));
    }

    [Fact]
    public async Task TheExportIsSavedIntoTheFolder_AndTheReplyCarriesOnlyItsLocation()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(9_501, champion: "Ahri"));
        var folder = Path.Combine(_root, "Revu");

        var (status, body) = Read(await SidecarEndpoints.SaveReviewExportAsync(
            Service(scope), folder, SidecarJson.CreateOptions(), NullLogger.Instance, CancellationToken.None));

        Assert.Equal(200, status);
        Assert.True(body.GetProperty("ok").GetBoolean());
        var path = body.GetProperty("path").GetString()!;
        Assert.Equal(folder, Path.GetDirectoryName(path));
        Assert.Equal(Path.GetFileName(path), body.GetProperty("fileName").GetString());
        Assert.Equal(1, body.GetProperty("games").GetInt32());
        Assert.Contains("Ahri", await File.ReadAllTextAsync(path));
        Assert.False(body.TryGetProperty("markdown", out _));
    }

    [Fact]
    public async Task AFolderThatCannotBeWritten_IsReportedPlainly()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        Directory.CreateDirectory(_root);
        var blocked = Path.Combine(_root, "not-a-folder");
        await File.WriteAllTextAsync(blocked, "a file where the folder should be");

        var (status, body) = Read(await SidecarEndpoints.SaveReviewExportAsync(
            Service(scope), blocked, SidecarJson.CreateOptions(), NullLogger.Instance, CancellationToken.None));

        Assert.Equal(422, status);
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.StartsWith("Couldn't save the export to ", body.GetProperty("error").GetString());
    }

    [Fact]
    public void TheExportFolderIsDocumentsRevu()
    {
        var folder = SidecarEndpoints.ReviewExportFolder();
        Assert.Equal("Revu", Path.GetFileName(folder));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Path.GetDirectoryName(folder));
    }
}

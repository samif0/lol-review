using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data;
using Revu.Core.Models;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// H5: clip cleanup deletes old clips, so /api/config/save refuses a clips folder that is,
/// contains or sits inside a recordings folder, and refuses it as a whole (no field of
/// that save lands). A newly chosen clips or backup folder must also be on this PC.
/// The mapping runs through the real ConfigService.UpdateAsync.
/// </summary>
public sealed class ConfigSaveClipsFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Revu.Sidecar.Tests", "clipsfolder-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_root, "config.json");

    public ConfigSaveClipsFolderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static SaveConfigBody Body(object fields) =>
        JsonSerializer.Deserialize<SaveConfigBody>(JsonSerializer.Serialize(fields), SidecarJson.CreateOptions())!;

    private ConfigService Service() => new(NullLogger<ConfigService>.Instance, new MemorySecretStore(), ConfigPath);

    private async Task<string?> SaveAsync(ConfigService service, object fields)
    {
        string? rejected = null;
        await service.UpdateAsync(cfg => (rejected = SidecarEndpoints.ApplyConfigSave(Body(fields), cfg)) is null);
        return rejected;
    }

    [Fact]
    public void ClipsFolder_MayNotOverlapRevusRecordingsFolder()
    {
        var inside = Path.Combine(AppDataPaths.RecordingsDirectory, "clips");
        Assert.NotNull(SidecarEndpoints.ClipsFolderConflict(new AppConfig { ClipsFolder = inside }));
        Assert.Null(SidecarEndpoints.ClipsFolderConflict(new AppConfig()));
    }

    [WindowsFact]
    public async Task AnOverlappingSave_IsRejectedWhole_AndASeparateFolderSaves()
    {
        var service = Service();
        Assert.Null(await SaveAsync(service, new { clipsMaxSizeMb = 4096 }));

        var error = await SaveAsync(service, new { clipsFolder = Path.Combine(AppDataPaths.RecordingsDirectory, "clips"), tiltFixMode = true });

        Assert.NotNull(error);
        var unchanged = await Service().LoadAsync();
        Assert.Equal("", unchanged.ClipsFolder);
        Assert.False(unchanged.TiltFixMode);

        Assert.Null(await SaveAsync(service, new { clipsFolder = @"D:\Videos\Clips" }));
        Assert.Equal(@"D:\Videos\Clips", (await Service().LoadAsync()).ClipsFolder);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(3, 10)]
    [InlineData(250, 250)]
    [InlineData(9000, 2000)]
    public async Task RecordingsLimit_IsZeroOrHeldBetween10And2000Gb(int sent, int saved)
    {
        var service = Service();
        Assert.Null(await SaveAsync(service, new { recordingsMaxSizeGb = sent }));
        Assert.Equal(saved, (await Service().LoadAsync()).RecordingsMaxSizeGb);
        Assert.Null(await SaveAsync(service, new { tiltFixMode = true }));
        Assert.Equal(saved, (await Service().LoadAsync()).RecordingsMaxSizeGb);
    }

    [WindowsFact]
    public async Task AnExistingOverlap_DoesNotBlockUnrelatedSaves()
    {
        await File.WriteAllTextAsync(ConfigPath, JsonSerializer.Serialize(new { clips_folder = AppDataPaths.RecordingsDirectory }));
        var service = Service();

        Assert.Null(await SaveAsync(service, new { tiltFixMode = true }));
        Assert.True((await Service().LoadAsync()).TiltFixMode);
    }

    [WindowsFact]
    public async Task ANewNetworkFolder_IsRejectedWhole_ButOneSavedEarlierDoesNotBlockSaves()
    {
        var service = Service();
        foreach (var field in new[] { "clipsFolder", "backupFolder" })
        {
            var error = await SaveAsync(service, new Dictionary<string, object>
            {
                [field] = @"\\nas.invalid\share\revu",
                ["tiltFixMode"] = true,
            });
            Assert.NotNull(error);
            Assert.DoesNotContain("\u2014", error);
        }
        var unchanged = await Service().LoadAsync();
        Assert.False(unchanged.TiltFixMode);
        Assert.Equal("", unchanged.BackupFolder);

        // A value saved before this check, sent back unchanged by the Settings page.
        await File.WriteAllTextAsync(ConfigPath, """{ "backup_folder": "\\\\nas.invalid\\share\\revu" }""");
        Assert.Null(await SaveAsync(service, new { backupFolder = @"\\nas.invalid\share\revu", tiltFixMode = true }));
        Assert.True((await Service().LoadAsync()).TiltFixMode);
    }

    private sealed class MemorySecretStore : IProtectedSecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public string? GetSecret(string name) => _values.TryGetValue(name, out var v) ? v : null;
        public void SetSecret(string name, string value) => _values[name] = value;
        public void ClearSecret(string name) => _values.Remove(name);
    }
}

/// <summary>A [Theory] that runs only on Windows (drive-letter path semantics).</summary>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Pins Windows path semantics; runs on the windows-latest CI job.";
        }
    }
}

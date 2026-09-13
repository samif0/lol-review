using System.Text.Json;
using Revu.Sidecar;
using Xunit;

namespace Revu.Sidecar.Tests;

public sealed class IsolatedHostPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Revu.IsolatedPolicy.Tests", Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_root, "LoLReviewData");

    [Theory]
    [InlineData("POST", "/api/clip/delete")]
    [InlineData("POST", "/api/clip/extract")]
    [InlineData("POST", "/api/clip/upload")]
    [InlineData("POST", "/api/config/save")]
    [InlineData("POST", "/api/settings/restore")]
    [InlineData("POST", "/api/settings/reset")]
    [InlineData("GET", "/API/AUTH/status")]
    [InlineData("GET", "/API/UPDATE/check/")]
    [InlineData("GET", "/api/pregame")]
    [InlineData("HEAD", "/API/PREGAME/")]
    [InlineData("GET", "/api/future-download")]
    [InlineData("POST", "/api/bookmark/future-file-operation")]
    [InlineData("DELETE", "/api/review/delete")]
    public void ExternalAndUnauditedOperationsAreDenied(string method, string route) => Assert.False(IsolatedHostPolicy.Allows(method, route));

    [Theory]
    [InlineData("GET", "/api/games")]
    [InlineData("GET", "/api/vod")]
    [InlineData("POST", "/api/review/save")]
    [InlineData("POST", "/api/bookmark/delete")]
    [InlineData("POST", "/API/EVENT/CORRECT/")]
    [InlineData("POST", "/api/correction/revert")]
    public void AuditedReviewAndDatabaseOperationsRemainAvailable(string method, string route) => Assert.True(IsolatedHostPolicy.Allows(method, route));

    [Theory]
    [InlineData("config.json")]
    [InlineData("config.json.bak")]
    public void ConfigurationAndFallbackCannotWriteToOriginalFolders(string filename)
    {
        Directory.CreateDirectory(Data);
        File.WriteAllText(Path.Combine(Data, filename), JsonSerializer.Serialize(new { clips_folder = Path.Combine(_root, "original-media") }));
        Assert.Throws<InvalidOperationException>(() => IsolatedHostPolicy.ValidateConfiguration(Data));
    }

    [Fact]
    public void UnsafeBackupIsRejectedEvenWhenPrimaryConfigIsSafe()
    {
        Directory.CreateDirectory(Data);
        File.WriteAllText(Path.Combine(Data, "config.json"), "{}");
        File.WriteAllText(Path.Combine(Data, "config.json.bak"), JsonSerializer.Serialize(new { backup_folder = Path.Combine(_root, "original-backups") }));
        Assert.Throws<InvalidOperationException>(() => IsolatedHostPolicy.ValidateConfiguration(Data));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyConfigurationFallbackIsValidated(bool nested)
    {
        Directory.CreateDirectory(Data);
        var legacy = nested ? Path.Combine(_root, "LoLReview", "data") : Path.Combine(_root, "LoLReview");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "config.json"), JsonSerializer.Serialize(new { backup_folder = Path.Combine(_root, "external") }));
        Assert.Throws<InvalidOperationException>(() => IsolatedHostPolicy.ValidateConfiguration(Data));
    }

    [Fact]
    public void ContainedScratchOutputsAndEmptyDefaultsAreAccepted()
    {
        Directory.CreateDirectory(Data);
        File.WriteAllText(Path.Combine(Data, "config.json"), JsonSerializer.Serialize(new { clips_folder = Path.Combine(Data, "clips"), backup_folder = "" }));
        File.WriteAllText(Path.Combine(Data, "config.json.bak"), "{}");
        IsolatedHostPolicy.ValidateConfiguration(Data);
    }

    [Fact]
    public void RelativeOutputAndNonObjectConfigurationsAreRejected()
    {
        Directory.CreateDirectory(Data);
        var config = Path.Combine(Data, "config.json");
        File.WriteAllText(config, "{\"clips_folder\":\"../original\"}");
        Assert.Throws<InvalidOperationException>(() => IsolatedHostPolicy.ValidateConfiguration(Data));
        File.WriteAllText(config, "null");
        Assert.Throws<InvalidOperationException>(() => IsolatedHostPolicy.ValidateConfiguration(Data));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}

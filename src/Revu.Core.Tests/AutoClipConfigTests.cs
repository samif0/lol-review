using Revu.Core.Models;

namespace Revu.Core.Tests;

/// <summary>
/// The new auto-clip toggle: opt-in default, and round-trips through the config save
/// seam the /api/config/save endpoint exercises (the endpoint applies the body then
/// calls IConfigService.SaveAsync). The nullable-bool handler only assigns when the
/// body field is non-null, so an unrelated save must leave the toggle unchanged —
/// mirrored here at the AppConfig level.
/// </summary>
public sealed class AutoClipConfigTests
{
    [Fact]
    public void DefaultsToOff()
    {
        Assert.False(new AppConfig().AutoClipObjectivesEnabled);
    }

    [Fact]
    public async Task RoundTripsThroughConfigService()
    {
        var svc = new TestConfigService();

        var cfg = await svc.LoadAsync();
        cfg.AutoClipObjectivesEnabled = true;
        await svc.SaveAsync(cfg);

        Assert.True((await svc.LoadAsync()).AutoClipObjectivesEnabled);
        Assert.True(svc.AutoClipObjectivesEnabled); // cached convenience property
    }

    [Fact]
    public async Task UnrelatedSave_LeavesToggleUnchanged()
    {
        // Start with the toggle ON, then apply a save that only touches another field
        // (the nullable-bool handler skips fields the body leaves null).
        var svc = new TestConfigService(new AppConfig { AutoClipObjectivesEnabled = true });

        var cfg = await svc.LoadAsync();
        cfg.MinimizeDuringGame = false; // simulate a partial save of a different toggle
        await svc.SaveAsync(cfg);

        Assert.True((await svc.LoadAsync()).AutoClipObjectivesEnabled);
    }
}

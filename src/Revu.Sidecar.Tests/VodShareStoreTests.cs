using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>vod-shares.json: atomic writes, expiry pruning, one entry per game, reconcile.</summary>
public sealed class VodShareStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Revu.Sidecar.Tests", "vodstore-" + Guid.NewGuid().ToString("N"));
    private long _now = 1_760_400_000;

    public VodShareStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string PathOf => Path.Combine(_dir, "Revu", "vod-shares.json");

    private VodShareStore Store() => new(PathOf, NullLogger<VodShareStore>.Instance, () => _now);

    private VodShareEntry Entry(long gameId, string id, long? expiresAt = null, bool markers = true) =>
        new(gameId, id, "https://revu.lol/v/" + id, "1080p", false, 1_315_583_333, _now - 100,
            expiresAt ?? _now + 259_000, markers);

    [Fact]
    public void Put_WritesTheContractShape_Atomically()
    {
        var store = Store();
        store.Put(Entry(123, "abc1234"));

        Assert.False(File.Exists(PathOf + ".tmp"));
        using var doc = JsonDocument.Parse(File.ReadAllText(PathOf));
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        var share = Assert.Single(doc.RootElement.GetProperty("shares").EnumerateArray());
        Assert.Equal(
            new[] { "gameId", "id", "url", "quality", "includeNotes", "sizeBytes", "createdAt", "expiresAt", "markersAttached" },
            share.EnumerateObject().Select(p => p.Name));
        Assert.Equal(123, share.GetProperty("gameId").GetInt64());
        Assert.Equal("https://revu.lol/v/abc1234", share.GetProperty("url").GetString());
        // A fresh store (an app restart) reads it back.
        Assert.Equal(Entry(123, "abc1234"), Store().Get(123));
    }

    [Fact]
    public void OneEntryPerGame_ANewShareReplacesTheOld()
    {
        var store = Store();
        store.Put(Entry(123, "old0001"));
        store.Put(Entry(123, "new0002"));
        store.Put(Entry(456, "oth0003"));

        Assert.Equal("new0002", store.Get(123)!.Id);
        Assert.Equal(2, store.Snapshot().Count);
    }

    [Fact]
    public void ExpiredEntries_AreDroppedOnLoadAndSave()
    {
        var store = Store();
        store.Put(Entry(1, "aaa0001", expiresAt: _now + 10));
        store.Put(Entry(2, "bbb0002", expiresAt: _now + 1000));
        Assert.Equal(2, store.Snapshot().Count);

        _now += 11;
        Assert.Null(store.Get(1));
        Assert.Equal("bbb0002", store.Get(2)!.Id);
        store.Put(Entry(3, "ccc0003"));
        Assert.DoesNotContain("aaa0001", File.ReadAllText(PathOf));

        // An already-expired entry is never written.
        store.Put(Entry(4, "ddd0004", expiresAt: _now));
        Assert.Null(store.Get(4));
    }

    [Fact]
    public void Remove_ReturnsTheEntry()
    {
        var store = Store();
        store.Put(Entry(123, "abc1234"));
        Assert.Equal("abc1234", store.Remove(123)!.Id);
        Assert.Null(store.Remove(123));
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void Reconcile_DropsLinksTheProxyNoLongerLists_ButKeepsJustStoredOnes()
    {
        var store = Store();
        store.Put(Entry(1, "live001"));
        store.Put(Entry(2, "gone002"));
        store.Put(Entry(3, "just003"));

        var removed = store.Reconcile(new[] { "live001", "other99" }, keep: new[] { "just003" });

        Assert.Equal(1, removed);
        Assert.Equal(new[] { "live001", "just003" }, store.Snapshot().Select(e => e.Id).OrderBy(i => i == "just003"));
        Assert.Equal(0, store.Reconcile(new[] { "live001", "just003" }));
    }

    [Fact]
    public void AnUnreadableFile_StartsEmpty_AndIsReplacedOnTheNextSave()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathOf)!);
        File.WriteAllText(PathOf, "{ not json");
        var store = Store();
        Assert.Empty(store.Snapshot());
        store.Put(Entry(7, "fix0007"));
        Assert.Equal("fix0007", Store().Get(7)!.Id);
    }

    [Fact]
    public void InvalidEntries_AreIgnored()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathOf)!);
        File.WriteAllText(PathOf, $$"""
            {"version":1,"shares":[null,{"gameId":0,"id":"zero000","expiresAt":{{_now + 100}}},
              {"gameId":5,"id":"","expiresAt":{{_now + 100}}},
              {"gameId":6,"id":"good006","url":"https://revu.lol/v/good006","quality":"720p","expiresAt":{{_now + 100}}}]}
            """);
        var only = Assert.Single(Store().Snapshot());
        Assert.Equal((6L, "good006", "720p"), (only.GameId, only.Id, only.Quality));
    }

    [Fact]
    public void TheFile_NeverHoldsTokensPathsOrNotes()
    {
        var store = Store();
        store.Put(Entry(123, "abc1234") with { IncludeNotes = true });
        var text = File.ReadAllText(PathOf);
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":\\\\", text);
        Assert.DoesNotContain("note\"", text);
    }
}

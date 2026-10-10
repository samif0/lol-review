#nullable enable

using System.Collections.Concurrent;
using Revu.Core.Data.Repositories;

namespace Revu.Core.Services;

/// <summary>
/// The set of clip files the clips-folder eviction must never delete (C8): narration
/// sources and narrated renders, the clip of every shared bookmark, and files pinned by
/// a running share job.
/// </summary>
public interface IClipRetentionGuard
{
    /// <summary>Full paths (case-insensitive) that eviction must skip.</summary>
    Task<IReadOnlySet<string>> GetProtectedPathsAsync(CancellationToken ct = default);

    /// <summary>Protect <paramref name="path"/> until the returned handle is disposed (refcounted).</summary>
    IDisposable Pin(string path);
}

public sealed class ClipRetentionGuard : IClipRetentionGuard
{
    private readonly IClipNarrationRepository _narrations;
    private readonly IVodRepository _vod;
    private readonly ConcurrentDictionary<string, int> _pins = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _pinGate = new();

    public ClipRetentionGuard(IClipNarrationRepository narrations, IVodRepository vod)
    {
        _narrations = narrations;
        _vod = vod;
    }

    public async Task<IReadOnlySet<string>> GetProtectedPathsAsync(CancellationToken ct = default)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in await _narrations.ListProtectedClipPathsAsync().ConfigureAwait(false))
            AddNormalized(set, path);
        ct.ThrowIfCancellationRequested();
        foreach (var path in await _vod.ListSharedClipPathsAsync().ConfigureAwait(false))
            AddNormalized(set, path);
        foreach (var pinned in _pins.Keys)
            set.Add(pinned);
        return set;
    }

    public IDisposable Pin(string path)
    {
        var key = Normalize(path);
        if (key.Length == 0) return NoopHandle.Instance;
        lock (_pinGate)
        {
            _pins.AddOrUpdate(key, 1, (_, count) => count + 1);
        }
        return new PinHandle(this, key);
    }

    /// <summary>Full path for comparisons, or "" when the path is empty or malformed.</summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFullPath(path); }
        catch { return ""; }
    }

    private static void AddNormalized(HashSet<string> set, string? path)
    {
        var full = Normalize(path);
        if (full.Length > 0) set.Add(full);
    }

    private void Release(string key)
    {
        lock (_pinGate)
        {
            if (!_pins.TryGetValue(key, out var count)) return;
            if (count <= 1) _pins.TryRemove(key, out _);
            else _pins[key] = count - 1;
        }
    }

    private sealed class PinHandle : IDisposable
    {
        private readonly ClipRetentionGuard _owner;
        private readonly string _key;
        private int _disposed;

        public PinHandle(ClipRetentionGuard owner, string key)
        {
            _owner = owner;
            _key = key;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _owner.Release(_key);
        }
    }

    private sealed class NoopHandle : IDisposable
    {
        public static readonly NoopHandle Instance = new();
        public void Dispose() { }
    }
}

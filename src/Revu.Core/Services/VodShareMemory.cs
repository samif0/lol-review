#nullable enable

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Revu.Core.Data;

namespace Revu.Core.Services;

/// <summary>
/// A small cache for HLS full-game sharing (4.4), persisted at <see cref="AppDataPaths.VodShareMemoryPath"/>:
/// each encoder's HLS self-test result (keyed by ffmpeg binary, encoder and GPU driver) and each account's
/// measured upload speed, which preselects 720p on the next share. Never holds tokens, emails or paths
/// beyond the ffmpeg binary's. A lock, load-modify-save, an atomic write (temp file + replace); an
/// unreadable file starts empty. Losing it costs one self-test and the preselect, nothing else.
/// </summary>
public sealed class VodShareMemory
{
    private const int MaxSelfTests = 32;
    private const int MaxAccounts = 16;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string? _path;
    private readonly ILogger? _logger;
    private readonly Func<long> _now;
    private readonly object _gate = new();
    private Document? _memory;

    /// <summary>A memory backed by <paramref name="path"/>; null keeps it in this process only (tests).</summary>
    public VodShareMemory(string? path, ILogger? logger = null, Func<long>? now = null)
    {
        _path = path;
        _logger = logger;
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    /// <summary>The cached self-test result for <paramref name="key"/>, or null when none is cached.</summary>
    public bool? SelfTest(string key)
    {
        lock (_gate) return Load().SelfTests.TryGetValue(key, out var entry) ? entry.Passed : null;
    }

    public void SetSelfTest(string key, bool passed)
    {
        lock (_gate)
        {
            var doc = Load();
            doc.SelfTests[key] = new SelfTestEntry { Passed = passed, At = _now() };
            Trim(doc.SelfTests, e => e.At, MaxSelfTests);
            Save(doc);
        }
    }

    /// <summary>The account's last measured upload speed in bytes per second, or null.</summary>
    public double? UplinkBytesPerSecond(string account)
    {
        if (string.IsNullOrWhiteSpace(account)) return null;
        lock (_gate) return Load().Uplink.TryGetValue(account, out var entry) && entry.BytesPerSecond > 0 ? entry.BytesPerSecond : null;
    }

    public void SetUplink(string account, double bytesPerSecond)
    {
        if (string.IsNullOrWhiteSpace(account) || !double.IsFinite(bytesPerSecond) || bytesPerSecond <= 0) return;
        lock (_gate)
        {
            var doc = Load();
            doc.Uplink[account] = new UplinkEntry { BytesPerSecond = bytesPerSecond, At = _now() };
            Trim(doc.Uplink, e => e.At, MaxAccounts);
            Save(doc);
        }
    }

    private static void Trim<T>(Dictionary<string, T> map, Func<T, long> at, int max)
    {
        while (map.Count > max) map.Remove(map.MinBy(kv => at(kv.Value)).Key);
    }

    private Document Load()
    {
        if (_memory is not null) return _memory;
        _memory = new Document();
        if (_path is null || !File.Exists(_path)) return _memory;
        try
        {
            var doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(_path), Json);
            if (doc is not null)
            {
                _memory = new Document
                {
                    SelfTests = new Dictionary<string, SelfTestEntry>(doc.SelfTests ?? new(), StringComparer.Ordinal),
                    Uplink = new Dictionary<string, UplinkEntry>(doc.Uplink ?? new(), StringComparer.Ordinal),
                };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger?.LogWarning(ex, "Share memory at {Path} could not be read; starting empty", _path);
        }
        return _memory;
    }

    private void Save(Document doc)
    {
        if (_path is null) return;
        var temp = _path + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(temp, JsonSerializer.Serialize(doc, Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Share memory at {Path} could not be saved", _path);
            try { File.Delete(temp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed class Document
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, SelfTestEntry> SelfTests { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, UplinkEntry> Uplink { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class SelfTestEntry
    {
        public bool Passed { get; set; }
        public long At { get; set; }
    }

    private sealed class UplinkEntry
    {
        public double BytesPerSecond { get; set; }
        public long At { get; set; }
    }
}

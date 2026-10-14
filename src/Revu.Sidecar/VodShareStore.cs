#nullable enable

using System.Text.Json;

namespace Revu.Sidecar;

/// <summary>One live full-game share. Never holds tokens, emails, file paths or notes.</summary>
public sealed record VodShareEntry(
    long GameId,
    string Id,
    string Url,
    string Quality,
    bool IncludeNotes,
    long SizeBytes,
    long CreatedAt,
    long ExpiresAt,
    bool MarkersAttached);

/// <summary>
/// Completed (live) full-game shares, one per game, persisted at
/// <c>AppDataPaths.VodSharesPath</c> as <c>{ "version": 1, "shares": [...] }</c>. Same pattern as
/// <see cref="RemoteClipCleanupStore"/>: a lock, load-modify-save, an atomic write (temp file +
/// replace), and an unreadable file starts empty with a warning. Expired entries are dropped on
/// every load and save. Abandoned uploads and deferred deletes live in the cleanup store.
/// </summary>
public sealed class VodShareStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly string _path;
    private readonly ILogger<VodShareStore> _logger;
    private readonly Func<long> _now;
    private readonly object _gate = new();

    public VodShareStore(string path, ILogger<VodShareStore> logger, Func<long>? now = null)
    {
        _path = path;
        _logger = logger;
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    /// <summary>The live entry for <paramref name="gameId"/>, or null.</summary>
    public VodShareEntry? Get(long gameId)
    {
        lock (_gate) return Load().FirstOrDefault(e => e.GameId == gameId);
    }

    public IReadOnlyList<VodShareEntry> Snapshot()
    {
        lock (_gate) return Load();
    }

    /// <summary>Store <paramref name="entry"/>, replacing any entry for the same game or id.</summary>
    public void Put(VodShareEntry entry)
    {
        if (entry is null || entry.GameId <= 0 || string.IsNullOrWhiteSpace(entry.Id)) return;
        lock (_gate)
        {
            var entries = Load();
            entries.RemoveAll(e => e.GameId == entry.GameId || string.Equals(e.Id, entry.Id, StringComparison.Ordinal));
            if (entry.ExpiresAt > _now()) entries.Add(entry);
            Save(entries);
        }
    }

    /// <summary>Remove and return the game's entry (null when none).</summary>
    public VodShareEntry? Remove(long gameId)
    {
        lock (_gate)
        {
            var entries = Load();
            var found = entries.FirstOrDefault(e => e.GameId == gameId);
            if (found is null) return null;
            entries.Remove(found);
            Save(entries);
            return found;
        }
    }

    /// <summary>
    /// Drop entries whose id the proxy no longer lists (stopped on the web, or purged). Ids in
    /// <paramref name="keep"/> survive regardless (stored after the status fetch began).
    /// Returns how many were dropped.
    /// </summary>
    public int Reconcile(IEnumerable<string> liveIds, IEnumerable<string>? keep = null)
    {
        var live = new HashSet<string>(liveIds ?? Array.Empty<string>(), StringComparer.Ordinal);
        if (keep is not null) live.UnionWith(keep);
        lock (_gate)
        {
            var entries = Load();
            var removed = entries.RemoveAll(e => !live.Contains(e.Id));
            if (removed > 0)
            {
                Save(entries);
                _logger.LogInformation("Full-game shares: {Count} link(s) ended outside the app", removed);
            }
            return removed;
        }
    }

    private List<VodShareEntry> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new List<VodShareEntry>();
            var doc = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(_path), Json);
            var now = _now();
            return (doc?.Shares ?? new List<VodShareEntry>())
                .OfType<VodShareEntry>()
                .Where(e => e.GameId > 0 && !string.IsNullOrWhiteSpace(e.Id) && e.ExpiresAt > now)
                .GroupBy(e => e.GameId)
                .Select(g => g.Last())
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Full-game share file unreadable; starting empty");
            return new List<VodShareEntry>();
        }
    }

    private void Save(List<VodShareEntry> entries)
    {
        try
        {
            var now = _now();
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var temp = _path + ".tmp";
            var live = entries.Where(e => e.ExpiresAt > now).ToList();
            File.WriteAllText(temp, JsonSerializer.Serialize(new StoreFile { Version = 1, Shares = live }, Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write the full-game share file");
        }
    }

    private sealed class StoreFile
    {
        public int Version { get; set; } = 1;
        public List<VodShareEntry> Shares { get; set; } = new();
    }
}

#nullable enable

using System.Text.Json;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>
/// Remote clip ids still to DELETE on the share proxy: abandoned multipart uploads (added
/// the moment init assigns an id, removed once the link is stored) and deletes deferred
/// while signed out. Persisted at <c>AppDataPaths.RemoteClipCleanupPath</c> as
/// <c>{ "slugs": [...] }</c>, written atomically (temp file + replace) under a lock, and
/// drained at signed-in startup and after sign-in.
/// </summary>
public sealed class RemoteClipCleanupStore
{
    private static readonly TimeSpan DeleteTimeout = TimeSpan.FromSeconds(10);

    private readonly string _path;
    private readonly IClipUploadService _clips;
    private readonly ILogger<RemoteClipCleanupStore> _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _drainGate = new(1, 1);

    public RemoteClipCleanupStore(string path, IClipUploadService clips, ILogger<RemoteClipCleanupStore> logger)
    {
        _path = path;
        _clips = clips;
        _logger = logger;
    }

    public void Add(string slug)
    {
        slug = (slug ?? "").Trim();
        if (slug.Length == 0) return;
        lock (_gate)
        {
            var slugs = Load();
            if (slugs.Contains(slug, StringComparer.Ordinal)) return;
            slugs.Add(slug);
            Save(slugs);
        }
    }

    public void Remove(string slug)
    {
        slug = (slug ?? "").Trim();
        if (slug.Length == 0) return;
        lock (_gate)
        {
            var slugs = Load();
            if (slugs.RemoveAll(s => string.Equals(s, slug, StringComparison.Ordinal)) > 0) Save(slugs);
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate) return Load();
    }

    /// <summary>
    /// DELETE every queued id (10 s each, best effort). A 200 or 404 removes the id; any
    /// other outcome keeps it for the next drain. Returns the number removed.
    /// </summary>
    public async Task<int> DrainAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return 0;
        await _drainGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var removed = 0;
            foreach (var slug in Snapshot())
            {
                ct.ThrowIfCancellationRequested();
                using var timeout = new CancellationTokenSource(DeleteTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                bool gone;
                try { gone = await _clips.DeleteAsync(slug, token, linked.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _logger.LogDebug(ex, "Remote clip cleanup of {Slug} failed", slug);
                    gone = false;
                }
                if (!gone) continue;
                Remove(slug);
                removed++;
            }
            if (removed > 0) _logger.LogInformation("Remote clip cleanup removed {Count} clip(s)", removed);
            return removed;
        }
        finally
        {
            _drainGate.Release();
        }
    }

    private List<string> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new List<string>();
            var doc = JsonSerializer.Deserialize<CleanupFile>(File.ReadAllText(_path));
            return (doc?.slugs ?? new List<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Remote clip cleanup file unreadable; starting empty");
            return new List<string>();
        }
    }

    private void Save(List<string> slugs)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new CleanupFile { slugs = slugs }));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write the remote clip cleanup file");
        }
    }

    private sealed class CleanupFile
    {
        public List<string> slugs { get; set; } = new();
    }
}

#nullable enable

using System.Diagnostics;
using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>
/// Full-game (VOD) sharing, 4.1 (contract 7.5): one job at a time, globally. A job encodes the
/// game's linked recording into a share copy under <c>ShareTemp</c>, uploads it in 16 MiB
/// parts, attaches the timeline markers and records the link in <see cref="VodShareStore"/>.
/// Each job runs through <see cref="SidecarBackgroundWork.TryRun"/> on a token linked to
/// shutdown. Progress goes out as <c>vodShareProgress</c> SSE events (percent throttled to
/// 4 per second, phase changes always) and is kept per game for the status poll.
/// </summary>
public sealed partial class VodShareWorker
{
    private static readonly long ThrottleTicks = Stopwatch.Frequency / 4;
    private static readonly TimeSpan StartStatusTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DurationProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AllowedCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StopWaitForJob = TimeSpan.FromSeconds(15);
    internal const long DefaultTtlSeconds = 259200;

    private readonly IVodShareClient _client;
    private readonly IClipUploadService _clips;
    private readonly IVodShareEncoder _encoder;
    private readonly INarrationMixer _mixer;
    private readonly IVodRepository _vod;
    private readonly IGameRepository _games;
    private readonly IConfigService _config;
    private readonly LcuLiveState _live;
    private readonly VodShareStore _store;
    private readonly RemoteClipCleanupStore _cleanup;
    private readonly SidecarEventHub _hub;
    private readonly SidecarBackgroundWork _work;
    private readonly ILogger<VodShareWorker> _logger;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly Dictionary<long, VodShareJobDto> _last = new();
    private readonly Dictionary<string, double> _durations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _storedAt = new(StringComparer.Ordinal);
    private (string Token, bool Allowed, long At)? _allowed;
    private VodShareJob? _running;
    private int _started;

    public VodShareWorker(
        IVodShareClient client,
        IClipUploadService clips,
        IVodShareEncoder encoder,
        INarrationMixer mixer,
        IVodRepository vod,
        IGameRepository games,
        Func<long, CancellationToken, Task<VodDto>> loadSnapshot,
        IConfigService config,
        LcuLiveState live,
        VodShareStore store,
        RemoteClipCleanupStore cleanup,
        SidecarEventHub hub,
        SidecarBackgroundWork work,
        ILogger<VodShareWorker> logger)
    {
        _client = client;
        _clips = clips;
        _encoder = encoder;
        _mixer = mixer;
        _vod = vod;
        _games = games;
        _config = config;
        _live = live;
        _store = store;
        _cleanup = cleanup;
        _hub = hub;
        _work = work;
        _logger = logger;
        LoadSnapshotAsync = loadSnapshot;
        ResolveRecordingAsync = id => SidecarEndpoints.ResolveLocalVodPathAsync(_vod, id);
    }

    // ── Seams (tests replace these) ──────────────────────────────────────────
    internal Func<long, Task<string?>> ResolveRecordingAsync { get; set; }
    internal Func<long, CancellationToken, Task<VodDto>> LoadSnapshotAsync { get; set; }
    internal Func<string, long> FreeDiskBytes { get; set; } = DefaultFreeDiskBytes;
    internal Func<long> Now { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    internal TimeSpan[] MarkerRetryDelays { get; set; } = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];
    internal TimeSpan GameCheckInterval { get; set; } = TimeSpan.FromSeconds(5);
    internal string TempDirectory { get; set; } = AppDataPaths.VodShareTempDirectory;
    internal string RecordingsDirectory { get; set; } = AppDataPaths.RecordingsDirectory;
    /// <summary>How long a start waits for a cancelled share of the same game to finish cleaning up.</summary>
    internal TimeSpan CancelledJobWait { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>One queued or running share.</summary>
    internal sealed class VodShareJob
    {
        public required long GameId { get; init; }
        public required string SourcePath { get; init; }
        public required double DurationSeconds { get; init; }
        public required bool IncludeNotes { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public VodShareQuality Quality { get; set; }
        public string? QualityReason { get; set; }
        /// <summary>Why the job was cancelled ("cancelled" or "game_started"). Guarded by <c>_gate</c>.</summary>
        public string? CancelCode { get; set; }
        public string Phase { get; set; } = "queued";
        public int Percent { get; set; }
        public VodShareJobDto? Status { get; set; }
        public string LastPhase { get; set; } = "";
        public long LastPublish { get; set; }
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Enable starts (idempotent). NarrationStartup calls this after clearing ShareTemp.</summary>
    public void Start() => Interlocked.Exchange(ref _started, 1);

    // ── Status ───────────────────────────────────────────────────────────────

    public async Task<VodShareStatusDto> GetStatusAsync(long gameId, CancellationToken ct)
    {
        var path = await ResolveRecordingAsync(gameId).ConfigureAwait(false);
        var duration = path is null ? null : await DurationAsync(gameId, path, ct).ConfigureAwait(false);
        var d = duration ?? 0;
        var recording = new VodShareRecordingDto(path is not null, path is not null && IsBuiltIn(path),
            d > 0 ? (int)Math.Ceiling(d) : null, d > 0 && VodShareEncoderArgs.IsTooLong(d));
        var estimates = d > 0
            ? new VodShareEstimatesDto(VodShareQualityPolicy.Estimate(VodShareQuality.P1080, d),
                VodShareQualityPolicy.Estimate(VodShareQuality.P720, d))
            : null;

        var signedIn = false;
        var allowed = false;
        var sharingOn = false;
        var reachable = true;
        VodSharePoolDto? pool = null;
        VodShareLimitsDto? limits = null;
        var ttl = DefaultTtlSeconds;
        var session = await ClipShareLinks.SessionAsync(_config).ConfigureAwait(false);
        if (session.SignedIn)
        {
            signedIn = true;
            try
            {
                var status = await FetchStatusAsync(session.Token, ct).ConfigureAwait(false);
                allowed = status.Supported && status.Allowed;
                sharingOn = status.Supported && status.Sharing;
                ttl = status.Limits?.TtlSeconds is > 0 ? status.Limits.TtlSeconds : DefaultTtlSeconds;
                if (allowed && status.Pool is { } p && estimates is not null)
                {
                    var at1080 = p.FitsAt(estimates.P1080Bytes, status.Now);
                    var at720 = p.FitsAt(estimates.P720Bytes, status.Now);
                    var fits1080 = at1080 == status.Now;
                    var fits720 = at720 == status.Now;
                    pool = new VodSharePoolDto(p.FreeBytes, fits1080, fits720, fits1080 ? null : at1080, fits720 ? null : at720);
                }
                if (allowed && status.Usage is { } u && status.Limits is { } l)
                    limits = new VodShareLimitsDto(u.Live, l.MaxLive, u.Pending, u.StartsToday, l.MaxStartsPerDay, u.DayResetsAt);
            }
            catch (ClipUploadException ex) when (ex.Unauthorized)
            {
                // The proxy rejected the stored session: sharing needs a fresh sign-in.
                signedIn = false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                reachable = false;
                allowed = CachedAllowed(session.Token);
                _logger.LogDebug(ex, "Full-game share status: proxy unreachable");
            }
        }

        VodShareJobDto? job;
        long? busy;
        lock (_gate)
        {
            if (_running is { } r && r.GameId == gameId) job = r.Status;
            else job = _last.TryGetValue(gameId, out var last) ? last : null;
            busy = _running is { } other && other.GameId != gameId ? other.GameId : null;
        }
        var entry = _store.Get(gameId);
        return new VodShareStatusDto(gameId, signedIn, allowed, sharingOn, reachable, recording, estimates, pool, limits,
            ttl, job, entry is null ? null : VodShareLinkDto.From(entry), busy);
    }

    /// <summary>GET /vod-shares/status, then cache "allowed" for this token and reconcile the store.</summary>
    private async Task<VodShareServerStatus> FetchStatusAsync(string token, CancellationToken ct)
    {
        var began = Stopwatch.GetTimestamp();
        var status = await _client.GetVodShareStatusAsync(token, ct).ConfigureAwait(false);
        string[] keep;
        lock (_gate)
        {
            _allowed = (token, status.Supported && status.Allowed, began);
            keep = _storedAt.Where(kv => kv.Value >= began).Select(kv => kv.Key).ToArray();
        }
        if (status.Supported && status.Allowed) _store.Reconcile(status.Shares.Select(s => s.Id), keep);
        return status;
    }

    private bool CachedAllowed(string token)
    {
        lock (_gate)
        {
            return _allowed is { } a && a.Token == token && a.Allowed
                && Stopwatch.GetElapsedTime(a.At) < AllowedCacheTtl;
        }
    }

    // ── Start / cancel / stop ────────────────────────────────────────────────

    public async Task<VodShareStartReply> StartAsync(long gameId, string quality, bool includeNotes, CancellationToken ct)
    {
        if (gameId <= 0) return VodShareStartReply.BadRequest("gameId required");
        if (!VodShareEncoderArgs.TryParseQuality(string.IsNullOrWhiteSpace(quality) ? "1080p" : quality, out var requested))
            return VodShareStartReply.BadRequest("quality must be 1080p or 720p");
        if (Volatile.Read(ref _started) == 0) return Refuse("starting");

        // Starts are serialized: the checks below take seconds, and two of them must never
        // both pass the "one job at a time" check.
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_store.Get(gameId) is { } existing) return VodShareStartReply.AlreadyShared(VodShareLinkDto.From(existing));
            VodShareJob? dying = null;
            lock (_gate)
            {
                if (_running is { } r)
                {
                    if (r.GameId != gameId) return Refuse("busy", busyGameId: r.GameId);
                    // A share the player just cancelled is still cleaning up (ffmpeg, the remote
                    // delete). Never answer "already running" for it: the new start would vanish.
                    if (r.CancelCode is null) return VodShareStartReply.AlreadyRunning();
                    dying = r;
                }
            }
            if (dying is not null)
            {
                try { await dying.Finished.Task.WaitAsync(CancelledJobWait, ct).ConfigureAwait(false); }
                catch (TimeoutException) { return Refuse("stopping"); }
                lock (_gate)
                {
                    if (_running is { } r) return r.GameId == gameId ? Refuse("stopping") : Refuse("busy", busyGameId: r.GameId);
                }
                if (_store.Get(gameId) is { } kept) return VodShareStartReply.AlreadyShared(VodShareLinkDto.From(kept));
            }

            var source = await ResolveRecordingAsync(gameId).ConfigureAwait(false);
            if (source is null) return Refuse("no_recording");
            var duration = await DurationAsync(gameId, source, ct).ConfigureAwait(false) ?? 0;
            if (duration <= 0) return Refuse("no_recording");
            if (VodShareEncoderArgs.IsTooLong(duration)) return Refuse("too_long");
            if (_live.IsGameInProgress) return Refuse("game_in_progress");

            var session = await ClipShareLinks.SessionAsync(_config).ConfigureAwait(false);
            if (!session.SignedIn) return Refuse("needs_login", needsLogin: true);

            VodShareServerStatus status;
            using (var timeout = new CancellationTokenSource(StartStatusTimeout))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token))
            {
                try
                {
                    status = await FetchStatusAsync(session.Token, linked.Token).ConfigureAwait(false);
                }
                catch (ClipUploadException ex) when (ex.Unauthorized)
                {
                    return Refuse("needs_login", needsLogin: true);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogInformation(ex, "Full-game share for game {GameId}: status check failed", gameId);
                    return Refuse("network");
                }
            }
            if (!status.Supported || !status.Allowed) return Refuse("not_allowed");
            if (!status.Sharing) return Refuse("sharing_paused");
            if (status.Usage is { } usage && status.Limits is { } limits)
            {
                if (usage.Pending >= limits.MaxPending) return Refuse("busy");
                if (usage.Live >= limits.MaxLive) return Refuse("live_limit");
                if (usage.StartsToday >= limits.MaxStartsPerDay) return Refuse("daily_limit", retryAt: usage.DayResetsAt);
            }
            var decision = VodShareQualityPolicy.Decide(requested, duration, status.Pool, status.Now);
            if (!decision.Ok) return Refuse(decision.ErrorCode!, retryAt: decision.RetryAt);

            long free;
            try { free = FreeDiskBytes(TempDirectory); }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Free disk check failed for {Dir}", TempDirectory);
                free = 0;
            }
            if (free < VodShareQualityPolicy.DiskNeeded(decision.Quality, duration)) return Refuse("disk_full");

            var job = new VodShareJob
            {
                GameId = gameId,
                SourcePath = source,
                DurationSeconds = duration,
                IncludeNotes = includeNotes,
                Cts = CancellationTokenSource.CreateLinkedTokenSource(_work.Stopping),
                Quality = decision.Quality,
                QualityReason = decision.Reason,
            };
            lock (_gate)
            {
                _running = job;
                _last.Remove(gameId);
            }
            Report(job, "running", "queued", 0);
            if (!_work.TryRun("vod-share", () => RunJobAsync(job)))
            {
                lock (_gate) if (ReferenceEquals(_running, job)) _running = null;
                job.Cts.Dispose();
                return Refuse("closing");
            }
            _logger.LogInformation("Full-game share queued: game {GameId} at {Quality} (reason {Reason}, notes {Notes})",
                gameId, VodShareEncoderArgs.Wire(decision.Quality), decision.Reason ?? "none", includeNotes);
            return VodShareStartReply.Accepted(VodShareEncoderArgs.Wire(decision.Quality), decision.Reason);
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>Cancel the game's running share. False when none runs for it.</summary>
    public bool Cancel(long gameId) => Cancel(gameId, "cancelled");

    private bool Cancel(long gameId, string code)
    {
        VodShareJob? job;
        lock (_gate)
        {
            job = _running;
            if (job is null || job.GameId != gameId) return false;
            job.CancelCode ??= code;
        }
        try { job.Cts.Cancel(); }
        catch (ObjectDisposedException) { }
        return true;
    }

    /// <summary>
    /// Cancel any running job for the game, then delete its live share. When the remote delete
    /// can't run now (signed out, offline, server error) it is queued AND the link is kept, so
    /// the page still shows that anyone with the link can watch and Stop sharing stays
    /// available; the next successful status read drops the link once the queue has deleted it.
    /// <paramref name="forgetIfQueued"/> drops the link anyway (the game itself is being deleted).
    /// </summary>
    public async Task<VodShareStopReply> StopAsync(long gameId, CancellationToken ct, bool forgetIfQueued = false)
    {
        VodShareJob? job;
        lock (_gate) job = _running is { } r && r.GameId == gameId ? r : null;
        if (job is not null && Cancel(gameId))
        {
            try { await job.Finished.Task.WaitAsync(StopWaitForJob, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                _logger.LogWarning("Full-game share for game {GameId} did not stop in time", gameId);
            }
        }
        lock (_gate) _last.Remove(gameId);
        var entry = _store.Get(gameId);
        if (entry is null) return new VodShareStopReply(true, false, false);
        var deleted = await ClipShareLinks.DeleteOrQueueAsync(entry.Id, _config, _clips, _cleanup, _logger, ct)
            .ConfigureAwait(false);
        if (deleted) _cleanup.Remove(entry.Id); // an earlier queued delete of this link is done too
        if (deleted || forgetIfQueued) RemoveStored(gameId, entry.Id);
        _logger.LogInformation("Full-game share {Id} for game {GameId}: remote deleted {Deleted}{Kept}",
            entry.Id, gameId, deleted, deleted || forgetIfQueued ? "" : " (link kept until the queued delete runs)");
        return new VodShareStopReply(true, deleted, !deleted);
    }

    /// <summary>For a host that cannot run background work any more: cancel, forget the link, queue its delete.</summary>
    internal void QueueStop(long gameId)
    {
        Cancel(gameId);
        lock (_gate) _last.Remove(gameId);
        if (_store.Remove(gameId) is { } entry) _cleanup.Add(entry.Id);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private VodShareStartReply Refuse(string code, string? error = null, long? retryAt = null, bool needsLogin = false,
        long? busyGameId = null) =>
        VodShareStartReply.Refuse(code, error ?? Message(code), retryAt, needsLogin, busyGameId);

    /// <summary>The recording's length: an ffmpeg probe (5 s, cached by path, size and time), else the game's.</summary>
    private async Task<double?> DurationAsync(long gameId, string path, CancellationToken ct)
    {
        string key;
        try
        {
            var info = new FileInfo(path);
            key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
        lock (_gate) if (_durations.TryGetValue(key, out var cached)) return cached;

        double? seconds = null;
        using (var timeout = new CancellationTokenSource(DurationProbeTimeout))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token))
        {
            try
            {
                var probe = await _mixer.ProbeAsync(path, linked.Token).ConfigureAwait(false);
                if (probe?.DurationSeconds is { } p && double.IsFinite(p) && p > 0) seconds = p;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Full-game share: duration probe failed for game {GameId}", gameId);
            }
        }
        if (seconds is null)
        {
            try
            {
                var game = await _games.GetAsync(gameId).ConfigureAwait(false);
                if (game?.GameDuration > 0) seconds = game.GameDuration;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Full-game share: game {GameId} lookup failed", gameId);
            }
        }
        if (seconds is { } s) lock (_gate) _durations[key] = s;
        return seconds;
    }

    private bool IsBuiltIn(string path)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RecordingsDirectory)) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static long DefaultFreeDiskBytes(string directory)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(directory));
        return string.IsNullOrEmpty(root) ? 0 : new DriveInfo(root).AvailableFreeSpace;
    }

    /// <summary>Fallback copy per error code (the desktop UI shows its own).</summary>
    internal static string Message(string code) => code switch
    {
        "pool_full" => "Sharing space is full right now. Try 720p, or save the file instead.",
        "not_allowed" => "Full-game sharing isn't available for this account.",
        "needs_login" => "Sign in to share this game.",
        "live_limit" => "You already share 3 games. Stop sharing one to share this game.",
        "daily_limit" => "You've reached today's limit for shared games.",
        "busy" => "Another game is still being shared. Try again when it finishes.",
        "starting" => "Revu is still starting. Try again in a moment.",
        "closing" => "Revu is closing. Share again after it restarts.",
        "stopping" => "Stopping the previous share. Try again in a moment.",
        "network" => "Couldn't reach Revu. Check your connection and try again.",
        "sharing_paused" => "Full-game sharing is paused right now. Try again later.",
        "too_long" => "Games longer than 2 hours can't be shared.",
        "too_large" => "This game is too large to share, even at 720p.",
        "no_recording" => "This game's recording isn't on this PC.",
        "disk_full" => "There isn't enough free disk space to prepare the video.",
        "encode_failed" => "Revu couldn't prepare the video. Try again.",
        "game_in_progress" => "Finish your game first, then share.",
        "game_started" => "Sharing stopped because a game started. Share again after the game.",
        "upload_failed" => "The upload didn't finish. Check your connection and try again.",
        "cancelled" => "",
        _ => "Sharing didn't finish. Try again.",
    };

    internal static readonly string[] ErrorCodes =
    [
        "pool_full", "not_allowed", "needs_login", "live_limit", "daily_limit", "busy", "starting", "closing", "stopping",
        "network", "sharing_paused", "too_long", "too_large", "no_recording", "disk_full", "encode_failed",
        "game_in_progress", "game_started", "upload_failed",
    ];
}

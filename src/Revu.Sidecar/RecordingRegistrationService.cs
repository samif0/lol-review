using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Revu.Core.Data;
using Revu.Core.Services;

namespace Revu.Sidecar;

public sealed record RecordingRegistration(Guid SessionId, long GameId, string FilePath, long FileSize,
    double DurationSeconds, DateTimeOffset StartedAt, bool Complete, double? GameTimeAtVideoStart = null);
public sealed record RecordingRegistrationResult(bool Ok, Guid SessionId, long GameId, string Status);

/// <summary>
/// Durable per-session receipts under the owned recording root. The receipt is written
/// before linking; replay after a crash heals a missing game row without guessing a match.
/// Partial recordings stay on disk and cannot replace the match's primary video.
/// </summary>
public sealed class RecordingRegistrationService(string root, RecordingLinkStore links,
    Func<Task> ensureBackup, ILogger<RecordingRegistrationService> logger, Action<long>? onLinked = null)
{
    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public sealed record Receipt(RecordingRegistration Recording, string Status);

    public async Task<RecordingRegistrationResult> RegisterAsync(RecordingRegistration request)
    {
        await _gate.WaitAsync();
        try
        {
            request = ValidateMetadata(request);
            using var media = OpenValidatedMedia(request);
            EnsureJournalDirectory();
            var previous = ReadReceipt(ReceiptPath(request.SessionId));
            if (previous is not null && previous.Recording != request)
                throw new RecordingConflictException("This recording session was already registered with different details.");
            foreach (var receipt in ReadReceipts())
                if (receipt.Recording.SessionId != request.SessionId
                    && receipt.Recording.FilePath.Equals(request.FilePath, StringComparison.OrdinalIgnoreCase))
                    throw new RecordingConflictException("This file already belongs to a different recording session.");

            var current = previous ?? new Receipt(request, request.Complete ? "pending-match" : "retained-partial");
            if (previous is null) await SaveReceiptAsync(current);
            current = await ReconcileOneAsync(current);
            return Result(current);
        }
        finally { _gate.Release(); }
    }

    public async Task<RecordingRegistrationResult?> GetAsync(Guid sessionId)
    {
        await _gate.WaitAsync();
        try
        {
            ValidateRoot();
            var receipt = ReadReceipt(ReceiptPath(sessionId));
            return receipt is null ? null : Result(receipt);
        }
        finally { _gate.Release(); }
    }

    public async Task ReconcileAsync(long? gameId = null)
    {
        await _gate.WaitAsync();
        try
        {
            await ReconcilePendingAsync(gameId);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Existing exact native receipts take priority over timestamp guesses.
    /// Keep the gate through external inserts; an unreadable pending native file
    /// still reserves its match until that receipt can be reconciled.</summary>
    public async Task<VodScanResult> RunExternalScanAsync(
        Func<IReadOnlySet<long>, Task<VodScanResult>> scan, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await ReconcilePendingAsync(null);
            var reserved = ReadReceipts().Where(r => r.Recording.Complete && r.Status == "pending-match")
                .Select(r => r.Recording.GameId).ToHashSet();
            cancellationToken.ThrowIfCancellationRequested();
            return await scan(reserved);
        }
        finally { _gate.Release(); }
    }

    private async Task ReconcilePendingAsync(long? gameId)
    {
        ValidateRoot();
        foreach (var receipt in ReadReceipts())
            {
                if (receipt.Status != "pending-match" || (gameId.HasValue && receipt.Recording.GameId != gameId)) continue;
                try
                {
                    var request = ValidateMetadata(receipt.Recording);
                    using var media = OpenValidatedMedia(request);
                    await ReconcileOneAsync(receipt);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or RecordingConflictException)
                { logger.LogWarning(ex, "Recording registration could not be reconciled for session {SessionId}", receipt.Recording.SessionId); }
        }
    }

    private async Task<Receipt> ReconcileOneAsync(Receipt receipt)
    {
        if (!receipt.Recording.Complete || receipt.Status != "pending-match") return receipt;
        await ensureBackup();
        var status = await links.LinkAsync(receipt.Recording, () => SaveTimingAsync(receipt.Recording));
        if (status == receipt.Status) return receipt;
        var updated = receipt with { Status = status };
        await SaveReceiptAsync(updated);
        if (status == "linked") onLinked?.Invoke(receipt.Recording.GameId);
        return updated;
    }

    private RecordingRegistration ValidateMetadata(RecordingRegistration request)
    {
        if (request.SessionId == Guid.Empty || request.GameId <= 0 || request.FileSize <= 0
            || !double.IsFinite(request.DurationSeconds) || request.DurationSeconds <= 0
            || request.DurationSeconds > TimeSpan.FromDays(1).TotalSeconds
            || request.StartedAt == default || request.StartedAt > DateTimeOffset.UtcNow.AddMinutes(5))
            throw new ArgumentException("Recording session, match, size, duration, and start time must be valid.");
        if ((request.Complete && !request.GameTimeAtVideoStart.HasValue)
            || (request.GameTimeAtVideoStart.HasValue && !RecordingTimeline.IsValidOffset(request.GameTimeAtVideoStart.Value)))
            throw new ArgumentException("Complete recordings need a valid game-clock anchor between -600 and 7200 seconds.");
        if (string.IsNullOrWhiteSpace(request.FilePath) || !Path.IsPathFullyQualified(request.FilePath)
            || request.FilePath.StartsWith("\\\\", StringComparison.Ordinal)
            || request.FilePath.IndexOf(':', Math.Min(2, request.FilePath.Length)) >= 0)
            throw new ArgumentException("Recording path must be an absolute local file path.");
        var path = Path.GetFullPath(request.FilePath);
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || Path.GetRelativePath(_root, path).Split(Path.DirectorySeparatorChar)[0].Equals(".registrations", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Recording files must stay inside Revu's recording folder.");
        if (!new[] { ".mp4", ".mkv", ".mov", ".webm" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Recording must be a supported video file.");
        return request with { FilePath = path, StartedAt = request.StartedAt.ToUniversalTime() };
    }

    private FileStream OpenValidatedMedia(RecordingRegistration request)
    {
        ValidateRoot();
        RejectAliases(request.FilePath);
        var stream = new FileStream(request.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (stream.Length <= 0 || stream.Length != request.FileSize)
                throw new InvalidDataException("Recording file is empty, still changing, or has an unexpected size.");
            if (OperatingSystem.IsWindows())
            {
                if (!GetFileInformationByHandle(stream.SafeFileHandle, out var information))
                    throw new IOException("Recording file identity could not be verified.");
                if (information.NumberOfLinks != 1)
                    throw new InvalidDataException("Recording files cannot be hard links.");
            }
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private string JournalDirectory => Path.Combine(_root, ".registrations");
    private string ReceiptPath(Guid id) => Path.Combine(JournalDirectory, id.ToString("D") + ".json");
    private void ValidateRoot()
    {
        DataRootLease.Canonicalize(_root);
        RejectAliases(JournalDirectory);
    }
    private void EnsureJournalDirectory()
    {
        ValidateRoot();
        Directory.CreateDirectory(JournalDirectory);
        ValidateRoot();
    }
    private static void RejectAliases(string path)
    {
        DataRootLease.Canonicalize(Path.GetDirectoryName(path)!);
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Recording paths cannot traverse symbolic links or junctions.");
    }
    private Receipt? ReadReceipt(string path)
    {
        RejectAliases(path);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 16 * 1024) throw new InvalidDataException("Recording receipt is too large.");
        var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Recording receipt is invalid.");
        if (receipt.Recording.SessionId == Guid.Empty || ReceiptPath(receipt.Recording.SessionId) != path)
            throw new InvalidDataException("Recording receipt identity does not match its filename.");
        return receipt;
    }
    private IEnumerable<Receipt> ReadReceipts()
    {
        if (!Directory.Exists(JournalDirectory)) yield break;
        foreach (var path in Directory.EnumerateFiles(JournalDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            Receipt? receipt;
            try { receipt = ReadReceipt(path); }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or ArgumentException)
            { logger.LogWarning(ex, "An invalid recording receipt was retained"); continue; }
            if (receipt is not null) yield return receipt;
        }
    }
    private async Task SaveReceiptAsync(Receipt receipt)
    {
        EnsureJournalDirectory();
        var destination = ReceiptPath(receipt.Recording.SessionId);
        RejectAliases(destination);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, receipt, Json);
                await stream.FlushAsync();
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task SaveTimingAsync(RecordingRegistration request)
    {
        var destination = request.FilePath + RecordingTimeline.Suffix;
        RejectAliases(destination);
        var timing = new RecordingTiming(1, request.GameId, Path.GetFileName(request.FilePath), request.GameTimeAtVideoStart!.Value);
        if (File.Exists(destination))
        {
            if (RecordingTimeline.ReadGameTimeAtVideoStart(request.FilePath, request.GameId) != timing.GameTimeAtVideoStart)
                throw new RecordingConflictException("Recording timing was already registered differently.");
            return;
        }
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, timing, Json);
                await stream.FlushAsync();
            }
            File.Move(temporary, destination, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static RecordingRegistrationResult Result(Receipt receipt) =>
        new(true, receipt.Recording.SessionId, receipt.Recording.GameId, receipt.Status);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, AccessTime, WriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
}

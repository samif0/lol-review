namespace Revu.Core.Data;

/// <summary>
/// One cooperating writer owns a canonical domain-data directory for its lifetime.
/// Keep the lock file after disposal: unlinking it can let a second owner lock a
/// different file while a waiter still holds the original file open.
/// </summary>
public sealed class DataRootLease : IDisposable
{
    public const string FileName = ".revu-data-owner.lock";
    private readonly FileStream _lock;

    private DataRootLease(string dataDirectory, FileStream fileLock)
    {
        DataDirectory = dataDirectory;
        _lock = fileLock;
    }

    public string DataDirectory { get; }

    /// <summary>The caller creates the directory; acquisition never creates domain data.</summary>
    public static DataRootLease Acquire(string dataDirectory)
    {
        var canonical = Canonicalize(dataDirectory);
        if (!Directory.Exists(canonical))
            throw new DirectoryNotFoundException("Create the data directory before acquiring ownership.");

        var lockPath = Path.Combine(canonical, FileName);
        if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The data ownership lock cannot be a link.");

        try
        {
            return new DataRootLease(canonical,
                new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException ex)
        {
            throw new IOException("The data directory is already owned or its ownership lock is unavailable.", ex);
        }
    }

    /// <summary>
    /// Resolve dot segments and trailing separators. Reject junctions/symlinks in
    /// the path so two lexical roots cannot silently obtain different owner locks.
    /// </summary>
    public static string Canonicalize(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        for (var entry = new DirectoryInfo(canonical); entry is not null; entry = entry.Parent)
        {
            if (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Data directories reached through junctions or symbolic links are unsupported.");
        }
        return canonical;
    }

    public void Dispose() => _lock.Dispose();
}

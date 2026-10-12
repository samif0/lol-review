#nullable enable

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Revu.Core.Data;

namespace Revu.Sidecar;

/// <summary>
/// Size-capped rolling file log: sidecar.log plus older generations (sidecar.1.log ...)
/// under &lt;data root&gt;/Revu/logs. The desktop host discards the sidecar's console, so
/// this file is where warnings, failures and crash reasons survive. Messages never carry
/// request or response bodies, and emails or credentials that reach a message are masked
/// before writing. Logging never throws into the caller.
/// </summary>
public sealed partial class SidecarFileLoggerProvider : ILoggerProvider
{
    public const long DefaultMaxBytes = 2 * 1024 * 1024;
    public const int DefaultMaxFiles = 5;
    public const string FileName = "sidecar.log";

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private readonly LogLevel _minimumLevel;
    private long _rotateAfter;
    private bool _disposed;

    public SidecarFileLoggerProvider(string directory, long maxBytes = DefaultMaxBytes,
        int maxFiles = DefaultMaxFiles, LogLevel minimumLevel = LogLevel.Information)
    {
        _directory = directory;
        _maxBytes = Math.Max(1024, maxBytes);
        _maxFiles = Math.Max(1, maxFiles);
        _minimumLevel = minimumLevel;
    }

    /// <summary>&lt;data root&gt;/Revu/logs; follows REVU_DATA_ROOT through <see cref="AppDataPaths"/>.</summary>
    public static string DefaultDirectory => Path.Combine(AppDataPaths.SidecarHandshakeDirectory, "logs");

    public string CurrentPath => Path.Combine(_directory, FileName);

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <summary>Last record before the runtime terminates on an unhandled exception.</summary>
    public void WriteUnhandled(object? exception) =>
        Write(LogLevel.Critical, "Unhandled", "Unhandled exception; the sidecar is terminating", exception as Exception);

    internal bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= _minimumLevel;

    internal void Write(LogLevel level, string category, string message, Exception? exception)
    {
        if (!IsEnabled(level)) return;
        var text = new StringBuilder()
            .Append(DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
            .Append(' ').Append(LevelName(level)).Append(' ')
            .Append(category).Append(": ").Append(message);
        if (exception is not null)
            text.Append(Environment.NewLine).Append("  ")
                .Append(exception.ToString().Replace("\n", "\n  "));
        text.Append(Environment.NewLine);
        var bytes = Encoding.UTF8.GetBytes(Redact(text.ToString()));
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                // Open per record so any reader (Notepad, a support upload) can open the
                // file while the sidecar runs, and nothing is left unflushed at a crash.
                var stream = Open();
                try
                {
                    if (stream.Length > 0 && stream.Length + bytes.Length > _maxBytes && stream.Length >= _rotateAfter)
                    {
                        stream.Dispose();
                        stream = Rotate();
                    }
                    stream.Write(bytes);
                }
                finally { stream.Dispose(); }
            }
            catch (Exception)
            {
                // A full, locked or missing disk must never break a request.
            }
        }
    }

    /// <summary>Mask emails, bearer and API credentials, and long hex tokens.</summary>
    public static string Redact(string text)
    {
        text = EmailPattern().Replace(text, "[email]");
        text = AuthorizationPattern().Replace(text, "$1 [redacted]");
        text = QueryCredentialPattern().Replace(text, "$1=[redacted]");
        text = RiotKeyPattern().Replace(text, "[redacted]");
        text = JwtPattern().Replace(text, "[redacted]");
        return HexTokenPattern().Replace(text, "[redacted]");
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
    }

    private FileStream Open()
    {
        Directory.CreateDirectory(_directory);
        return new FileStream(CurrentPath, FileMode.Append, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
    }

    private FileStream Rotate()
    {
        for (var index = _maxFiles - 1; index > 0; index--)
        {
            var source = index == 1 ? CurrentPath : Generation(index - 1);
            try { if (File.Exists(source)) File.Move(source, Generation(index), overwrite: true); }
            catch (Exception) { /* A reader without delete sharing blocks this generation only. */ }
        }
        if (_maxFiles == 1)
        {
            try { File.Delete(CurrentPath); } catch (Exception) { }
        }
        var stream = Open();
        // Rotation was blocked: keep appending and retry after another slice, not every line.
        _rotateAfter = stream.Length > 0 ? stream.Length + _maxBytes / 8 : 0;
        return stream;
    }

    private string Generation(int index) => Path.Combine(_directory, $"sidecar.{index}.log");

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO ",
        LogLevel.Warning => "WARN ",
        LogLevel.Error => "ERROR",
        _ => "CRIT ",
    };

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(?i)\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]+")]
    private static partial Regex AuthorizationPattern();

    [GeneratedRegex(@"(?i)\b(access_token|token|password|secret|api_?key|code)=[^&\s""']+")]
    private static partial Regex QueryCredentialPattern();

    [GeneratedRegex(@"\bRGAPI-[A-Za-z0-9-]+")]
    private static partial Regex RiotKeyPattern();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*")]
    private static partial Regex JwtPattern();

    [GeneratedRegex(@"\b[A-Fa-f0-9]{32,}\b")]
    private static partial Regex HexTokenPattern();

    private sealed class FileLogger : ILogger
    {
        private readonly SidecarFileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(SidecarFileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => _provider.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            string message;
            try { message = formatter(state, exception); }
            catch (Exception) { message = state?.ToString() ?? ""; }
            _provider.Write(logLevel, _category, message, exception);
        }
    }
}

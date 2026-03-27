#nullable enable

using System.Management;
using System.Text.RegularExpressions;
using LoLReview.Core.Models;
using Microsoft.Extensions.Logging;

namespace LoLReview.Core.Lcu;

/// <summary>
/// Discovers LCU credentials from the running League client process or lockfile.
/// Ported from Python credentials.py.
/// </summary>
public sealed class LcuCredentialDiscovery : ILcuCredentialDiscovery
{
    private readonly ILogger<LcuCredentialDiscovery> _logger;

    public LcuCredentialDiscovery(ILogger<LcuCredentialDiscovery> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public LcuCredentials? FindCredentials()
    {
        var creds = FindFromProcess();
        if (creds is not null)
        {
            _logger.LogInformation("Found LCU via process inspection (port {Port})", creds.Port);
            return creds;
        }

        creds = FindFromLockfile();
        if (creds is not null)
        {
            _logger.LogInformation("Found LCU via lockfile (port {Port})", creds.Port);
            return creds;
        }

        return null;
    }

    /// <inheritdoc />
    public LcuCredentials? FindFromProcess()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT CommandLine FROM Win32_Process WHERE Name = 'LeagueClientUx.exe'");

            using var results = searcher.Get();

            foreach (ManagementObject obj in results)
            {
                var commandLine = obj["CommandLine"]?.ToString();
                if (string.IsNullOrEmpty(commandLine))
                    continue;

                var portMatch = Regex.Match(commandLine, @"--app-port=(\d+)");
                var tokenMatch = Regex.Match(commandLine, @"--remoting-auth-token=([\w_-]+)");
                var pidMatch = Regex.Match(commandLine, @"--app-pid=(\d+)");

                if (portMatch.Success && tokenMatch.Success)
                {
                    return new LcuCredentials
                    {
                        Pid = pidMatch.Success ? int.Parse(pidMatch.Groups[1].Value) : 0,
                        Port = int.Parse(portMatch.Groups[1].Value),
                        Password = tokenMatch.Groups[1].Value,
                        Protocol = "https",
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Process inspection failed");
        }

        return null;
    }

    /// <inheritdoc />
    public LcuCredentials? FindFromLockfile()
    {
        var lockfilePaths = new List<string>();

        // LEAGUE_PATH environment variable
        var leaguePath = Environment.GetEnvironmentVariable("LEAGUE_PATH");
        if (!string.IsNullOrEmpty(leaguePath))
        {
            lockfilePaths.Add(Path.Combine(leaguePath, "lockfile"));
        }

        // Common install locations
        lockfilePaths.Add(@"C:\Riot Games\League of Legends\lockfile");
        lockfilePaths.Add(@"D:\Riot Games\League of Legends\lockfile");

        // User home directory
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        lockfilePaths.Add(Path.Combine(home, "Riot Games", "League of Legends", "lockfile"));

        foreach (var lockfilePath in lockfilePaths)
        {
            if (!File.Exists(lockfilePath))
                continue;

            try
            {
                var content = File.ReadAllText(lockfilePath).Trim();
                var parts = content.Split(':');

                if (parts.Length >= 5)
                {
                    return new LcuCredentials
                    {
                        Pid = int.Parse(parts[1]),
                        Port = int.Parse(parts[2]),
                        Password = parts[3],
                        Protocol = parts[4],
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to read lockfile at {Path}", lockfilePath);
            }
        }

        return null;
    }
}

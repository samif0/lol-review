#nullable enable

using System.Text;

namespace Revu.Core.Models;

/// <summary>
/// Auth credentials parsed from the League client lockfile or process.
/// </summary>
public class LcuCredentials
{
    public int Pid { get; set; }
    public int Port { get; set; }
    public string Password { get; set; } = "";
    public string Protocol { get; set; } = "https";

    /// <summary>Base URL for LCU REST API calls.</summary>
    public string BaseUrl => $"https://127.0.0.1:{Port}";

    /// <summary>Base64-encoded "riot:{Password}" value for the Authorization header.</summary>
    public string AuthHeaderValue =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{Password}"));
}

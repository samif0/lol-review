#nullable enable

namespace Revu.Core.Services;

/// <summary>
/// Compile-time Riot proxy endpoint. The URL is fixed; users never configure it.
/// If this needs to change (e.g. Worker rename), update here and rebuild.
/// </summary>
public static class RiotProxyEndpoint
{
    public const string BaseUrl = "https://revu-proxy.lol-review.workers.dev";
}

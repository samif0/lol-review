using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Lcu;
using Revu.Core.Models;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// Pins every request the app can make to the League client's local API and to the
/// game's Live Client Data API. <see cref="ExpectedEndpoints"/> is the whole list. A new
/// path, a new method on a path, or a request built anywhere outside the three known
/// files fails here until the list is changed on purpose.
///
/// Two independent checks agree on the list: a scan of the source, which also sees
/// code nothing calls yet, and a run of every client method against a recording
/// handler, which sees what actually goes on the wire.
///
/// The list must never hold a request that answers a found match. Accepting or
/// declining one affects the other players in that lobby, so it stays with the player.
/// </summary>
public sealed partial class LeagueClientEndpointContractTests
{
    // Method, then the path as the source writes it. {name} marks a value filled in at
    // run time. Keep this sorted. Changing it changes what the app is allowed to call.
    private const string ExpectedEndpoints = """
        DELETE /lol-lobby/v2/lobby/matchmaking/search
        GET /liveclientdata/activeplayer
        GET /liveclientdata/activeplayername
        GET /liveclientdata/allgamedata
        GET /liveclientdata/eventdata
        GET /liveclientdata/gamestats
        GET /liveclientdata/playerlist
        GET /lol-champ-select/v1/session
        GET /lol-end-of-game/v1/eog-stats-block
        GET /lol-game-data/assets/v1/champion-summary.json
        GET /lol-gameflow/v1/gameflow-phase
        GET /lol-gameflow/v1/session
        GET /lol-match-history/v1/games/{gameId}
        GET /lol-match-history/v1/products/lol/{puuid}/matches?begIndex={begin}&endIndex={end}
        GET /lol-ranked/v1/current-ranked-stats
        GET /lol-summoner/v1/current-summoner
        """;

    // The one request on the list that only the opt-in practice diagnostic makes. It does
    // not go through LiveEventApi, so the wire check below cannot see it; the source scan
    // does, and PracticeCaptureTests asserts its exact address.
    private const string DiagnosticOnlyEndpoint = "GET /liveclientdata/allgamedata";

    private const string LcuClientFile = "src/Revu.Core/Lcu/LcuClient.cs";
    private const string LiveEventApiFile = "src/Revu.Core/Lcu/LiveEventApi.cs";
    private const string PracticeCaptureFile = "src/Revu.Sidecar/PracticeCaptureService.cs";
    private const string LiveClientOrigin = "https://127.0.0.1:2999";

    private static string[] Expected() => ExpectedEndpoints
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Fact]
    public void ThePinnedListIsSortedAndHasNoDuplicates()
    {
        var expected = Expected();
        Assert.Equal(expected.Order(StringComparer.Ordinal).ToArray(), expected);
        Assert.Equal(expected.Length, expected.Distinct().Count());
        Assert.Contains(DiagnosticOnlyEndpoint, expected);
    }

    [Fact]
    public void EveryRequestInTheSourceIsOnThePinnedList()
    {
        var found = ScanSource();

        Assert.Equal(
            Expected().Select(Holes).Order(StringComparer.Ordinal).ToArray(),
            found.Requests.Select(request => Holes(request.Endpoint)).Distinct().Order(StringComparer.Ordinal).ToArray());

        // Each request sits in one of the three known files.
        Assert.Equal(
            new[] { LcuClientFile, LiveEventApiFile, PracticeCaptureFile },
            found.Requests.Select(request => request.File).Distinct().Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void NoPathIsBuiltOutsideARecognisedRequest()
    {
        var found = ScanSource();

        // A League-shaped path in a constant, a field or a helper argument would reach the
        // client without appearing in the scan above. Every such literal must be the path
        // argument of a recognised request.
        Assert.True(found.StrayPathLiterals.Count == 0,
            "League client or Live Client Data paths outside a recognised request call:\n"
            + string.Join("\n", found.StrayPathLiterals));

        // Inside the two client classes every request goes through the private helpers
        // with a literal path, so no caller can hand in a path of its own.
        Assert.True(found.HelperCallsWithoutLiteralPath.Count == 0,
            "Request helper calls whose path is not a string literal:\n"
            + string.Join("\n", found.HelperCallsWithoutLiteralPath));
        Assert.Equal(new Dictionary<string, int> { [LcuClientFile] = 2, [LiveEventApiFile] = 1 }, found.RawSends);

        // The scan must actually have read the clients (a moved file would otherwise pass).
        Assert.True(found.Requests.Count >= 16, $"Only {found.Requests.Count} request call sites were scanned.");
    }

    [Fact]
    public void OnlyTheKnownFilesCanReachTheLeagueClientOrTheGame()
    {
        var found = ScanSource();

        // The League client needs its port and token. These are the only files that handle them.
        Assert.Equal(
            new[]
            {
                "src/Revu.Core/Lcu/ILcuClient.cs",
                "src/Revu.Core/Lcu/ILcuCredentialDiscovery.cs",
                LcuClientFile,
                "src/Revu.Core/Lcu/LcuCredentialDiscovery.cs",
                "src/Revu.Core/Models/LcuCredentials.cs",
            },
            found.FilesNaming("LcuCredentials"));

        // The Live Client Data API is a fixed local address. These are the only files that name it.
        Assert.Equal(new[] { LiveEventApiFile, PracticeCaptureFile }, found.FilesNaming(":2999"));

        // The two HTTP clients that trust the local certificate are handed out here only.
        Assert.Equal(
            new[] { "src/Revu.Sidecar/Hosting/SidecarServices.cs", PracticeCaptureFile },
            found.FilesNaming("\"LcuClient\"", "\"LiveEventApi\""));
    }

    [Fact]
    public void NothingAnswersAFoundMatch()
    {
        var expected = Expected();

        // The only request that changes anything in the client cancels the player's own search.
        Assert.Equal(
            new[] { "DELETE /lol-lobby/v2/lobby/matchmaking/search" },
            expected.Where(endpoint => !endpoint.StartsWith("GET ", StringComparison.Ordinal)).ToArray());

        foreach (var endpoint in expected)
        {
            Assert.DoesNotContain("ready-check", endpoint, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/lol-matchmaking/", endpoint, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("accept", endpoint, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("decline", endpoint, StringComparison.OrdinalIgnoreCase);
        }

        // Not in a request, not in a constant, not in a comment someone could copy from.
        var root = RepoRoot();
        var mentions = SourceFiles(root)
            .Where(path => File.ReadAllText(path).Contains("ready-check", StringComparison.OrdinalIgnoreCase))
            .Select(path => Relative(root, path))
            .ToArray();
        Assert.Empty(mentions);

        Assert.DoesNotContain(typeof(ILcuClient).GetMethods(), method =>
            method.Name.Contains("ReadyCheck", StringComparison.OrdinalIgnoreCase)
            || method.Name.Contains("Accept", StringComparison.OrdinalIgnoreCase)
            || method.Name.Contains("Decline", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EveryClientMethodSendsOnlyPinnedRequests()
    {
        var sent = new ConcurrentDictionary<string, byte>();

        using var lcuHttp = new HttpClient(new Recorder(sent));
        var lcu = new LcuClient(lcuHttp, NullLogger<LcuClient>.Instance);
        lcu.Configure(new LcuCredentials { Port = 4242, Password = "test-token" });
        await InvokeEveryMethodAsync<ILcuClient>(lcu);

        using var liveHttp = new HttpClient(new Recorder(sent));
        var live = new LiveEventApi(liveHttp, NullLogger<LiveEventApi>.Instance);
        await InvokeEveryMethodAsync<ILiveEventApi>(live);

        Assert.Equal(
            Expected().Where(endpoint => endpoint != DiagnosticOnlyEndpoint).Select(Holes).Order(StringComparer.Ordinal).ToArray(),
            sent.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    // ── Wire check ───────────────────────────────────────────────────────────

    private const int Number = 4242;
    private const string Puuid = "test-puuid";

    /// <summary>Calls every method the interface declares, so a method added later is
    /// exercised without anyone remembering to add it here.</summary>
    private static async Task InvokeEveryMethodAsync<T>(T client) where T : class
    {
        var invoked = 0;
        foreach (var method in typeof(T).GetMethods())
        {
            if (method.Name == nameof(ILcuClient.Configure)) continue;
            var arguments = method.GetParameters().Select(parameter => parameter.ParameterType switch
            {
                var type when type == typeof(CancellationToken) => (object)CancellationToken.None,
                var type when type == typeof(int) => Number,
                var type when type == typeof(long) => (long)Number,
                var type => throw new InvalidOperationException(
                    $"{typeof(T).Name}.{method.Name} takes a {type.Name}. Teach this test what to pass, so the method stays covered."),
            }).ToArray();
            if (method.Invoke(client, arguments) is Task task) await task;
            invoked++;
        }
        Assert.True(invoked >= 5, $"Only {invoked} methods of {typeof(T).Name} were invoked.");
    }

    /// <summary>Records each request as "METHOD /path" with run-time values replaced by
    /// {}, and answers with just enough for every method to go on to its next request.</summary>
    private sealed partial class Recorder(ConcurrentDictionary<string, byte> sent) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Assert.True(uri.IsLoopback, $"A client method called a non-local address: {uri}");
            var path = uri.PathAndQuery;
            sent[$"{request.Method.Method} {RunTimeNumber().Replace(path.Replace(Puuid, "{}"), "{}")}"] = 0;

            var body = path switch
            {
                "/lol-summoner/v1/current-summoner" => $$"""{"puuid":"{{Puuid}}"}""",
                "/lol-gameflow/v1/gameflow-phase" => "\"Lobby\"",
                "/lol-game-data/assets/v1/champion-summary.json" => "[]",
                _ => "{}",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }

        [GeneratedRegex(@"(?<![\w.])\d{4,}(?![\w.])")]
        private static partial Regex RunTimeNumber();
    }

    // ── Source scan ──────────────────────────────────────────────────────────

    private sealed record Request(string File, string Endpoint);

    private sealed record Scan(
        List<Request> Requests,
        List<string> StrayPathLiterals,
        List<string> HelperCallsWithoutLiteralPath,
        Dictionary<string, int> RawSends,
        Dictionary<string, string> Code)
    {
        public string[] FilesNaming(params string[] needles) => Code
            .Where(file => needles.Any(needle => file.Value.Contains(needle, StringComparison.Ordinal)))
            .Select(file => file.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static Scan ScanSource()
    {
        var root = RepoRoot();
        var scan = new Scan([], [], [], [], []);
        foreach (var path in SourceFiles(root))
        {
            var file = Relative(root, path);
            var code = CommentLine().Replace(File.ReadAllText(path), "");
            scan.Code[file] = code;
            var isClient = file is LcuClientFile or LiveEventApiFile;

            var requestPathSpans = new HashSet<int>();
            foreach (Match call in RequestCall().Matches(code))
            {
                var literal = call.Groups["path"].Value;
                // In the two client classes every helper call is a request to the client or
                // the game, whatever its path looks like. Anywhere else, only a League-shaped
                // address counts (other code calls other services through its own GetAsync).
                if (!isClient && !IsLeaguePath(literal)) continue;
                var method = call.Groups["verb"].Success ? call.Groups["verb"].Value.ToUpperInvariant() : "GET";
                var endpoint = literal.StartsWith(LiveClientOrigin, StringComparison.Ordinal) ? literal[LiveClientOrigin.Length..] : literal;
                scan.Requests.Add(new Request(file, $"{method} {endpoint}"));
                requestPathSpans.Add(call.Groups["path"].Index);
            }

            foreach (Match literal in StringLiteral().Matches(code))
            {
                var body = literal.Groups["body"];
                if (IsLeaguePath(body.Value) && !requestPathSpans.Contains(body.Index))
                    scan.StrayPathLiterals.Add($"{file}:{LineOf(code, literal.Index)}: {literal.Value}");
            }

            if (!isClient) continue;
            var literalCalls = RequestCall().Matches(code).Select(call => call.Index).ToHashSet();
            foreach (Match call in HelperCall().Matches(code))
            {
                if (HelperDeclaration().IsMatch(code[..call.Index])) continue;
                if (!literalCalls.Contains(call.Index))
                    scan.HelperCallsWithoutLiteralPath.Add($"{file}:{LineOf(code, call.Index)}");
            }
            scan.RawSends[file] = RawSend().Matches(code).Count;
        }
        return scan;
    }

    private static string Holes(string endpoint) => Hole().Replace(endpoint, "{}");

    private static bool IsLeaguePath(string literal) =>
        LeaguePath().IsMatch(literal) && !literal.Contains("/lol-review", StringComparison.Ordinal);

    private static int LineOf(string code, int index) => code.AsSpan(0, index).Count('\n') + 1;

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    // Every shipped C# project under src/ (test projects are not shipped).
    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateDirectories(Path.Combine(root, "src"))
            .Where(directory => !directory.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase))
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Revu.Core")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    // A whole-line comment. Doc comments name paths in prose and are not requests; the
    // line breaks stay so reported line numbers match the file.
    [GeneratedRegex(@"^[ \t]*//[^\r\n]*", RegexOptions.Multiline)]
    private static partial Regex CommentLine();

    // GetAsync("...") on the private helpers or on an HttpClient, and
    // SendNoContentAsync(HttpMethod.Verb, "..."). \x22 is a double quote.
    [GeneratedRegex(@"\b(?:GetAsync\(\s*|SendNoContentAsync\(\s*HttpMethod\.(?<verb>\w+)\s*,\s*)\$?\x22(?<path>[^\x22\r\n]*)\x22")]
    private static partial Regex RequestCall();

    [GeneratedRegex(@"(?<![\w.])(?:GetAsync|SendNoContentAsync)\(")]
    private static partial Regex HelperCall();

    [GeneratedRegex(@"\bTask(?:<[^>\r\n]*>)?\s+$")]
    private static partial Regex HelperDeclaration();

    [GeneratedRegex(@"\b_httpClient\.\w+Async\(")]
    private static partial Regex RawSend();

    [GeneratedRegex(@"\$?\x22(?<body>(?:[^\x22\\\r\n]|\\.)*)\x22")]
    private static partial Regex StringLiteral();

    // The League client serves its plugins under /lol-<name>/ and a few other roots; the
    // game serves /liveclientdata/.
    [GeneratedRegex(@"/(?:lol-[a-z0-9-]+|liveclientdata|riotclient|riot-messaging-service|process-control|player-notifications)/", RegexOptions.IgnoreCase)]
    private static partial Regex LeaguePath();

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex Hole();
}

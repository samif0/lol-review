#nullable enable

using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace LoLReview.App.Services;

public sealed class CoachApiClient : ICoachApiClient
{
    private readonly HttpClient _http;
    private readonly CoachSidecarService _sidecar;
    private readonly ILogger<CoachApiClient> _logger;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public CoachApiClient(
        IHttpClientFactory httpFactory,
        CoachSidecarService sidecar,
        ILogger<CoachApiClient> logger)
    {
        _http = httpFactory.CreateClient("CoachApi");
        _http.Timeout = TimeSpan.FromMinutes(2);
        _sidecar = sidecar;
        _logger = logger;
    }

    private Uri Url(string path) => new($"http://127.0.0.1:{_sidecar.Port}{path}");

    public async Task<bool> HealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var r = await _http.GetAsync(Url("/health"), cancellationToken);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<CoachTestPromptResponse?> TestPromptAsync(string prompt, CancellationToken cancellationToken = default)
    {
        try
        {
            var r = await _http.PostAsJsonAsync(Url("/coach/test-prompt"), new { prompt }, JsonOpts, cancellationToken);
            if (!r.IsSuccessStatusCode) return null;
            var payload = await r.Content.ReadFromJsonAsync<TestPromptPayload>(JsonOpts, cancellationToken);
            if (payload is null) return null;
            return new CoachTestPromptResponse(payload.Text, payload.Model, payload.Provider, payload.LatencyMs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "test-prompt failed");
            return null;
        }
    }

    public async Task<CoachBuildSummaryResponse?> BuildSummaryAsync(long gameId, CancellationToken cancellationToken = default)
    {
        try
        {
            var r = await _http.PostAsync(Url($"/summaries/build/{gameId}"), content: null, cancellationToken);
            if (!r.IsSuccessStatusCode) return null;
            var payload = await r.Content.ReadFromJsonAsync<BuildSummaryPayload>(JsonOpts, cancellationToken);
            if (payload is null) return null;
            return new CoachBuildSummaryResponse(payload.GameId, payload.SummaryVersion, payload.TokenCount, payload.Ok, payload.Error);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "summaries/build failed for game {GameId}", gameId);
            return null;
        }
    }

    public async Task<string?> GetSummaryJsonAsync(long gameId, CancellationToken cancellationToken = default)
    {
        try
        {
            var r = await _http.GetAsync(Url($"/summaries/{gameId}"), cancellationToken);
            if (!r.IsSuccessStatusCode) return null;
            return await r.Content.ReadAsStringAsync(cancellationToken);
        }
        catch { return null; }
    }

    public async Task<bool> ExtractConceptsAsync(long gameId, CancellationToken cancellationToken = default)
    {
        try
        {
            var r = await _http.PostAsync(Url($"/concepts/extract/{gameId}"), content: null, cancellationToken);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<bool> ReclusterConceptsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var r = await _http.PostAsync(Url("/concepts/recluster"), content: null, cancellationToken);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<bool> ComputeFeaturesAsync(long gameId, CancellationToken cancellationToken = default)
    {
        try
        {
            var r = await _http.PostAsync(Url($"/signals/compute-features/{gameId}"), content: null, cancellationToken);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<bool> RerankSignalsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var r = await _http.PostAsync(Url("/signals/rerank"), content: null, cancellationToken);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public Task<CoachDraftResponse?> DraftPostGameAsync(long gameId, CancellationToken cancellationToken = default) =>
        DraftAsync("/coach/post-game", new { game_id = gameId }, cancellationToken);

    public Task<CoachDraftResponse?> DraftClipReviewAsync(long bookmarkId, CancellationToken cancellationToken = default) =>
        DraftAsync("/coach/clip-review", new { bookmark_id = bookmarkId }, cancellationToken);

    public Task<CoachDraftResponse?> DraftSessionAsync(long? since, long? until, CancellationToken cancellationToken = default) =>
        DraftAsync("/coach/session", new { since, until }, cancellationToken);

    public Task<CoachDraftResponse?> DraftWeeklyAsync(long? since, long? until, CancellationToken cancellationToken = default) =>
        DraftAsync("/coach/weekly", new { since, until }, cancellationToken);

    private async Task<CoachDraftResponse?> DraftAsync(string path, object body, CancellationToken cancellationToken)
    {
        try
        {
            var r = await _http.PostAsJsonAsync(Url(path), body, JsonOpts, cancellationToken);
            if (!r.IsSuccessStatusCode) return null;
            var payload = await r.Content.ReadFromJsonAsync<CoachResponsePayload>(JsonOpts, cancellationToken);
            if (payload is null) return null;
            var json = payload.ResponseJson is null
                ? null
                : JsonSerializer.Serialize(payload.ResponseJson, JsonOpts);
            return new CoachDraftResponse(
                payload.CoachSessionId,
                payload.Mode,
                payload.ResponseText,
                json,
                payload.Model,
                payload.Provider,
                payload.LatencyMs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "coach mode {Path} failed", path);
            return null;
        }
    }

    public async Task<bool> LogEditAsync(long coachSessionId, string editedText, CancellationToken cancellationToken = default)
    {
        try
        {
            var r = await _http.PostAsJsonAsync(Url("/coach/log-edit"),
                new { coach_session_id = coachSessionId, edited_text = editedText }, JsonOpts, cancellationToken);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<bool> DescribeBookmarkAsync(long bookmarkId, CancellationToken cancellationToken = default)
    {
        try
        {
            var r = await _http.PostAsync(Url($"/vision/describe-bookmark/{bookmarkId}"), content: null, cancellationToken);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<bool> UpdateConfigAsync(CoachConfigUpdate update, CancellationToken cancellationToken = default)
    {
        try
        {
            var body = new Dictionary<string, object?>
            {
                ["provider"] = update.Provider,
                ["port"] = update.Port,
                ["vision_override_provider"] = update.VisionOverrideProvider,
            };
            if (update.Ollama is { } ol)
                body["ollama"] = new Dictionary<string, object?>
                {
                    ["base_url"] = ol.BaseUrl,
                    ["model"] = ol.Model,
                    ["vision_model"] = ol.VisionModel,
                };
            if (update.GoogleAi is { } gai)
                body["google_ai"] = new Dictionary<string, object?>
                {
                    ["model"] = gai.Model,
                    ["api_key"] = gai.ApiKey,
                };
            if (update.OpenRouter is { } orr)
                body["openrouter"] = new Dictionary<string, object?>
                {
                    ["model"] = orr.Model,
                    ["api_key"] = orr.ApiKey,
                };

            var r = await _http.PostAsJsonAsync(Url("/config"), body, JsonOpts, cancellationToken);
            return r.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "update config failed");
            return false;
        }
    }

    public async Task<CoachAskResponse?> AskAsync(string question, long? threadId = null, CoachScope? scope = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var body = new Dictionary<string, object?>
            {
                ["question"] = question,
                ["thread_id"] = threadId,
            };
            if (scope is not null)
            {
                var scopeDict = new Dictionary<string, object?>();
                if (scope.GameId is not null) scopeDict["game_id"] = scope.GameId;
                if (scope.Since is not null) scopeDict["since"] = scope.Since;
                if (scope.Until is not null) scopeDict["until"] = scope.Until;
                if (scopeDict.Count > 0) body["scope"] = scopeDict;
            }
            var r = await _http.PostAsJsonAsync(Url("/coach/ask"), body, JsonOpts, cancellationToken);
            if (!r.IsSuccessStatusCode)
            {
                _logger.LogWarning("ask failed: {Status}", r.StatusCode);
                return null;
            }
            var payload = await r.Content.ReadFromJsonAsync<AskPayload>(JsonOpts, cancellationToken);
            if (payload is null) return null;
            return new CoachAskResponse(
                payload.ThreadId,
                ToChatMessage(payload.UserMessage),
                ToChatMessage(payload.AssistantMessage),
                payload.CoachVisibleTotals ?? new Dictionary<string, int>());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ask failed");
            return null;
        }
    }

    public async IAsyncEnumerable<CoachAskStreamEvent> AskStreamAsync(
        string question,
        long? threadId = null,
        CoachScope? scope = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["question"] = question,
            ["thread_id"] = threadId,
        };
        if (scope is not null)
        {
            var scopeDict = new Dictionary<string, object?>();
            if (scope.GameId is not null) scopeDict["game_id"] = scope.GameId;
            if (scope.Since is not null) scopeDict["since"] = scope.Since;
            if (scope.Until is not null) scopeDict["until"] = scope.Until;
            if (scopeDict.Count > 0) body["scope"] = scopeDict;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Url("/coach/ask-stream"))
        {
            Content = JsonContent.Create(body, options: JsonOpts),
        };

        HttpResponseMessage? response = null;
        string? connectError = null;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ask-stream connect failed");
            connectError = "Connect failed. Is the sidecar running?";
        }
        if (connectError is not null || response is null)
        {
            yield return new CoachAskStreamError(connectError ?? "Connect failed.");
            yield break;
        }

        if (!response.IsSuccessStatusCode)
        {
            yield return new CoachAskStreamError($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            response.Dispose();
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = null;
            string? readError = null;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "stream read failed");
                readError = "Stream read failed.";
            }
            if (readError is not null)
            {
                yield return new CoachAskStreamError(readError);
                break;
            }
            if (line is null) break;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var json = line["data: ".Length..];
            if (json == "[DONE]") break;

            CoachAskStreamEvent? evt = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                switch (type)
                {
                    case "started":
                    {
                        var tid = root.GetProperty("thread_id").GetInt64();
                        var umid = root.GetProperty("user_message_id").GetInt64();
                        var totals = new Dictionary<string, int>();
                        if (root.TryGetProperty("coach_visible_totals", out var tt) && tt.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in tt.EnumerateObject())
                            {
                                if (prop.Value.TryGetInt32(out var v))
                                    totals[prop.Name] = v;
                            }
                        }
                        evt = new CoachAskStreamStarted(tid, umid, totals);
                        break;
                    }
                    case "thinking":
                        evt = new CoachAskStreamThinking();
                        break;
                    case "delta":
                        evt = new CoachAskStreamDelta(root.GetProperty("text").GetString() ?? "");
                        break;
                    case "done":
                        evt = new CoachAskStreamDone(
                            root.GetProperty("assistant_message_id").GetInt64(),
                            root.GetProperty("model").GetString() ?? "",
                            root.GetProperty("provider").GetString() ?? "",
                            root.TryGetProperty("latency_ms", out var lm) ? lm.GetInt32() : 0);
                        break;
                    case "error":
                        evt = new CoachAskStreamError(root.GetProperty("message").GetString() ?? "unknown error");
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "bad sse chunk: {Line}", line);
                continue;
            }

            if (evt is not null)
                yield return evt;

            if (evt is CoachAskStreamDone or CoachAskStreamError)
                break;
        }

        response.Dispose();
    }

    public async Task<CoachThread?> GetThreadAsync(long threadId, CancellationToken cancellationToken = default)
    {
        try
        {
            var r = await _http.GetAsync(Url($"/coach/threads/{threadId}"), cancellationToken);
            if (!r.IsSuccessStatusCode) return null;
            var payload = await r.Content.ReadFromJsonAsync<ThreadPayload>(JsonOpts, cancellationToken);
            if (payload is null) return null;
            return new CoachThread(
                payload.Id,
                payload.Title,
                ScopeFromDict(payload.Scope),
                payload.CreatedAt,
                payload.UpdatedAt,
                payload.Messages.Select(ToChatMessage).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "get thread failed");
            return null;
        }
    }

    public async Task<IReadOnlyList<CoachThreadSummary>> ListThreadsAsync(int limit = 50, CancellationToken cancellationToken = default)
    {
        try
        {
            var r = await _http.GetAsync(Url($"/coach/threads?limit={limit}"), cancellationToken);
            if (!r.IsSuccessStatusCode) return [];
            var payload = await r.Content.ReadFromJsonAsync<ThreadListPayload>(JsonOpts, cancellationToken);
            if (payload?.Threads is null) return [];
            return payload.Threads
                .Select(t => new CoachThreadSummary(
                    t.Id, t.Title, ScopeFromDict(t.Scope), t.CreatedAt, t.UpdatedAt))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "list threads failed");
            return [];
        }
    }

    public async Task<CoachGenerateObjectiveResponse?> GenerateObjectiveAsync(long? since = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var body = new Dictionary<string, object?> { ["since"] = since };
            var r = await _http.PostAsJsonAsync(Url("/coach/generate-objective"), body, JsonOpts, cancellationToken);
            if (!r.IsSuccessStatusCode) return null;
            var payload = await r.Content.ReadFromJsonAsync<GenerateObjectivePayload>(JsonOpts, cancellationToken);
            if (payload is null) return null;
            return new CoachGenerateObjectiveResponse(
                payload.Proposals.Select(p => new CoachObjectiveProposal(
                    p.Title, p.Rationale, p.ReplacesObjectiveId, p.Confidence)).ToList(),
                payload.Model,
                payload.Provider,
                payload.LatencyMs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "generate-objective failed");
            return null;
        }
    }

    private static CoachChatMessage ToChatMessage(ChatMessagePayload m) =>
        new(m.Id, m.ThreadId, m.Role, m.Content, m.Model, m.Provider, m.LatencyMs, m.CreatedAt);

    private static CoachScope? ScopeFromDict(Dictionary<string, JsonElement>? dict)
    {
        if (dict is null) return null;
        long? gameId = dict.TryGetValue("game_id", out var g) && g.TryGetInt64(out var gv) ? gv : null;
        long? since = dict.TryGetValue("since", out var s) && s.TryGetInt64(out var sv) ? sv : null;
        long? until = dict.TryGetValue("until", out var u) && u.TryGetInt64(out var uv) ? uv : null;
        if (gameId is null && since is null && until is null) return null;
        return new CoachScope(gameId, since, until);
    }

    // ───────────────── wire-format records ─────────────────

    private sealed record TestPromptPayload(string Text, string Model, string Provider, int LatencyMs);

    private sealed record ChatMessagePayload(
        long Id, long ThreadId, string Role, string Content,
        string? Model, string? Provider, int? LatencyMs, long CreatedAt);

    private sealed record AskPayload(
        long ThreadId,
        ChatMessagePayload UserMessage,
        ChatMessagePayload AssistantMessage,
        Dictionary<string, int>? CoachVisibleTotals);

    private sealed record ThreadPayload(
        long Id, string? Title, Dictionary<string, JsonElement>? Scope,
        long CreatedAt, long UpdatedAt, List<ChatMessagePayload> Messages);

    private sealed record ThreadSummaryPayload(
        long Id, string? Title, Dictionary<string, JsonElement>? Scope,
        long CreatedAt, long UpdatedAt);

    private sealed record ThreadListPayload(List<ThreadSummaryPayload> Threads);

    private sealed record ObjectiveProposalPayload(
        string Title, string Rationale, long? ReplacesObjectiveId, double Confidence);

    private sealed record GenerateObjectivePayload(
        List<ObjectiveProposalPayload> Proposals, string Model, string Provider, int LatencyMs);

    private sealed record BuildSummaryPayload(long GameId, int SummaryVersion, int? TokenCount, bool Ok, string? Error);

    private sealed record CoachResponsePayload(
        long CoachSessionId,
        string Mode,
        string ResponseText,
        Dictionary<string, object>? ResponseJson,
        string Model,
        string Provider,
        int LatencyMs);
}

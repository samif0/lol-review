#nullable enable

using System.Net.Http;
using System.Net.Http.Json;
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

    // ───────────────── wire-format records ─────────────────

    private sealed record TestPromptPayload(string Text, string Model, string Provider, int LatencyMs);

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

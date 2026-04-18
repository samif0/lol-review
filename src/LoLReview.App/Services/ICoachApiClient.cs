#nullable enable

namespace LoLReview.App.Services;

/// <summary>
/// Typed HTTP client for the coach sidecar. Viewmodels talk to this, not to
/// the process directly.
/// </summary>
public interface ICoachApiClient
{
    Task<bool> HealthAsync(CancellationToken cancellationToken = default);

    Task<CoachTestPromptResponse?> TestPromptAsync(string prompt, CancellationToken cancellationToken = default);

    // Phase 1
    Task<CoachBuildSummaryResponse?> BuildSummaryAsync(long gameId, CancellationToken cancellationToken = default);
    Task<string?> GetSummaryJsonAsync(long gameId, CancellationToken cancellationToken = default);

    // Phase 2
    Task<bool> ExtractConceptsAsync(long gameId, CancellationToken cancellationToken = default);
    Task<bool> ReclusterConceptsAsync(CancellationToken cancellationToken = default);

    // Phase 3
    Task<bool> ComputeFeaturesAsync(long gameId, CancellationToken cancellationToken = default);
    Task<bool> RerankSignalsAsync(CancellationToken cancellationToken = default);

    // Phase 5 — coach modes
    Task<CoachDraftResponse?> DraftPostGameAsync(long gameId, CancellationToken cancellationToken = default);
    Task<CoachDraftResponse?> DraftClipReviewAsync(long bookmarkId, CancellationToken cancellationToken = default);
    Task<CoachDraftResponse?> DraftSessionAsync(long? since, long? until, CancellationToken cancellationToken = default);
    Task<CoachDraftResponse?> DraftWeeklyAsync(long? since, long? until, CancellationToken cancellationToken = default);
    Task<bool> LogEditAsync(long coachSessionId, string editedText, CancellationToken cancellationToken = default);

    // Phase 4 — vision
    Task<bool> DescribeBookmarkAsync(long bookmarkId, CancellationToken cancellationToken = default);

    // Config
    Task<bool> UpdateConfigAsync(CoachConfigUpdate update, CancellationToken cancellationToken = default);
}

public record CoachTestPromptResponse(string Text, string Model, string Provider, int LatencyMs);

public record CoachBuildSummaryResponse(long GameId, int SummaryVersion, int? TokenCount, bool Ok, string? Error);

public record CoachDraftResponse(
    long CoachSessionId,
    string Mode,
    string ResponseText,
    string? ResponseJson,
    string Model,
    string Provider,
    int LatencyMs);

public record CoachConfigUpdate(
    string? Provider = null,
    int? Port = null,
    string? VisionOverrideProvider = null,
    CoachOllamaConfig? Ollama = null,
    CoachHostedConfig? GoogleAi = null,
    CoachHostedConfig? OpenRouter = null);

public record CoachOllamaConfig(string? BaseUrl = null, string? Model = null, string? VisionModel = null);

public record CoachHostedConfig(string? Model = null, string? ApiKey = null);

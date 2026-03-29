#nullable enable

using System.Text.Json;
using LoLReview.Core.Data;

namespace LoLReview.Core.Models;

public static class CoachLabFeature
{
    public const string EnableEnvVar = "LOLREVIEW_ENABLE_COACH_LAB";
    public const string AccessFileName = "coach-lab.access.json";

    private static string? _currentPuuid;
    private static string? _currentSummonerName;

    public static bool IsEnabled()
    {
        if (!IsEnvironmentEnabled())
        {
            return false;
        }

        var access = LoadAccessConfig();
        if (access is null)
        {
            return false;
        }

        if (MatchesAny(access.AllowedWindowsUsers, Environment.UserName))
        {
            return true;
        }

        if (MatchesAny(access.AllowedPuuids, _currentPuuid))
        {
            return true;
        }

        if (MatchesAny(access.AllowedSummonerNames, _currentSummonerName))
        {
            return true;
        }

        return false;
    }

    public static bool IsEnvironmentEnabled()
    {
        return IsTruthy(Environment.GetEnvironmentVariable(EnableEnvVar))
            || IsTruthy(Environment.GetEnvironmentVariable(EnableEnvVar, EnvironmentVariableTarget.User))
            || IsTruthy(Environment.GetEnvironmentVariable(EnableEnvVar, EnvironmentVariableTarget.Machine));
    }

    public static string AccessFilePath => Path.Combine(AppDataPaths.UserDataRoot, AccessFileName);

    public static void UpdateRuntimeIdentity(string? puuid, string? summonerName)
    {
        _currentPuuid = string.IsNullOrWhiteSpace(puuid) ? null : puuid.Trim();
        _currentSummonerName = string.IsNullOrWhiteSpace(summonerName) ? null : summonerName.Trim();
    }

    private static CoachLabAccessConfig? LoadAccessConfig()
    {
        try
        {
            if (!File.Exists(AccessFilePath))
            {
                return null;
            }

            var json = File.ReadAllText(AccessFilePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonSerializer.Deserialize<CoachLabAccessConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    private static bool MatchesAny(IReadOnlyList<string>? values, string? candidate)
    {
        if (values is null || values.Count == 0 || string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        return values.Any(value => string.Equals(
            value?.Trim(),
            candidate.Trim(),
            StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTruthy(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return raw.Equals("1", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("true", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("on", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class CoachLabAccessConfig
{
    public List<string> AllowedWindowsUsers { get; set; } = [];
    public List<string> AllowedSummonerNames { get; set; } = [];
    public List<string> AllowedPuuids { get; set; } = [];
}

public static class CoachObjectiveCatalog
{
    public static IReadOnlyList<CoachObjectiveCatalogItem> Items { get; } =
    [
        new(
            "favorable_trade_windows",
            "Favorable Trade Windows",
            "Choose trades when the wave state and lane setup are actually favorable.",
            ["trade", "wave", "minion", "all in", "fight", "contest", "commit"]),
        new(
            "respect_jungle_support_threat",
            "Respect Jungle / Support Threat",
            "Play with jungle and engage support threat in mind before stepping up.",
            ["jungle", "jungler", "support", "gank", "leona", "nautilus", "thresh", "engage", "position in mind"]),
        new(
            "safe_lane_spacing",
            "Safe Lane Spacing",
            "Maintain spacing that keeps pressure without exposing yourself to free damage or engage.",
            ["spacing", "range", "position", "too far", "step up", "overextend", "caught", "distance"]),
        new(
            "recall_on_crash_and_tempo",
            "Recall On Crash And Tempo",
            "Base on useful wave states instead of bleeding tempo.",
            ["recall", "base", "reset", "tempo", "crash", "plate", "stay"]),
        new(
            "punish_enemy_cooldown_windows",
            "Punish Enemy Cooldown Windows",
            "Recognize and use windows created by enemy cooldowns or summoner spell usage.",
            ["cooldown", "window", "spell", "flash", "ult", "cd", "punish"])
    ];

    public static CoachObjectiveCatalogItem? Find(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return Items.FirstOrDefault(item =>
            string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    public static string NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        return value.Trim().ToLowerInvariant();
    }
}

public static class CoachDraftHeuristics
{
    private static readonly string[] NegativeHints =
    [
        "didn't", "didnt", "bad", "mistake", "missed", "too focused", "late", "wrong",
        "shouldn't", "shouldnt", "died", "death", "caught", "overextend", "overextended",
        "misposition", "misplayed", "failed", "int", "threw"
    ];

    private static readonly string[] PositiveHints =
    [
        "good", "nice", "well", "clean", "great", "correct", "disciplined", "smart",
        "patient", "good spacing", "good trade", "played well", "strong"
    ];

    public static string InferObjectiveKey(params string?[] values)
    {
        var combined = string.Join(' ', values.Where(value => !string.IsNullOrWhiteSpace(value)))
            .Trim()
            .ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(combined))
        {
            return "";
        }

        foreach (var item in CoachObjectiveCatalog.Items)
        {
            if (combined.Contains(item.Key, StringComparison.OrdinalIgnoreCase)
                || combined.Contains(item.Title, StringComparison.OrdinalIgnoreCase))
            {
                return item.Key;
            }

            if (item.Keywords.Any(keyword => combined.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            {
                return item.Key;
            }
        }

        return "";
    }

    public static string InferMomentQuality(params string?[] values)
    {
        var combined = string.Join(' ', values.Where(value => !string.IsNullOrWhiteSpace(value)))
            .Trim()
            .ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(combined))
        {
            return "neutral";
        }

        if (NegativeHints.Any(hint => combined.Contains(hint, StringComparison.OrdinalIgnoreCase)))
        {
            return "bad";
        }

        if (PositiveHints.Any(hint => combined.Contains(hint, StringComparison.OrdinalIgnoreCase)))
        {
            return "good";
        }

        return "neutral";
    }
}

public sealed record CoachObjectiveCatalogItem(
    string Key,
    string Title,
    string Summary,
    IReadOnlyList<string> Keywords);

public sealed class CoachPlayer
{
    public long Id { get; set; }
    public string DisplayName { get; set; } = "Primary Player";
    public bool IsPrimary { get; set; } = true;
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}

public sealed class CoachObjectiveBlock
{
    public long Id { get; set; }
    public long PlayerId { get; set; }
    public long? ObjectiveId { get; set; }
    public string ObjectiveTitle { get; set; } = "";
    public string ObjectiveKey { get; set; } = "";
    public string Status { get; set; } = "active";
    public string Mode { get; set; } = "assist";
    public long StartedAt { get; set; }
    public long UpdatedAt { get; set; }
    public long? CompletedAt { get; set; }
    public string Notes { get; set; } = "";
}

public sealed class CoachMomentSample
{
    public long Id { get; set; }
    public long PlayerId { get; set; }
    public long GameId { get; set; }
    public long? BookmarkId { get; set; }
    public long? ObjectiveBlockId { get; set; }
    public string SourceType { get; set; } = "manual_clip";
    public string PatchVersion { get; set; } = "unknown";
    public string Champion { get; set; } = "";
    public string Role { get; set; } = "";
    public int GameTimeS { get; set; }
    public int? ClipStartS { get; set; }
    public int? ClipEndS { get; set; }
    public string ClipPath { get; set; } = "";
    public string StoryboardPath { get; set; } = "";
    public string HudStripPath { get; set; } = "";
    public string MinimapStripPath { get; set; } = "";
    public string ManifestPath { get; set; } = "";
    public string NoteText { get; set; } = "";
    public string ContextText { get; set; } = "";
    public string DatasetVersion { get; set; } = "bootstrap-v1";
    public string ModelVersion { get; set; } = "assist-heuristic-v1";
    public long CreatedAt { get; set; }
    public long? ReviewedAt { get; set; }
}

public sealed class CoachMomentInference
{
    public long Id { get; set; }
    public long MomentId { get; set; }
    public long PlayerId { get; set; }
    public string ModelVersion { get; set; } = "";
    public string InferenceMode { get; set; } = "assist";
    public string MomentQuality { get; set; } = "neutral";
    public string PrimaryReason { get; set; } = "";
    public string ObjectiveKey { get; set; } = "";
    public long? AttachedObjectiveId { get; set; }
    public string AttachedObjectiveTitle { get; set; } = "";
    public double Confidence { get; set; }
    public string Rationale { get; set; } = "";
    public string RawPayload { get; set; } = "{}";
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}

public sealed class CoachMomentLabel
{
    public long Id { get; set; }
    public long MomentId { get; set; }
    public long PlayerId { get; set; }
    public string LabelQuality { get; set; } = "neutral";
    public string PrimaryReason { get; set; } = "";
    public string ObjectiveKey { get; set; } = "";
    public long? AttachedObjectiveId { get; set; }
    public string AttachedObjectiveTitle { get; set; } = "";
    public string Explanation { get; set; } = "";
    public double Confidence { get; set; }
    public string Source { get; set; } = "manual";
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}

public sealed class CoachRecommendation
{
    public long Id { get; set; }
    public long ObjectiveBlockId { get; set; }
    public long PlayerId { get; set; }
    public string RecommendationType { get; set; } = "keep";
    public string State { get; set; } = "draft";
    public string ObjectiveKey { get; set; } = "";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public double Confidence { get; set; }
    public int EvidenceGameCount { get; set; }
    public string RawPayload { get; set; } = "{}";
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}

public sealed class CoachModelVersion
{
    public long Id { get; set; }
    public string ModelVersion { get; set; } = "";
    public string ModelKind { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Provider { get; set; } = "";
    public bool IsActive { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public long CreatedAt { get; set; }
}

public sealed class CoachDatasetVersion
{
    public long Id { get; set; }
    public string DatasetVersion { get; set; } = "bootstrap-v1";
    public string Status { get; set; } = "active";
    public int GoldCount { get; set; }
    public int SilverCount { get; set; }
    public int BronzeCount { get; set; }
    public int ReviewedGames { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}

public sealed class CoachDraftRequest
{
    public string NoteText { get; set; } = "";
    public string ReviewContext { get; set; } = "";
    public string ActiveObjectiveTitle { get; set; } = "";
    public string Champion { get; set; } = "";
    public string Role { get; set; } = "";
    public string SourceType { get; set; } = "manual_clip";
    public int GameTimeS { get; set; }
    public string StoryboardPath { get; set; } = "";
    public string MinimapStripPath { get; set; } = "";
}

public sealed class CoachDraftResult
{
    public string ModelVersion { get; set; } = "assist-heuristic-v1";
    public string InferenceMode { get; set; } = "assist";
    public string MomentQuality { get; set; } = "neutral";
    public string PrimaryReason { get; set; } = "";
    public string ObjectiveKey { get; set; } = "";
    public double Confidence { get; set; }
    public string Rationale { get; set; } = "";
    public string RawPayload { get; set; } = "{}";
}

public sealed class CoachDashboardSnapshot
{
    public bool IsEnabled { get; set; }
    public bool IsAssistMode { get; set; } = true;
    public string ActiveObjectiveTitle { get; set; } = "Observe lane phase";
    public string ActiveObjectiveKey { get; set; } = "";
    public string RecommendationTitle { get; set; } = "Assist mode active";
    public string RecommendationSummary { get; set; } = "Collect clip-backed evidence before promoting stronger coaching behavior.";
    public string WatchItemTitle { get; set; } = "Clip-backed evidence";
    public string WatchItemSummary { get; set; } = "Keep saving lane clips with notes so the model can learn from exact moments.";
    public int TotalMoments { get; set; }
    public int GoldMoments { get; set; }
    public int SilverMoments { get; set; }
    public int BronzeMoments { get; set; }
    public int PendingMoments { get; set; }
    public int ReviewedGames { get; set; }
    public string DatasetVersion { get; set; } = "bootstrap-v1";
    public string ActiveModelVersion { get; set; } = "assist-heuristic-v1";
    public CoachTrainingStatus TrainingStatus { get; set; } = new();
}

public sealed class CoachMomentCard
{
    public long Id { get; set; }
    public long GameId { get; set; }
    public long? BookmarkId { get; set; }
    public string SourceType { get; set; } = "manual_clip";
    public string Champion { get; set; } = "";
    public string Role { get; set; } = "";
    public int GameTimeS { get; set; }
    public string ClipPath { get; set; } = "";
    public string StoryboardPath { get; set; } = "";
    public string HudStripPath { get; set; } = "";
    public string MinimapStripPath { get; set; } = "";
    public string ManifestPath { get; set; } = "";
    public string NoteText { get; set; } = "";
    public string ContextText { get; set; } = "";
    public string DraftQuality { get; set; } = "neutral";
    public string DraftPrimaryReason { get; set; } = "";
    public string DraftObjectiveKey { get; set; } = "";
    public long? DraftAttachedObjectiveId { get; set; }
    public string DraftAttachedObjectiveTitle { get; set; } = "";
    public double DraftConfidence { get; set; }
    public string DraftRationale { get; set; } = "";
    public string LabelQuality { get; set; } = "";
    public string LabelPrimaryReason { get; set; } = "";
    public string LabelObjectiveKey { get; set; } = "";
    public long? LabelAttachedObjectiveId { get; set; }
    public string LabelAttachedObjectiveTitle { get; set; } = "";
    public long? BlockObjectiveId { get; set; }
    public string BlockObjectiveTitle { get; set; } = "";
    public string LabelExplanation { get; set; } = "";
    public double LabelConfidence { get; set; }
    public long CreatedAt { get; set; }
    public long? ReviewedAt { get; set; }

    public bool HasManualLabel => !string.IsNullOrWhiteSpace(LabelQuality);

    public string TimeText => $"{GameTimeS / 60}:{GameTimeS % 60:D2}";
}

public sealed class CoachManualLabelInput
{
    public string LabelQuality { get; set; } = "neutral";
    public string PrimaryReason { get; set; } = "";
    public string ObjectiveKey { get; set; } = "";
    public long? AttachedObjectiveId { get; set; }
    public string Explanation { get; set; } = "";
    public double Confidence { get; set; } = 0.7;
}

public sealed class CoachSyncResult
{
    public int ManualClipsImported { get; set; }
    public int AutoSamplesCreated { get; set; }
    public int DraftsCreated { get; set; }
}

public sealed class CoachTrainingStatus
{
    public string Mode { get; set; } = "assist";
    public string ActiveModelVersion { get; set; } = "assist-heuristic-v1";
    public string ActiveTeacherVersion { get; set; } = "";
    public string ActiveBaseJudgeVersion { get; set; } = "";
    public string ActivePersonalAdapterVersion { get; set; } = "";
    public bool IsTrainingInProgress { get; set; }
    public string ActiveTrainingKind { get; set; } = "";
    public string ActiveTrainingStatusText { get; set; } = "";
    public long? ActiveTrainingStartedAt { get; set; }
    public string LastTrainingSummary { get; set; } = "";
    public long? LastTrainingCompletedAt { get; set; }
    public bool LastTrainingSucceeded { get; set; }
    public int GoldMoments { get; set; }
    public int AcceptedMoments { get; set; }
    public int ReviewedGames { get; set; }
    public int PrematureTrainingExamples { get; set; }
    public bool CanTrainPrematurePrototype { get; set; }
    public bool CanTrainFirstAdapter { get; set; }
    public bool CanTrainBaseModel { get; set; }
    public bool HasPrematurePrototype { get; set; }
    public bool HasTeacherModel { get; set; }
    public bool HasBaseJudge { get; set; }
    public bool HasPersonalAdapter { get; set; }
    public string Summary =>
        IsTrainingInProgress
            ? (!string.IsNullOrWhiteSpace(ActiveTrainingStatusText)
                ? ActiveTrainingStatusText
                : "Coach model training is running in the background.")
            : !string.IsNullOrWhiteSpace(LastTrainingSummary)
                ? LastTrainingSummary
                : HasPersonalAdapter
            ? "A personalized coach model is active for draft scoring."
            : HasBaseJudge
                ? "A stronger base judge is active for clip scoring."
                : HasTeacherModel
                    ? "Teacher-assisted clip drafting is active."
                    : HasPrematurePrototype
                        ? $"A prototype coach is active. It learned from {PrematureTrainingExamples} accepted clip-backed moments, so treat it as exploratory."
                        : CanTrainBaseModel
                            ? "Enough accepted clip-backed moments exist to train the shared base model."
                            : CanTrainFirstAdapter
                                ? "Enough reviewed clip-backed moments exist to train the first personalized coach."
                                : CanTrainPrematurePrototype
                                    ? "Enough clip-backed moments exist to train a prototype now. It will still be weak and exploratory."
                                    : $"Collect {Math.Max(0, 2 - AcceptedMoments)} more accepted clip-backed moments for a prototype, or {Math.Max(0, 250 - GoldMoments)} reviewed moments and {Math.Max(0, 50 - ReviewedGames)} reviewed games with clips for the first trained coach.";
}

public sealed class CoachTrainResult
{
    public bool Success { get; set; }
    public string ModelVersion { get; set; } = "";
    public string Summary { get; set; } = "";
    public bool StartedInBackground { get; set; }
    public bool AlreadyRunning { get; set; }
    public int TrainingExamples { get; set; }
    public int GoldExamples { get; set; }
    public int SilverExamples { get; set; }
    public int RescoredMoments { get; set; }
    public string ModelDirectory { get; set; } = "";
}

public sealed class CoachProblemInsight
{
    public string ReasonKey { get; set; } = "";
    public string Title { get; set; } = "";
    public int MomentCount { get; set; }
    public int GameCount { get; set; }
    public double Confidence { get; set; }
    public string ExampleNote { get; set; } = "";
}

public sealed class CoachProblemsReport
{
    public string Title { get; set; } = "Model problems";
    public string Summary { get; set; } = "";
    public string ModelVersion { get; set; } = "";
    public bool UsesTrainedModel { get; set; }
    public List<CoachProblemInsight> Problems { get; set; } = [];
}

public sealed class CoachObjectiveSuggestion
{
    public string Title { get; set; } = "Suggested objective";
    public string Summary { get; set; } = "";
    public string ModelVersion { get; set; } = "";
    public bool UsesTrainedModel { get; set; }
    public string SuggestionMode { get; set; } = "";
    public long? AttachedObjectiveId { get; set; }
    public string AttachedObjectiveTitle { get; set; } = "";
    public string ObjectiveKey { get; set; } = "";
    public string CandidateObjectiveTitle { get; set; } = "";
    public string CandidateCompletionCriteria { get; set; } = "";
    public string CandidateDescription { get; set; } = "";
    public int EvidenceMomentCount { get; set; }
    public int EvidenceGameCount { get; set; }
    public double Confidence { get; set; }

    public bool CanCreateObjective =>
        string.Equals(SuggestionMode, "create_new", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(CandidateObjectiveTitle);
}

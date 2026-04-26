using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Revu.Core.Tests;

internal sealed class TestDatabaseScope : IDisposable
{
    private readonly string _rootDirectory;

    public TestDatabaseScope()
    {
        _rootDirectory = Path.Combine(Path.GetTempPath(), "Revu.Core.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDirectory);
        DatabasePath = Path.Combine(_rootDirectory, "test.db");

        ConnectionFactory = new SqliteConnectionFactory(NullLogger<SqliteConnectionFactory>.Instance, DatabasePath);
        Initializer = new DatabaseInitializer(ConnectionFactory, NullLogger<DatabaseInitializer>.Instance);

        Games = new GameRepository(ConnectionFactory, new NoopBackupService());
        GameEvents = new GameEventsRepository(ConnectionFactory);
        DerivedEvents = new DerivedEventsRepository(ConnectionFactory);
        Objectives = new ObjectivesRepository(ConnectionFactory);
        Prompts = new PromptsRepository(ConnectionFactory);
        MatchupNotes = new MatchupNotesRepository(ConnectionFactory);
        Vod = new VodRepository(ConnectionFactory);
        SessionLog = new SessionLogRepository(ConnectionFactory);
        ConceptTags = new ConceptTagRepository(ConnectionFactory);
        ReviewDrafts = new ReviewDraftRepository(ConnectionFactory);
        MissedGameDecisions = new MissedGameDecisionRepository(ConnectionFactory);
    }

    public string DatabasePath { get; }

    public IDbConnectionFactory ConnectionFactory { get; }

    public DatabaseInitializer Initializer { get; }

    public GameRepository Games { get; }

    public GameEventsRepository GameEvents { get; }

    public DerivedEventsRepository DerivedEvents { get; }

    public ObjectivesRepository Objectives { get; }

    public PromptsRepository Prompts { get; }

    public MatchupNotesRepository MatchupNotes { get; }

    public VodRepository Vod { get; }

    public SessionLogRepository SessionLog { get; }

    public ConceptTagRepository ConceptTags { get; }

    public ReviewDraftRepository ReviewDrafts { get; }

    public MissedGameDecisionRepository MissedGameDecisions { get; }

    public Task InitializeAsync() => Initializer.InitializeAsync();

    public SqliteConnection OpenConnection() => ConnectionFactory.CreateConnection();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
        catch
        {
        }
    }
}

internal sealed class TestConfigService : IConfigService
{
    public TestConfigService(AppConfig? config = null)
    {
        Current = config ?? new AppConfig();
    }

    public AppConfig Current { get; private set; }

    public string GithubToken => Current.GithubToken;

    public string? AscentFolder => string.IsNullOrWhiteSpace(Current.AscentFolder) ? null : Current.AscentFolder;

    public bool TiltFixEnabled => Current.TiltFixMode;

    public string ClipsFolder => Current.ClipsFolder;

    public int ClipsMaxSizeMb => Current.ClipsMaxSizeMb;

    public bool BackupEnabled => Current.BackupEnabled;

    public string BackupFolder => Current.BackupFolder;

    public Dictionary<string, string> Keybinds => GetKeybinds();

    public string RiotSessionToken => Current.RiotSessionToken;
    public string RiotSessionEmail => Current.RiotSessionEmail;
    public long RiotSessionExpiresAt => Current.RiotSessionExpiresAt;
    public string RiotId => Current.RiotId;
    public string RiotRegion => Current.RiotRegion;
    public string RiotPuuid => Current.RiotPuuid;
    public string PrimaryRole => Current.PrimaryRole;
    public bool OnboardingSkipped => Current.OnboardingSkipped;
    public bool AscentReminderDismissed => Current.AscentReminderDismissed;
    public bool SidebarAnimationEnabled => Current.SidebarAnimationEnabled;
    public bool MinimizeDuringGame => Current.MinimizeDuringGame;

    public bool IsAscentEnabled => !string.IsNullOrWhiteSpace(AscentFolder);

    public bool HasValidRiotSession =>
        !string.IsNullOrWhiteSpace(RiotSessionToken)
        && RiotSessionExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public bool RiotProxyEnabled =>
        HasValidRiotSession
        && !string.IsNullOrWhiteSpace(RiotId)
        && RiotId.Contains('#')
        && !string.IsNullOrWhiteSpace(RiotRegion);

    public bool OnboardingComplete =>
        OnboardingSkipped
        || (RiotProxyEnabled && !string.IsNullOrEmpty(PrimaryRole));

    public Task<AppConfig> LoadAsync() => Task.FromResult(Current);

    public Task SaveAsync(AppConfig config)
    {
        Current = config;
        return Task.CompletedTask;
    }

    public Dictionary<string, string> GetKeybinds()
    {
        var merged = new Dictionary<string, string>(AppConfig.DefaultKeybinds);
        foreach (var pair in Current.Keybinds)
        {
            merged[pair.Key] = pair.Value;
        }

        return merged;
    }
}

internal sealed class StubVodService : IVodService
{
    public bool TryLinkResult { get; set; }

    public Task<List<VodRecordingInfo>> FindRecordingsAsync(string? folder = null) =>
        Task.FromResult<List<VodRecordingInfo>>([]);

    public string? MatchRecordingToGame(GameStats game, IReadOnlyList<VodRecordingInfo> recordings,
        IReadOnlySet<string>? excludePaths = null) => null;

    public Task<bool> TryLinkRecordingAsync(GameStats game, string? folder = null) => Task.FromResult(TryLinkResult);

    public Task<int> AutoMatchRecordingsAsync() => Task.FromResult(0);
}

internal static class TestGameStatsFactory
{
    public static GameStats Create(
        long gameId,
        string champion = "Ahri",
        bool win = true,
        long? timestamp = null,
        string gameMode = "Ranked Solo",
        int durationSeconds = 1800)
    {
        return new GameStats
        {
            GameId = gameId,
            Timestamp = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            GameDuration = durationSeconds,
            GameMode = gameMode,
            GameType = "MATCHED_GAME",
            QueueType = "Ranked Solo/Duo",
            SummonerName = "Tester",
            ChampionName = champion,
            ChampionId = 103,
            TeamId = 100,
            Position = "MIDDLE",
            Win = win,
            Kills = 8,
            Deaths = 3,
            Assists = 6,
            KdaRatio = 4.67,
            CsTotal = 210,
            CsPerMin = 7.0,
            VisionScore = 24,
            TeamKills = 20,
            KillParticipation = 70,
        };
    }
}

/// <summary>
/// Test double for IBackupService — backups are disk-touching side effects
/// the repo layer doesn't need to exercise during unit tests.
/// </summary>
internal sealed class NoopBackupService : IBackupService
{
    public Task CreateSafetyBackupAsync(string reason) => Task.CompletedTask;
    public Task RunBackupAsync() => Task.CompletedTask;
    public Task<IReadOnlyList<BackupFileInfo>> ListBackupsAsync() =>
        Task.FromResult<IReadOnlyList<BackupFileInfo>>(Array.Empty<BackupFileInfo>());
    public Task<ResetResult> ResetAllDataAsync() =>
        Task.FromResult(new ResetResult(true, "", null));
    public Task<RestoreResult> RestoreFromBackupAsync(string backupFilePath) =>
        Task.FromResult(new RestoreResult(true, null, null));
}

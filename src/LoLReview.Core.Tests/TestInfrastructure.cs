using LoLReview.Core.Data;
using LoLReview.Core.Data.Repositories;
using LoLReview.Core.Models;
using LoLReview.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LoLReview.Core.Tests;

internal sealed class TestDatabaseScope : IDisposable
{
    private readonly string _rootDirectory;

    public TestDatabaseScope()
    {
        _rootDirectory = Path.Combine(Path.GetTempPath(), "LoLReview.Core.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDirectory);
        DatabasePath = Path.Combine(_rootDirectory, "test.db");

        ConnectionFactory = new SqliteConnectionFactory(NullLogger<SqliteConnectionFactory>.Instance, DatabasePath);
        Initializer = new DatabaseInitializer(ConnectionFactory, NullLogger<DatabaseInitializer>.Instance);

        Games = new GameRepository(ConnectionFactory);
        GameEvents = new GameEventsRepository(ConnectionFactory);
        DerivedEvents = new DerivedEventsRepository(ConnectionFactory);
        Objectives = new ObjectivesRepository(ConnectionFactory);
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

    public bool IsAscentEnabled => !string.IsNullOrWhiteSpace(AscentFolder);

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

    public string? MatchRecordingToGame(GameStats game, IReadOnlyList<VodRecordingInfo> recordings) => null;

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
            QueueType = "420",
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

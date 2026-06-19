using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Revu.Sidecar.Tests;

/// <summary>
/// Integration-test harness for the sidecar's WRITE seam.
///
/// The sidecar's write endpoints (save_review, save_config, set_*_objective,
/// start_block/end_block, focus adherence, death class, evidence triage) all
/// delegate to Revu.Core write services/repositories. This scope builds the
/// exact same object graph against a throwaway temp SQLite DB, so contract
/// tests exercise the real persistence code the endpoints run — without the
/// WinExe host, Kestrel, or Velopack.
///
/// Mirrors Revu.Core.Tests/TestInfrastructure.cs (TestDatabaseScope). We do NOT
/// reference Revu.Sidecar (Microsoft.NET.Sdk.Web + WinExe + RID = awkward to
/// reference); only Revu.Core. The test doubles below (config / vod / backup /
/// game factory) are copied here because their TestDatabaseScope equivalents are
/// `internal` to the Revu.Core.Tests assembly.
/// </summary>
public sealed class SidecarWriteScope : IDisposable
{
    private readonly string _rootDirectory;

    public SidecarWriteScope()
    {
        _rootDirectory = Path.Combine(Path.GetTempPath(), "Revu.Sidecar.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDirectory);
        DatabasePath = Path.Combine(_rootDirectory, "revu.db");

        ConnectionFactory = new SqliteConnectionFactory(NullLogger<SqliteConnectionFactory>.Instance, DatabasePath);
        Initializer = new DatabaseInitializer(ConnectionFactory, NullLogger<DatabaseInitializer>.Instance);

        // ── Repositories the sidecar write endpoints hit ────────────────────
        Games = new GameRepository(ConnectionFactory, new NoopBackupService());
        SessionLog = new SessionLogRepository(ConnectionFactory);
        Objectives = new ObjectivesRepository(ConnectionFactory);
        DeathClassifications = new DeathClassificationsRepository(ConnectionFactory);
        Evidence = new EvidenceRepository(ConnectionFactory);
        Prompts = new PromptsRepository(ConnectionFactory);
        ReviewDrafts = new ReviewDraftRepository(ConnectionFactory);
        ConceptTags = new ConceptTagRepository(ConnectionFactory);
        MatchupNotes = new MatchupNotesRepository(ConnectionFactory);
        Vod = new VodRepository(ConnectionFactory);

        // ── Services the save_review endpoint composes ──────────────────────
        Config = new TestConfigService();
        VodService = new VodService(
            Games,
            Vod,
            Config,
            NullLogger<VodService>.Instance);
        ClipService = new ClipService(Config, NullLogger<ClipService>.Instance);
        CoachNotifier = new NullCoachSidecarNotifier();

        // ReviewWorkflowService.SaveAsync is the contract-critical write path
        // (null-passthrough / no-clobber semantics live here). Wired with its
        // real dependency list — see ReviewWorkflowService's constructor.
        ReviewWorkflow = new ReviewWorkflowService(
            Games,
            ConceptTags,
            Vod,
            VodService,
            SessionLog,
            Objectives,
            ReviewDrafts,
            MatchupNotes,
            Evidence,
            Config,
            CoachNotifier,
            NullLogger<ReviewWorkflowService>.Instance);
    }

    public string DatabasePath { get; }

    public IDbConnectionFactory ConnectionFactory { get; }

    public DatabaseInitializer Initializer { get; }

    // ── Repositories ────────────────────────────────────────────────────────
    public GameRepository Games { get; }

    public SessionLogRepository SessionLog { get; }

    public ObjectivesRepository Objectives { get; }

    public DeathClassificationsRepository DeathClassifications { get; }

    public EvidenceRepository Evidence { get; }

    public PromptsRepository Prompts { get; }

    public ReviewDraftRepository ReviewDrafts { get; }

    public ConceptTagRepository ConceptTags { get; }

    public MatchupNotesRepository MatchupNotes { get; }

    public VodRepository Vod { get; }

    // ── Services ──────────────────────────────────────────────────────────────
    public TestConfigService Config { get; }

    public VodService VodService { get; }

    public ClipService ClipService { get; }

    public NullCoachSidecarNotifier CoachNotifier { get; }

    public ReviewWorkflowService ReviewWorkflow { get; }

    /// <summary>Create the schema (runs the full migration set, mirroring startup).</summary>
    public Task InitializeAsync() => Initializer.InitializeAsync();

    public SqliteConnection OpenConnection() => ConnectionFactory.CreateConnection();

    /// <summary>
    /// Insert a seed game row so tests have a game to attach a review/objective/
    /// death/evidence to. Mirrors how the Core tests persist a GameStats via
    /// GameRepository.SaveAsync. Returns the persisted GameStats.
    /// </summary>
    public async Task<GameStats> SeedGameAsync(
        long gameId = 1001,
        string champion = "Ahri",
        bool win = true,
        long? timestamp = null,
        string gameMode = "Ranked Solo",
        int durationSeconds = 1800)
    {
        var game = TestGameStatsFactory.Create(gameId, champion, win, timestamp, gameMode, durationSeconds);
        await Games.SaveAsync(game);
        return game;
    }

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

/// <summary>
/// In-memory IConfigService for tests — mirrors Revu.Core.Tests's TestConfigService
/// (internal there, so re-declared here). SaveAsync swaps the in-memory config,
/// which is the exact behavior the save_config endpoint's Core seam exercises.
/// </summary>
public sealed class TestConfigService : IConfigService
{
    public TestConfigService(AppConfig? config = null)
    {
        Current = config ?? new AppConfig();
    }

    public AppConfig Current { get; private set; }

    public string GithubToken => Current.GithubToken;

    public string? AscentFolder => string.IsNullOrWhiteSpace(Current.AscentFolder) ? null : Current.AscentFolder;

    public string AscentFolderRaw => Current.AscentFolder;

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
    public bool AutoTimelineClippingEnabled => Current.AutoTimelineClippingEnabled;
    public bool AutoTimelineClippingHintDismissed => Current.AutoTimelineClippingHintDismissed;

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

/// <summary>
/// No-op IBackupService — backups are disk-touching side effects the repo layer
/// doesn't need to exercise during contract tests. Copied from
/// Revu.Core.Tests/TestInfrastructure.cs (internal there).
/// </summary>
public sealed class NoopBackupService : IBackupService
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

/// <summary>
/// Builds a fully-populated GameStats for seeding. Copied from
/// Revu.Core.Tests/TestInfrastructure.cs (internal there).
/// </summary>
public static class TestGameStatsFactory
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

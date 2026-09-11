#nullable enable

namespace Revu.Sidecar;

/// <summary>
/// Shared, thread-safe snapshot of the live champ-select / in-game context the
/// sidecar's hosted <c>GameMonitorService</c> publishes over IMessenger.
///
/// <para>
/// Three consumers read this:
///   • the SSE channel (GET /api/events) — streams every raw LCU message; this
///     state is the "current value" a late SSE subscriber is replayed on connect.
///   • GET /api/pregame — uses the active <see cref="SessionKey"/> to prefill the
///     champ-select prompt-answer drafts, and the live champ/enemy/role to seed
///     the matchup card before the first SSE tick lands.
///   • the EOG write path — the <see cref="SidecarGameFlowCoordinator"/> stamps
///     the deferred pre-game snapshots here as they change (mood / intent /
///     practiced ids) and reads them back at game end, exactly like the WinUI
///     PreGameDialogViewModel statics + ShellViewModel.
/// </para>
///
/// <para>
/// The session key scopes <c>pre_game_draft_prompts</c> to one champ-select →
/// game flow. The coordinator mints it on ChampSelectStarted and clears it after
/// promoting drafts at game end (mirrors PreGameDialogViewModel.LastSessionKey /
/// ResetSessionKey). A null key means "no live flow" — the read endpoint then
/// shows no prefilled drafts.
/// </para>
/// </summary>
public sealed class LcuLiveState
{
    private readonly object _gate = new();

    private string _myChampion = "";
    private string _enemyChampion = "";
    private string _myPosition = "";
    private string _participantMapJson = "";
    private string? _sessionKey;
    private bool _isGameInProgress;
    private bool _isLcuConnected;

    // ── Deferred pre-game snapshots (written at EOG) — mirror the WinUI
    //    PreGameDialogViewModel statics. The frontend POSTs these as the user
    //    edits; the coordinator reads them when GameEnded fires.
    private int _preGameMood;
    private string _intention = "";
    private string _intentionSource = "";
    private bool _intentCleared;
    private IReadOnlyList<long> _practicedObjectiveIds = Array.Empty<long>();

    // v3.7 (hard stop): the most recent enforcement, replayed to a webview that
    // (re)connects mid-lockout so the banner survives a reload. Cleared when a
    // game actually starts (the player overrode, or the rule stopped holding).
    private HardStopSnapshot? _hardStop;

    public string MyChampion { get { lock (_gate) return _myChampion; } }
    public string EnemyChampion { get { lock (_gate) return _enemyChampion; } }
    public string MyPosition { get { lock (_gate) return _myPosition; } }
    public string ParticipantMapJson { get { lock (_gate) return _participantMapJson; } }
    public string? SessionKey { get { lock (_gate) return _sessionKey; } }
    public bool IsGameInProgress { get { lock (_gate) return _isGameInProgress; } }
    public bool IsLcuConnected { get { lock (_gate) return _isLcuConnected; } }

    public int PreGameMood { get { lock (_gate) return _preGameMood; } }
    public string Intention { get { lock (_gate) return _intention; } }
    public string IntentionSource { get { lock (_gate) return _intentionSource; } }
    public bool IntentCleared { get { lock (_gate) return _intentCleared; } }
    public IReadOnlyList<long> PracticedObjectiveIds { get { lock (_gate) return _practicedObjectiveIds; } }
    public HardStopSnapshot? HardStop { get { lock (_gate) return _hardStop; } }

    /// <summary>Champ select began — reset the champ context and mint a fresh
    /// session key (and clear the prior game's deferred snapshots).</summary>
    public string BeginChampSelect(string myChampion, string enemyChampion, string myPosition, string participantMapJson)
    {
        lock (_gate)
        {
            _myChampion = myChampion ?? "";
            _enemyChampion = enemyChampion ?? "";
            _myPosition = myPosition ?? "";
            _participantMapJson = participantMapJson ?? "";
            // Unconditionally mint (the doc contract): a surviving key here is a
            // stale flow — a game that never reached EOG — and reusing it would
            // prefill and later promote THAT lobby's drafts onto this game.
            _sessionKey = Guid.NewGuid().ToString("N");
            // Fresh flow: clear any leftover deferred snapshots.
            _preGameMood = 0;
            _intention = "";
            _intentionSource = "";
            _intentCleared = false;
            _practicedObjectiveIds = Array.Empty<long>();
            return _sessionKey;
        }
    }

    /// <summary>Live champ-select tick — update the detected champ/enemy/role/map.</summary>
    public void UpdateChampSelect(string myChampion, string enemyChampion, string myPosition, string participantMapJson)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(myChampion)) _myChampion = myChampion;
            _enemyChampion = enemyChampion ?? _enemyChampion;
            if (!string.IsNullOrEmpty(myPosition)) _myPosition = myPosition;
            if (!string.IsNullOrEmpty(participantMapJson)) _participantMapJson = participantMapJson;
        }
    }

    public void SetGameInProgress(bool inProgress)
    {
        lock (_gate) _isGameInProgress = inProgress;
    }

    public void SetHardStop(HardStopSnapshot? snapshot)
    {
        lock (_gate) _hardStop = snapshot;
    }

    public void SetLcuConnected(bool connected)
    {
        lock (_gate) _isLcuConnected = connected;
    }

    /// <summary>Champ select cancelled (dodge / queue expired) — drop the champ
    /// context ONLY. The session key and staged choices deliberately survive:
    /// the "cancel" signal also fires on LCU wobbles where the game still
    /// launches (ChampSelect → FailedToLaunch/None → InProgress), and wiping
    /// here would lose the staged mood/intent/drafts for a game that IS played.
    /// Cross-lobby leakage is closed at the other end — BeginChampSelect
    /// unconditionally mints a fresh key and clears the staged snapshots, so a
    /// genuinely dodged lobby's state can never attach to the next game.</summary>
    public void CancelChampSelect()
    {
        lock (_gate)
        {
            _myChampion = "";
            _enemyChampion = "";
            _myPosition = "";
            _participantMapJson = "";
        }
    }

    // ── Deferred pre-game snapshot writers (POSTed from the frontend) ─────────
    public void SetMood(int mood) { lock (_gate) _preGameMood = mood; }

    public void SetIntent(string intention, string source, bool cleared)
    {
        lock (_gate)
        {
            _intentCleared = cleared;
            _intention = cleared ? "" : (intention ?? "").Trim();
            _intentionSource = string.IsNullOrWhiteSpace(_intention) ? "" : (source ?? "");
        }
    }

    public void SetPracticed(IReadOnlyList<long> ids)
    {
        lock (_gate) _practicedObjectiveIds = ids ?? Array.Empty<long>();
    }

    /// <summary>Read the deferred snapshots + session key for the EOG write, then
    /// clear them so the next game starts clean (mirror ResetPreGameSnapshots +
    /// ResetSessionKey). Returns null sessionKey when no flow was live.</summary>
    public (int Mood, string Intention, string IntentionSource, bool Cleared,
        IReadOnlyList<long> PracticedIds, string? SessionKey,
        string MyPosition, string ParticipantMapJson) TakeForGameEnd()
    {
        lock (_gate)
        {
            var snapshot = (_preGameMood, _intention, _intentionSource, _intentCleared,
                _practicedObjectiveIds, _sessionKey, _myPosition, _participantMapJson);
            // v3.10.1: the champ-select context goes to the game-end matchup fallback
            // and is cleared with the rest, so a lobby can never label a later game
            // whose champ select the sidecar did not see.
            _myChampion = "";
            _enemyChampion = "";
            _myPosition = "";
            _participantMapJson = "";
            _preGameMood = 0;
            _intention = "";
            _intentionSource = "";
            _intentCleared = false;
            _practicedObjectiveIds = Array.Empty<long>();
            _sessionKey = null;
            _isGameInProgress = false;
            return snapshot;
        }
    }
}

/// <summary>
/// v3.7: the SSE <c>hardStop</c> payload — one enforcement of one rule. Also the
/// <c>liveState.hardStop</c> replay value. camelCase on the wire. Everything the
/// lock screen shows comes from here so it never has to fetch the Rules page.
/// </summary>
public sealed record HardStopSnapshot(
    long RuleId,
    string RuleName,
    // The live trip reason, e.g. "Already played 6/6 games today".
    string Reason,
    // The rule's IF leg as the Rules page words it ("Max 6 games per day").
    string ConditionCue,
    // The player's own THEN plan; may be empty.
    string ReplacementPlan,
    bool HasPlan,
    // "cancelled_queue" | "declined_ready_check" (HardStopActions).
    string Action,
    // Unix second the rule stops holding (countdown), null when unknown.
    long? UnlockAt,
    // Unix second of this enforcement.
    long At);

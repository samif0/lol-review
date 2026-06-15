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
            _sessionKey ??= Guid.NewGuid().ToString("N");
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

    public void SetLcuConnected(bool connected)
    {
        lock (_gate) _isLcuConnected = connected;
    }

    /// <summary>Champ select cancelled (dodge / queue expired) — drop the flow.</summary>
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
        IReadOnlyList<long> PracticedIds, string? SessionKey) TakeForGameEnd()
    {
        lock (_gate)
        {
            var snapshot = (_preGameMood, _intention, _intentionSource, _intentCleared,
                _practicedObjectiveIds, _sessionKey);
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

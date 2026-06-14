#nullable enable

using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Revu.App.Contracts;
using Revu.App.Helpers;
using Revu.App.Styling;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;
using Microsoft.Extensions.Logging;

namespace Revu.App.ViewModels;

/// <summary>
/// Pattern Review viewer — walks the cross-game moments that compose one
/// dashboard pattern as a single playlist. The page drives a shared VodSurface
/// imperatively off <see cref="CurrentMoment"/>; advancing past a moment in a
/// different game transparently loads that game's VOD. Reviewing the pattern
/// records it (dashboard "Patterns Reviewed" stat) and drops it from the nag.
/// </summary>
public partial class PatternReviewViewModel : ObservableObject
{
    private readonly IEvidenceRepository _evidenceRepo;
    private readonly IVodRepository _vodRepo;
    private readonly IClipService _clipService;
    private readonly IConfigService _configService;
    private readonly INavigationService _navigationService;
    private readonly IObjectivesRepository _objectivesRepo;
    private readonly ILogger<PatternReviewViewModel> _logger;

    private ObjectivePatternCard? _pattern;

    public PatternReviewViewModel(
        IEvidenceRepository evidenceRepo,
        IVodRepository vodRepo,
        IClipService clipService,
        IConfigService configService,
        INavigationService navigationService,
        IObjectivesRepository objectivesRepo,
        ILogger<PatternReviewViewModel> logger)
    {
        _evidenceRepo = evidenceRepo;
        _vodRepo = vodRepo;
        _clipService = clipService;
        _configService = configService;
        _navigationService = navigationService;
        _objectivesRepo = objectivesRepo;
        _logger = logger;
    }

    public ObservableCollection<PatternMomentItem> Moments { get; } = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClosureSummary))]
    private string _patternTitle = "";

    [ObservableProperty]
    private string _patternSubtitle = "";

    [ObservableProperty]
    private string _severityLabel = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeverityBrush))]
    private string _severityHex = AppSemanticPalette.AccentGoldHex;

    public Microsoft.UI.Xaml.Media.SolidColorBrush SeverityBrush => AppSemanticPalette.Brush(SeverityHex);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMoments))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyPropertyChangedFor(nameof(NotesCount))]
    [NotifyPropertyChangedFor(nameof(ClosureSummary))]
    private int _momentCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CounterText))]
    [NotifyPropertyChangedFor(nameof(CanGoPrev))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    private int _currentIndex = -1;

    [ObservableProperty]
    private PatternMomentItem? _currentMoment;

    /// <summary>Raised when the active moment changes — the page loads/seeks the surface.</summary>
    public event Action<PatternMomentItem>? MomentActivated;

    [ObservableProperty]
    private bool _isReviewed;

    [ObservableProperty]
    private string _carryForwardNote = "";

    // ── "This moment" work panel state ──────────────────────────────────────
    //
    // A pattern moment is already a clip-sized window of the VOD, so the user
    // doesn't manage clip ranges or bookmarks here — they just write a note and
    // Save. On save we silently extract a clip over the moment's window (padded)
    // and attach it as evidence, once per moment.

    /// <summary>The active moment's note, editable in the work panel. Autosaves.</summary>
    [ObservableProperty]
    private string _momentNote = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMomentStatus))]
    private string _momentStatus = "";

    public bool HasMomentStatus => !string.IsNullOrEmpty(MomentStatus);

    // Autosave: a short pause after typing flushes the note (and clips the
    // moment once). Set while we load a moment's note into the box so that
    // programmatic change doesn't trigger a save/clip.
    private Microsoft.UI.Xaml.DispatcherTimer? _noteDebounce;
    private bool _suppressNoteSave;
    private static readonly TimeSpan NoteDebounce = TimeSpan.FromMilliseconds(900);

    // Single-point moments (start==end, e.g. a death) get this much context
    // before / after so the auto-clip is always watchable.
    private const int ClipLeadSeconds = 8;
    private const int ClipTrailSeconds = 4;

    // How close an existing clip's start must be to a moment's start to count
    // as "already clipped" (covers the lead padding so we don't double-clip).
    private const int ClipMatchSlack = 10;

    private static string FormatTime(int s) => $"{s / 60}:{s % 60:D2}";

    public bool HasMoments => MomentCount > 0;
    public bool IsEmpty => !IsLoading && MomentCount == 0;
    public bool CanGoPrev => CurrentIndex > 0;
    public bool CanGoNext => CurrentIndex >= 0 && CurrentIndex < MomentCount - 1;
    public string CounterText => MomentCount == 0 ? "" : $"MOMENT {CurrentIndex + 1} / {MomentCount}";

    [RelayCommand]
    private async Task LoadAsync(ObjectivePatternCard pattern)
    {
        if (IsLoading) return;
        _pattern = pattern;
        IsLoading = true;
        try
        {
            PatternTitle = pattern.Title;
            SeverityLabel = pattern.Severity.ToUpperInvariant();
            SeverityHex = pattern.Severity == "high"
                ? AppSemanticPalette.NegativeHex
                : AppSemanticPalette.AccentGoldHex;

            var reviewedKeys = await _evidenceRepo.GetReviewedPatternKeysAsync();
            IsReviewed = reviewedKeys.Contains(pattern.PatternKey);

            var moments = await _evidenceRepo.GetPatternMomentsAsync(pattern);
            var distinctGames = moments.Select(m => m.GameId).Distinct().ToList();

            // Seed which moments already have a clip, so Save won't re-extract.
            // A moment is "clipped" if any clip bookmark on its game starts
            // within its window (±a few seconds of slack for the padded point).
            var clippedEvidenceIds = await ComputeClippedMomentsAsync(moments, distinctGames);

            DispatcherHelper.RunOnUIThread(() =>
            {
                Moments.Clear();
                var ordinal = 0;
                foreach (var m in moments)
                {
                    var item = new PatternMomentItem(m, ++ordinal);
                    if (clippedEvidenceIds.Contains(m.EvidenceId)) item.MarkClipped();
                    Moments.Add(item);
                }
                MomentCount = Moments.Count;
                PatternSubtitle = MomentCount == 0
                    ? "No moments are still pending for this pattern."
                    : $"{MomentCount} moment{(MomentCount == 1 ? "" : "s")} across {distinctGames.Count} game{(distinctGames.Count == 1 ? "" : "s")}";

                if (MomentCount > 0)
                {
                    GoToIndex(0);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load pattern review for {Kind}", pattern.Kind);
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    /// <summary>
    /// Return the evidence ids whose moment window already has a clip attached.
    /// Batched one bookmark read per game. A moment counts as clipped when a
    /// clip bookmark on its game starts within ClipMatchSlack of the moment's
    /// start — close enough that re-clipping would just duplicate it.
    /// </summary>
    private async Task<IReadOnlySet<long>> ComputeClippedMomentsAsync(
        IReadOnlyList<PatternMoment> moments, IReadOnlyList<long> games)
    {
        var clipped = new HashSet<long>();
        try
        {
            // gameId -> clip bookmark start times (seconds)
            var clipStartsByGame = new System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<int>>();
            foreach (var gameId in games)
            {
                var bookmarks = await _vodRepo.GetBookmarksAsync(gameId);
                var starts = bookmarks
                    .Where(b => !string.IsNullOrWhiteSpace(b.ClipPath) && b.ClipStartSeconds.HasValue)
                    .Select(b => b.ClipStartSeconds!.Value)
                    .ToList();
                clipStartsByGame[gameId] = starts;
            }

            foreach (var m in moments)
            {
                if (!clipStartsByGame.TryGetValue(m.GameId, out var starts)) continue;
                var momentStart = m.StartTimeSeconds ?? 0;
                if (starts.Any(s => Math.Abs(s - momentStart) <= ClipMatchSlack))
                {
                    clipped.Add(m.EvidenceId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pattern review: clip-state seed failed");
        }
        return clipped;
    }

    [RelayCommand]
    private void SelectMoment(PatternMomentItem? item)
    {
        if (item is null) return;
        var idx = Moments.IndexOf(item);
        if (idx >= 0) GoToIndex(idx);
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void NextMoment()
    {
        if (CanGoNext) GoToIndex(CurrentIndex + 1);
    }

    [RelayCommand(CanExecute = nameof(CanGoPrev))]
    private void PrevMoment()
    {
        if (CanGoPrev) GoToIndex(CurrentIndex - 1);
    }

    private void GoToIndex(int index)
    {
        if (index < 0 || index >= Moments.Count) return;

        // Flush the outgoing moment's note (and clip) in the background so
        // navigation stays snappy. Capture refs — CurrentMoment is about to change.
        if (CurrentMoment is not null)
        {
            CurrentMoment.IsActive = false;
            _noteDebounce?.Stop();
            var leaving = CurrentMoment;
            var leavingText = MomentNote;
            _ = FlushNoteAsync(leaving, leavingText);
        }

        CurrentIndex = index;
        var item = Moments[index];
        item.IsActive = true;
        item.HasBeenViewed = true;
        CurrentMoment = item;

        // Load the new moment's note WITHOUT triggering an autosave.
        _suppressNoteSave = true;
        MomentNote = item.Note;
        _suppressNoteSave = false;
        MomentStatus = "";

        NextMomentCommand.NotifyCanExecuteChanged();
        PrevMomentCommand.NotifyCanExecuteChanged();
        MomentActivated?.Invoke(item);
    }

    [RelayCommand]
    private async Task MarkReviewedAsync()
    {
        if (_pattern is null) return;
        // Make sure the moment you're on is saved before closing out the pattern.
        await CommitPendingAsync();
        try
        {
            await _evidenceRepo.MarkPatternReviewedAsync(_pattern.PatternKey, _pattern.Kind, MomentCount);
            IsReviewed = true;
            // Tell the dashboard to refresh its pattern card + reviewed count.
            WeakReferenceMessenger.Default.Send<PatternReviewedMessage>(new PatternReviewedMessage(_pattern.PatternKey));

            // v2.18: feed the insight forward — offer to turn the pattern into
            // a 5-game mini objective instead of letting it vanish with the nag.
            FixObjectiveTitle = $"Fix: {_pattern.Title}";
            ShowFixObjectiveOffer = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark pattern {Key} reviewed", _pattern.PatternKey);
        }
    }

    // ── v2.18: pattern → mini-objective pipe ────────────────────────────────

    [ObservableProperty] private bool _showFixObjectiveOffer;
    [ObservableProperty] private string _fixObjectiveTitle = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsClosed))]
    private bool _fixObjectiveCreated;

    // ── P-012 (digest 2026-06-14): closure state ────────────────────────────
    // Make "mark reviewed" feel done. Presentation-only over data the VM already
    // holds — no schema, no repo call. Both offer branches now terminate: DRILL IT
    // already showed a confirmation; "Not now" used to collapse into silence, so
    // IsClosedNoDrill gives it an explicit terminal line.

    /// <summary>Set when the user declined the drill ("Not now") — drives the
    /// "no drill created" terminal line so the decline branch isn't silent.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsClosed))]
    private bool _isClosedNoDrill;

    /// <summary>Count of this pattern's moments that have a note saved.</summary>
    public int NotesCount => Moments.Count(static m => m.HasNote);

    /// <summary>One-line plain-English closure summary shown in the terminal panel.</summary>
    public string ClosureSummary =>
        $"Worked through {PatternTitle} — {MomentCount} moment{(MomentCount == 1 ? "" : "s")} reviewed"
        + (NotesCount > 0 ? $", {NotesCount} note{(NotesCount == 1 ? "" : "s")} saved." : ".");

    /// <summary>The pattern has reached a terminal state (drilled or declined),
    /// so the completion summary + Back-to-dashboard affordance should show.</summary>
    public bool IsClosed => FixObjectiveCreated || IsClosedNoDrill;

    [RelayCommand]
    private async Task CreateFixObjectiveAsync()
    {
        if (_pattern is null || string.IsNullOrWhiteSpace(FixObjectiveTitle))
        {
            return;
        }

        try
        {
            await _objectivesRepo.CreateWithPhasesAndTargetAsync(
                FixObjectiveTitle.Trim(),
                skillArea: "pattern fix",
                type: "mini",
                completionCriteria: "",
                description: $"From pattern review: {_pattern.Title}. {_pattern.Detail}".Trim(),
                practicePre: true,
                practiceIn: true,
                practicePost: false,
                targetGameCount: 5);

            FixObjectiveCreated = true;
            ShowFixObjectiveOffer = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create fix objective from pattern {Key}", _pattern.PatternKey);
        }
    }

    [RelayCommand]
    private void DismissFixObjective()
    {
        // P-012: "Not now" used to just hide the offer and end in silence. Mark a
        // terminal state so the closure panel shows a "no drill created" line and
        // the Back-to-dashboard affordance, matching the DRILL IT branch.
        ShowFixObjectiveOffer = false;
        IsClosedNoDrill = true;
    }

    // ── "This moment" work panel — autosave ─────────────────────────────────
    //
    // The note autosaves: a short pause after typing flushes it (and clips the
    // moment once). Leaving a moment or the page flushes immediately so nothing
    // is lost. The user never presses Save.

    private void EnsureDebounce()
    {
        if (_noteDebounce is not null) return;
        _noteDebounce = new Microsoft.UI.Xaml.DispatcherTimer { Interval = NoteDebounce };
        _noteDebounce.Tick += async (_, _) =>
        {
            _noteDebounce!.Stop();
            await FlushNoteAsync(CurrentMoment, MomentNote);
        };
    }

    partial void OnMomentNoteChanged(string value)
    {
        // Ignore the programmatic set when a moment loads its note into the box.
        if (_suppressNoteSave) return;
        EnsureDebounce();
        _noteDebounce!.Stop();
        _noteDebounce.Start();
        MomentStatus = "Saving…";
    }

    /// <summary>
    /// Persist a note onto a SPECIFIC moment (captured, so flushing the outgoing
    /// moment on navigation is correct), and — once per moment, only when the
    /// note is non-empty — silently clip its padded window and attach it.
    /// </summary>
    private async Task FlushNoteAsync(PatternMomentItem? moment, string? noteText)
    {
        if (moment is null) return;
        var text = (noteText ?? "").Trim();

        // Nothing changed → don't churn the DB or status line.
        if (text == (moment.Note ?? "").Trim() && (text.Length == 0 || moment.HasClip))
        {
            return;
        }

        try
        {
            await _evidenceRepo.UpdateNoteAsync(moment.EvidenceId, text);
            moment.UpdateNote(text);

            var clipNeeded = text.Length > 0 && moment.HasVod && !moment.HasClip;
            if (!clipNeeded)
            {
                SetStatusIfCurrent(moment, "Saved");
                return;
            }

            SetStatusIfCurrent(moment, "Saved · clipping…");
            var (startS, endS) = ClipWindowFor(moment);
            var note = string.IsNullOrWhiteSpace(text) ? moment.Title : text;

            var clipPath = await _clipService.ExtractClipAsync(
                moment.VodPath, startS, endS, moment.ChampionName, _configService.ClipsFolder);

            if (string.IsNullOrEmpty(clipPath))
            {
                SetStatusIfCurrent(moment, "Saved (couldn't clip — is ffmpeg installed?)");
                return;
            }

            var bookmarkId = await _vodRepo.AddBookmarkAsync(
                moment.GameId, startS, note,
                clipStartSeconds: startS, clipEndSeconds: endS, clipPath: clipPath,
                quality: moment.Polarity);

            // Promote the moment's own evidence row to BE this clip (source_kind
            // = clip + source_id), so it shows as a saved clip in the review
            // rather than as a duplicate row. Mirrors the VOD player's save.
            await _evidenceRepo.AttachClipToEvidenceAsync(moment.EvidenceId, bookmarkId, startS, endS);

            moment.MarkClipped();
            SetStatusIfCurrent(moment, "Saved · clip kept");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pattern review: autosave/clip failed");
            SetStatusIfCurrent(moment, "Couldn't save");
        }
    }

    /// <summary>Only show status for a moment if it's still the one on screen.</summary>
    private void SetStatusIfCurrent(PatternMomentItem moment, string status)
    {
        if (ReferenceEquals(moment, CurrentMoment)) MomentStatus = status;
    }

    /// <summary>
    /// Flush any pending note for the moment currently on screen — called on
    /// page unload and before marking the pattern reviewed.
    /// </summary>
    public async Task CommitPendingAsync()
    {
        _noteDebounce?.Stop();
        await FlushNoteAsync(CurrentMoment, MomentNote);
    }

    /// <summary>
    /// The padded clip window for a moment: its own start–end when it has a
    /// real range, otherwise a lead/trail window around the single point so the
    /// clip is always watchable.
    /// </summary>
    private static (int startS, int endS) ClipWindowFor(PatternMomentItem moment)
    {
        var start = moment.StartTimeSeconds;
        var end = moment.EndTimeSeconds;
        if (end > start)
        {
            return (Math.Max(0, start), end);
        }
        return (Math.Max(0, start - ClipLeadSeconds), start + ClipTrailSeconds);
    }

    [RelayCommand]
    private void GoBack() => _navigationService.GoBack();
}

/// <summary>One moment in the pattern playlist — display wrapper over PatternMoment.</summary>
public partial class PatternMomentItem : ObservableObject
{
    private readonly PatternMoment _m;

    public PatternMomentItem(PatternMoment moment, int ordinal)
    {
        _m = moment;
        Ordinal = ordinal;
        _polarity = moment.Polarity;
        _note = moment.Note;
    }

    public long EvidenceId => _m.EvidenceId;
    public long GameId => _m.GameId;
    public string VodPath => _m.VodPath;
    public bool HasVod => !string.IsNullOrWhiteSpace(_m.VodPath);
    public int StartTimeSeconds => _m.StartTimeSeconds ?? 0;
    public int EndTimeSeconds => _m.EndTimeSeconds ?? _m.StartTimeSeconds ?? 0;
    public int Ordinal { get; }
    public string Title => _m.Title;

    /// <summary>Raw champion name for clip filenames (display uses ChampionLabel).</summary>
    public string ChampionName => _m.ChampionName;

    /// <summary>True once this moment's window has a clip attached (seeded at
    /// load from existing bookmarks, set after an auto-clip). Gates re-extract.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClipStateLabel))]
    private bool _hasClip;

    public void MarkClipped() => HasClip = true;

    /// <summary>Rail badge: shows a small "CLIP" marker once a moment is clipped.</summary>
    public string ClipStateLabel => HasClip ? "CLIP" : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    private string _note;

    public bool HasNote => !string.IsNullOrWhiteSpace(Note);

    /// <summary>Update the note after a save so the rail reflects the new text.</summary>
    public void UpdateNote(string note) => Note = note;

    /// <summary>"G2 · 14:32" — game ordinal within the pattern + in-game timestamp.</summary>
    public string GameLabel => $"{ChampionLabel} · {TimeLabel}";

    public string ChampionLabel => string.IsNullOrWhiteSpace(_m.ChampionName) ? "Game" : _m.ChampionName;
    public string ResultLabel => _m.Win ? "WIN" : "LOSS";
    public string ResultHex => _m.Win ? AppSemanticPalette.PositiveHex : AppSemanticPalette.NegativeHex;

    public string TimeLabel
    {
        get
        {
            var s = _m.StartTimeSeconds ?? 0;
            return $"{s / 60}:{s % 60:D2}";
        }
    }

    /// <summary>Header line shown over the video: which game + matchup + result.</summary>
    public string VideoHeaderText => $"{ChampionLabel} · {ResultLabel} · {TimeLabel}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PolarityLabel))]
    [NotifyPropertyChangedFor(nameof(AccentHex))]
    [NotifyPropertyChangedFor(nameof(AccentBrush))]
    private string _polarity;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowOpacity))]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowOpacity))]
    private bool _hasBeenViewed;

    /// <summary>Active moment is full strength; viewed-but-inactive dims; unseen sits between.</summary>
    public double RowOpacity => IsActive ? 1.0 : HasBeenViewed ? 0.55 : 0.85;

    public string PolarityLabel => Polarity switch
    {
        "good" => "GOOD",
        "bad" => "BAD",
        _ => "NEUTRAL",
    };

    public string AccentHex => Polarity switch
    {
        "good" => AppSemanticPalette.PositiveHex,
        "bad" => AppSemanticPalette.NegativeHex,
        _ => AppSemanticPalette.NeutralHex,
    };

    public Microsoft.UI.Xaml.Media.SolidColorBrush AccentBrush => AppSemanticPalette.Brush(AccentHex);
    public Microsoft.UI.Xaml.Media.SolidColorBrush ResultBrush => AppSemanticPalette.Brush(ResultHex);
}

/// <summary>Sent when a pattern is marked reviewed, so the dashboard refreshes.</summary>
public sealed record PatternReviewedMessage(string PatternKey);

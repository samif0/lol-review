#nullable enable

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoLReview.App.Contracts;
using LoLReview.App.Helpers;
using LoLReview.Core.Data.Repositories;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace LoLReview.App.ViewModels;

/// <summary>Optional parameter passed when the check is auto-fired after a loss.</summary>
/// <param name="GameId">Game row that triggered the check (null for manual runs).</param>
/// <param name="StreakCount">Consecutive-loss count at trigger time.</param>
public sealed record TiltCheckInfo(long? GameId, int StreakCount);

/// <summary>
/// Post-loss guided tilt check. Design B per the research review (Torre &amp; Lieberman 2018;
/// Bushman 2002; Webb, Miles &amp; Sheeran 2012; Balban/Huberman 2023; Gollwitzer &amp; Sheeran 2006).
///
/// Five steps, ~3:30 total, rotating content pools to resist habituation:
///   0. Pre-rating + label (select 1–3 from curated gaming chips)        — affect labeling
///   1. Cyclic sighing, 80 s, exhale-weighted pacer                      — vagal down-regulation
///   2. Reappraisal (pick 1 of 4 rotating options)                        — cognitive reframing
///   3. Implementation intention (trigger × response, rotating pools)     — next-game carry-over
///   4. Post-rating + save                                                — pre/post delta for stats
/// </summary>
public partial class TiltCheckViewModel : ObservableObject
{
    private readonly ITiltCheckRepository _tiltChecks;
    private DispatcherTimer? _breatheTimer;
    private long? _gameId;

    // ── Curated gaming emotion chips (rotating subset shown each run) ────────
    //
    // Draws from the 6 existing TiltCheck labels + gaming-specific research pool.
    // Up to 3 may be selected; selection beats self-generation for immediate
    // labeling effects (Vives et al. 2023).
    private static readonly string[] EmotionPool =
    [
        "Angry", "Frustrated", "Anxious", "Hopeless", "Numb", "Restless",
        "Helpless", "Cheated", "Deflated", "Ashamed", "Disrespected", "Envious",
        "Crushed", "Done", "Tense", "Hostile",
    ];
    private const int EmotionChipsShown = 10;

    // ── Reappraisal menu (Webb 2012; Denny/Ochsner 2014; Ranney 2024) ────────
    private static readonly string[] ReappraisalPool =
    [
        "This is one of ~2,000 games I'll play this year — one data point.",
        "Rank is noisy under 100 games. One loss is not a trend.",
        "I was on tilt by mid-game — that's the real lesson, not the loss.",
        "The past 30 minutes are sunk cost. Only the next game is in front of me.",
        "Losses are priced in. My rank over a month is what matters.",
        "If a friend told me about this game, I'd tell them to let it go.",
        "I can be frustrated AND play well next game. They're not the same thing.",
        "What I notice about this loss now is not what I'll notice in a week.",
    ];
    private const int ReappraisalOptionsShown = 4;

    // ── If-then trigger × response pools (Gollwitzer/Sheeran 2006;
    //     Schweiger Gallo 2018 for anger specifically) ─────────────────────
    private static readonly string[] TriggerPool =
    [
        "If I notice myself flame-typing in chat",
        "If I die and start to blame a teammate",
        "If I fall two levels behind in lane",
        "If my jungler hasn't ganked by minute 10",
        "If I catch myself tilting at champ select",
        "If I start forcing fights I wouldn't normally take",
        "If I die the same way twice",
        "If I'm about to ping-spam someone",
    ];
    private static readonly string[] ResponsePool =
    [
        "I will mute-all for the next 2 minutes.",
        "I will farm for 60 seconds before doing anything else.",
        "I will ward a safe bush and take one slow breath.",
        "I will ping 'retreat' instead of typing.",
        "I will play the next minute purely around vision.",
        "I will type 'gg wp' and close the chat box.",
        "I will stand up and stretch for 15 seconds next death-wait.",
        "I will call one objective to refocus the team.",
    ];
    private const int IfThenOptionsShown = 3;

    public TiltCheckViewModel()
    {
        _tiltChecks = App.GetService<ITiltCheckRepository>();
        Emotions = new ObservableCollection<EmotionOption>();
        ReappraisalOptions = new ObservableCollection<string>();
        IfThenTriggers = new ObservableCollection<string>();
        IfThenResponses = new ObservableCollection<string>();
        RotateContent();
    }

    // ── Step tracking ────────────────────────────────────────────────

    [ObservableProperty] private int _currentStep;

    /// <summary>Controls whether the primary "Next" button is enabled on labeling step.</summary>
    [ObservableProperty] private bool _canGoNext;

    /// <summary>Optional streak indicator surfaced on step 0 when auto-triggered.</summary>
    [ObservableProperty] private int _streakCount;

    [ObservableProperty] private bool _hasStreakContext;

    // ── Step 0: Pre-rate + label ─────────────────────────────────────

    public ObservableCollection<EmotionOption> Emotions { get; }

    /// <summary>Joined label string saved to <c>tilt_checks.emotion</c> (e.g. "Frustrated, Helpless").</summary>
    [ObservableProperty] private string _selectedEmotion = "";

    [ObservableProperty] private string _selectedEmotionColor = "";

    [ObservableProperty] private int _intensityBefore = 5;

    partial void OnIntensityBeforeChanged(int value)
    {
        OnPropertyChanged(nameof(IntensityBeforeDisplay));
    }

    public string IntensityBeforeDisplay => $"{IntensityBefore} / 10";

    private readonly List<string> _selectedEmotionList = new();
    private const int MaxEmotionSelections = 3;

    // ── Step 1: Breathe (cyclic sighing, 80 s) ───────────────────────
    //
    // Pattern: double-inhale (2 s) → long exhale (6 s), ~7.5 cycles/min.
    // Exhale-weighted per Balban et al. 2023 (Huberman lab) which beat
    // other short breathing protocols on acute affect.
    private static readonly (string Label, int Seconds)[] BreathPhases =
    [
        ("Inhale...", 1),
        ("Top off...", 1),
        ("Exhale slowly...", 6),
    ];
    private const int TotalBreathSeconds = 80;

    [ObservableProperty] private bool _isBreathing;
    [ObservableProperty] private string _breathePhaseText = "Inhale...";
    [ObservableProperty] private int _breatheCountdown = 1;
    [ObservableProperty] private double _breatheProgress;
    [ObservableProperty] private int _breatheSecondsElapsed;
    [ObservableProperty] private int _currentCycle = 1;

    private int _phaseIndex;

    // ── Step 2: Reappraisal ──────────────────────────────────────────

    public ObservableCollection<string> ReappraisalOptions { get; }

    [ObservableProperty] private string _reframeResponse = "";

    // ── Step 3: Implementation intention ─────────────────────────────

    public ObservableCollection<string> IfThenTriggers { get; }

    public ObservableCollection<string> IfThenResponses { get; }

    [ObservableProperty] private string _selectedTrigger = "";

    [ObservableProperty] private string _selectedResponse = "";

    /// <summary>Composed "If X then Y" string saved to <c>tilt_checks.if_then_plan</c>.</summary>
    public string IfThenPlan =>
        string.IsNullOrEmpty(SelectedTrigger) || string.IsNullOrEmpty(SelectedResponse)
            ? ""
            : $"{SelectedTrigger}, then {SelectedResponse.TrimStart().TrimEnd('.')}.";

    partial void OnSelectedTriggerChanged(string value) => OnPropertyChanged(nameof(IfThenPlan));
    partial void OnSelectedResponseChanged(string value) => OnPropertyChanged(nameof(IfThenPlan));

    // ── Step 4: Post-rate ────────────────────────────────────────────

    [ObservableProperty] private int _intensityAfter = 5;

    partial void OnIntensityAfterChanged(int value)
    {
        OnPropertyChanged(nameof(IntensityAfterDisplay));
        OnPropertyChanged(nameof(ResultMessage));
        OnPropertyChanged(nameof(ResultColor));
    }

    public string IntensityAfterDisplay => $"{IntensityAfter} / 10";

    public string ResultMessage
    {
        get
        {
            var diff = IntensityBefore - IntensityAfter;
            if (diff > 0)
                return $"You went from {IntensityBefore} \u2192 {IntensityAfter}. That's {diff} down.";
            if (diff == 0)
                return "Same intensity — that's okay. A 4-minute ritual can't clear cortisol; it catches you on the slope.";
            return "Higher than before. Consider a longer break before the next game.";
        }
    }

    public SolidColorBrush ResultColor
    {
        get
        {
            var diff = IntensityBefore - IntensityAfter;
            if (diff > 0) return new SolidColorBrush(ColorHelper.FromArgb(255, 126, 201, 160));
            if (diff == 0) return new SolidColorBrush(ColorHelper.FromArgb(255, 138, 128, 168));
            return new SolidColorBrush(ColorHelper.FromArgb(255, 211, 140, 144));
        }
    }

    // ── Commands ─────────────────────────────────────────────────────

    /// <summary>Called by the page on Navigated with optional streak context.</summary>
    [RelayCommand]
    public void Start(TiltCheckInfo? info)
    {
        _gameId = info?.GameId;
        StreakCount = info?.StreakCount ?? 0;
        HasStreakContext = StreakCount > 0;
        StartOver();
    }

    [RelayCommand]
    private void ToggleEmotion(string emotion)
    {
        if (_selectedEmotionList.Contains(emotion))
        {
            _selectedEmotionList.Remove(emotion);
        }
        else
        {
            if (_selectedEmotionList.Count >= MaxEmotionSelections)
            {
                // Replace oldest — keep the set bounded at 3 labels.
                _selectedEmotionList.RemoveAt(0);
            }
            _selectedEmotionList.Add(emotion);
        }

        SelectedEmotion = string.Join(", ", _selectedEmotionList);
        var first = _selectedEmotionList.FirstOrDefault();
        SelectedEmotionColor = first is null
            ? ""
            : Emotions.FirstOrDefault(e => e.Name == first)?.Color ?? "#A78BFA";
        CanGoNext = _selectedEmotionList.Count > 0;
    }

    [RelayCommand]
    private void SelectReframe(string option)
    {
        ReframeResponse = option;
    }

    [RelayCommand]
    private void SelectTrigger(string option)
    {
        SelectedTrigger = option;
    }

    [RelayCommand]
    private void SelectResponse(string option)
    {
        SelectedResponse = option;
    }

    [RelayCommand]
    private void NextStep()
    {
        // Step 0 requires at least one label; other steps are soft-gated.
        if (CurrentStep == 0 && _selectedEmotionList.Count == 0)
            return;

        CurrentStep++;

        if (CurrentStep == 1)
        {
            StartBreathing();
        }
        else if (CurrentStep == 4)
        {
            IntensityAfter = IntensityBefore;
            OnPropertyChanged(nameof(IntensityAfterDisplay));
            OnPropertyChanged(nameof(ResultMessage));
            OnPropertyChanged(nameof(ResultColor));
        }
    }

    [RelayCommand]
    private void PreviousStep()
    {
        if (CurrentStep > 0)
        {
            if (CurrentStep == 1)
                StopBreathing();
            CurrentStep--;
        }
    }

    [RelayCommand]
    private void SkipBreathing()
    {
        StopBreathing();
        CurrentStep = 2;
    }

    [RelayCommand]
    private async Task SaveAndClose()
    {
        try
        {
            await _tiltChecks.SaveAsync(
                emotion: SelectedEmotion,
                intensityBefore: IntensityBefore,
                intensityAfter: IntensityAfter,
                reframeResponse: ReframeResponse,
                gameId: _gameId,
                ifThenPlan: IfThenPlan);
        }
        catch
        {
            // Best-effort save
        }

        StartOver();
    }

    [RelayCommand]
    private void StartOver()
    {
        StopBreathing();
        CurrentStep = 0;
        _selectedEmotionList.Clear();
        SelectedEmotion = "";
        SelectedEmotionColor = "";
        IntensityBefore = 5;
        IntensityAfter = 5;
        CanGoNext = false;
        ReframeResponse = "";
        SelectedTrigger = "";
        SelectedResponse = "";
        RotateContent();
    }

    // ── Content rotation (resist habituation) ────────────────────────

    private void RotateContent()
    {
        var rng = new Random();
        RepopulateFromPool(Emotions.Clear, Emotions.Add, EmotionPool, EmotionChipsShown, rng,
            map: name => new EmotionOption(name, PickColorFor(name)));
        RepopulateFromPool(ReappraisalOptions.Clear, ReappraisalOptions.Add, ReappraisalPool, ReappraisalOptionsShown, rng,
            map: s => s);
        RepopulateFromPool(IfThenTriggers.Clear, IfThenTriggers.Add, TriggerPool, IfThenOptionsShown, rng,
            map: s => s);
        RepopulateFromPool(IfThenResponses.Clear, IfThenResponses.Add, ResponsePool, IfThenOptionsShown, rng,
            map: s => s);
    }

    private static void RepopulateFromPool<TSource, TTarget>(
        Action clear,
        Action<TTarget> add,
        IReadOnlyList<TSource> pool,
        int count,
        Random rng,
        Func<TSource, TTarget> map)
    {
        clear();
        var indices = Enumerable.Range(0, pool.Count).OrderBy(_ => rng.Next()).Take(count);
        foreach (var i in indices)
        {
            add(map(pool[i]));
        }
    }

    private static string PickColorFor(string emotion) => emotion switch
    {
        "Angry" or "Hostile" or "Crushed" => "#D38C90",
        "Frustrated" or "Cheated" or "Disrespected" => "#C9956A",
        "Anxious" or "Tense" => "#C9956A",
        "Hopeless" or "Helpless" or "Deflated" or "Done" => "#A78BFA",
        "Numb" or "Ashamed" or "Envious" => "#8A80A8",
        "Restless" => "#8A7AF2",
        _ => "#A78BFA",
    };

    // ── Breathing engine (cyclic sighing pacer) ──────────────────────

    private void StartBreathing()
    {
        IsBreathing = true;
        _phaseIndex = 0;
        CurrentCycle = 1;
        BreatheSecondsElapsed = 0;
        BreatheProgress = 0;
        StartBreathPhase();
    }

    private void StopBreathing()
    {
        IsBreathing = false;
        _breatheTimer?.Stop();
        _breatheTimer = null;
    }

    private void StartBreathPhase()
    {
        if (BreatheSecondsElapsed >= TotalBreathSeconds)
        {
            StopBreathing();
            DispatcherHelper.RunOnUIThread(() => CurrentStep = 2);
            return;
        }

        var (label, seconds) = BreathPhases[_phaseIndex];
        BreathePhaseText = label;
        BreatheCountdown = seconds;

        _breatheTimer?.Stop();
        _breatheTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _breatheTimer.Tick += OnBreathTick;
        _breatheTimer.Start();
    }

    private void OnBreathTick(object? sender, object e)
    {
        BreatheCountdown--;
        BreatheSecondsElapsed++;
        BreatheProgress = Math.Min(1.0, (double)BreatheSecondsElapsed / TotalBreathSeconds);

        if (BreatheCountdown <= 0)
        {
            _breatheTimer?.Stop();

            _phaseIndex++;
            if (_phaseIndex >= BreathPhases.Length)
            {
                _phaseIndex = 0;
                CurrentCycle++;
            }

            StartBreathPhase();
        }
    }
}

/// <summary>Simple record for emotion pill display.</summary>
public sealed record EmotionOption(string Name, string Color);

#nullable enable

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoLReview.App.Helpers;
using LoLReview.Core.Data.Repositories;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace LoLReview.App.ViewModels;

/// <summary>
/// ViewModel for the guided Tilt Check exercise (5 steps).
/// Based on affect labeling, 4-7-8 breathing, cognitive reframing,
/// and implementation intentions.
/// </summary>
public partial class TiltCheckViewModel : ObservableObject
{
    private readonly ITiltCheckRepository _tiltChecks;
    private DispatcherTimer? _breatheTimer;

    // ── Breathing constants ──────────────────────────────────────────
    private static readonly (string Label, int Seconds)[] BreathPhases =
    [
        ("Breathe in...", 4),
        ("Hold...", 7),
        ("Breathe out...", 8),
    ];
    private const int TotalBreathCycles = 5;

    public TiltCheckViewModel()
    {
        _tiltChecks = App.GetService<ITiltCheckRepository>();

        Emotions =
        [
            new EmotionOption("Angry", "#D38C90"),
            new EmotionOption("Frustrated", "#C9956A"),
            new EmotionOption("Anxious", "#C9956A"),
            new EmotionOption("Hopeless", "#A78BFA"),
            new EmotionOption("Numb", "#8A80A8"),
            new EmotionOption("Restless", "#8A7AF2"),
        ];

        CueWords = ["Calm", "Patient", "Focused", "Aggressive", "Clean", "Fun"];
    }

    // ── Step tracking ────────────────────────────────────────────────

    [ObservableProperty]
    private int _currentStep;

    [ObservableProperty]
    private bool _canGoNext;

    // ── Step 0: Check In ─────────────────────────────────────────────

    public ObservableCollection<EmotionOption> Emotions { get; }

    [ObservableProperty]
    private string _selectedEmotion = "";

    [ObservableProperty]
    private string _selectedEmotionColor = "";

    [ObservableProperty]
    private int _intensityBefore = 5;

    partial void OnIntensityBeforeChanged(int value)
    {
        OnPropertyChanged(nameof(IntensityBeforeDisplay));
    }

    public string IntensityBeforeDisplay => $"{IntensityBefore} / 10";

    // ── Step 1: Breathe ──────────────────────────────────────────────

    [ObservableProperty]
    private bool _isBreathing;

    [ObservableProperty]
    private string _breathePhaseText = "Breathe in...";

    [ObservableProperty]
    private int _breatheCountdown = 4;

    [ObservableProperty]
    private int _currentCycle = 1;

    [ObservableProperty]
    private double _breatheProgress;

    private int _phaseIndex;
    private int _cycleIndex;

    // ── Step 2: Reframe ──────────────────────────────────────────────

    [ObservableProperty]
    private string _reframeThought = "";

    [ObservableProperty]
    private string _thoughtType = "";

    [ObservableProperty]
    private string _reframeResponse = "";

    // ── Step 3: Reset ────────────────────────────────────────────────

    public ObservableCollection<string> CueWords { get; }

    [ObservableProperty]
    private string _selectedCueWord = "";

    [ObservableProperty]
    private string _focusIntention = "";

    // ── Step 4: Done ─────────────────────────────────────────────────

    [ObservableProperty]
    private int _intensityAfter = 5;

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
                return $"Nice. You went from {IntensityBefore} \u2192 {IntensityAfter}. That's {diff} points down.";
            if (diff == 0)
                return "Same intensity \u2014 that's okay. Sometimes just pausing is the win.";
            return "Higher than before \u2014 consider taking a longer break. Your future self will thank you.";
        }
    }

    public SolidColorBrush ResultColor
    {
        get
        {
            var diff = IntensityBefore - IntensityAfter;
            if (diff > 0) return new SolidColorBrush(ColorHelper.FromArgb(255, 126, 201, 160));  // #7EC9A0 positive
            if (diff == 0) return new SolidColorBrush(ColorHelper.FromArgb(255, 138, 128, 168)); // #8A80A8 neutral
            return new SolidColorBrush(ColorHelper.FromArgb(255, 211, 140, 144));                // #D38C90 negative
        }
    }

    // ── Commands ─────────────────────────────────────────────────────

    [RelayCommand]
    private void SelectEmotion(string emotion)
    {
        SelectedEmotion = emotion;
        var match = Emotions.FirstOrDefault(e => e.Name == emotion);
        SelectedEmotionColor = match?.Color ?? "#A78BFA";
        CanGoNext = true;
    }

    [RelayCommand]
    private void SelectThoughtType(string type)
    {
        ThoughtType = type;
    }

    [RelayCommand]
    private void SelectCueWord(string word)
    {
        SelectedCueWord = word;
    }

    [RelayCommand]
    private void NextStep()
    {
        if (CurrentStep == 0 && string.IsNullOrEmpty(SelectedEmotion))
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
                reframeThought: ReframeThought,
                reframeResponse: ReframeResponse,
                thoughtType: ThoughtType,
                cueWord: SelectedCueWord,
                focusIntention: FocusIntention);
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
        SelectedEmotion = "";
        SelectedEmotionColor = "";
        IntensityBefore = 5;
        IntensityAfter = 5;
        CanGoNext = false;
        ReframeThought = "";
        ThoughtType = "";
        ReframeResponse = "";
        SelectedCueWord = "";
        FocusIntention = "";
    }

    // ── Breathing engine ─────────────────────────────────────────────

    private void StartBreathing()
    {
        IsBreathing = true;
        _cycleIndex = 0;
        _phaseIndex = 0;
        CurrentCycle = 1;
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
        if (_cycleIndex >= TotalBreathCycles)
        {
            StopBreathing();
            DispatcherHelper.RunOnUIThread(() => CurrentStep = 2);
            return;
        }

        var (label, seconds) = BreathPhases[_phaseIndex];
        BreathePhaseText = label;
        BreatheCountdown = seconds;
        CurrentCycle = _cycleIndex + 1;

        var totalPhases = TotalBreathCycles * BreathPhases.Length;
        var completedPhases = _cycleIndex * BreathPhases.Length + _phaseIndex;
        BreatheProgress = (double)completedPhases / totalPhases;

        _breatheTimer?.Stop();
        _breatheTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _breatheTimer.Tick += OnBreathTick;
        _breatheTimer.Start();
    }

    private void OnBreathTick(object? sender, object e)
    {
        BreatheCountdown--;

        if (BreatheCountdown <= 0)
        {
            _breatheTimer?.Stop();

            _phaseIndex++;
            if (_phaseIndex >= BreathPhases.Length)
            {
                _phaseIndex = 0;
                _cycleIndex++;
            }

            StartBreathPhase();
        }
    }
}

/// <summary>Simple record for emotion pill display.</summary>
public sealed record EmotionOption(string Name, string Color);

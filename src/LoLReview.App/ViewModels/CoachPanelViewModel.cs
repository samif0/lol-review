#nullable enable

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoLReview.App.Services;
using Microsoft.Extensions.Logging;

namespace LoLReview.App.ViewModels;

/// <summary>
/// ViewModel for the in-review coach panel. Handles post-game drafts
/// (Phase 5a), clip reviews (Phase 5b), session + weekly coaching (Phase 5b).
/// Edit logging goes to /coach/log-edit.
/// </summary>
public sealed partial class CoachPanelViewModel : ObservableObject
{
    private readonly ICoachApiClient _api;
    private readonly ILogger<CoachPanelViewModel> _logger;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasDraft;
    [ObservableProperty] private string _mode = "";
    [ObservableProperty] private string _draftText = "";
    [ObservableProperty] private string _draftMistakes = "";
    [ObservableProperty] private string _draftWentWell = "";
    [ObservableProperty] private string _draftFocusNext = "";
    [ObservableProperty] private long _gameId;
    [ObservableProperty] private long _coachSessionId;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _providerTag = "";

    public CoachPanelViewModel(ICoachApiClient api, ILogger<CoachPanelViewModel> logger)
    {
        _api = api;
        _logger = logger;
    }

    public void SetGame(long gameId)
    {
        GameId = gameId;
        HasDraft = false;
        DraftText = "";
        DraftMistakes = "";
        DraftWentWell = "";
        DraftFocusNext = "";
        StatusText = "";
    }

    [RelayCommand]
    private async Task DraftPostGameAsync()
    {
        if (IsBusy || GameId <= 0) return;
        IsBusy = true;
        StatusText = "Drafting...";
        Mode = "post_game";

        try
        {
            var result = await _api.DraftPostGameAsync(GameId);
            if (result is null)
            {
                StatusText = "No draft. Check that the coach is running and configured.";
                return;
            }

            CoachSessionId = result.CoachSessionId;
            ProviderTag = $"[{result.Provider} / {result.Model} / {result.LatencyMs}ms]";
            DraftText = result.ResponseText;

            // If structured JSON is present, split into the three fields.
            if (!string.IsNullOrEmpty(result.ResponseJson))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(result.ResponseJson);
                    if (doc.RootElement.TryGetProperty("mistakes", out var m))
                        DraftMistakes = m.GetString() ?? "";
                    if (doc.RootElement.TryGetProperty("went_well", out var w))
                        DraftWentWell = w.GetString() ?? "";
                    if (doc.RootElement.TryGetProperty("focus_next", out var f))
                        DraftFocusNext = f.GetString() ?? "";
                }
                catch
                {
                    // Leave raw text; fields stay empty.
                }
            }

            HasDraft = true;
            StatusText = "Ready.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Called by the host after the user saves an edited version to `games`.
    /// Logs the edit distance for eventual fine-tuning signal.
    /// </summary>
    public async Task LogEditIfApplicableAsync(string finalEditedText)
    {
        if (CoachSessionId <= 0 || string.IsNullOrWhiteSpace(finalEditedText)) return;
        try
        {
            await _api.LogEditAsync(CoachSessionId, finalEditedText);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "log-edit best-effort failed");
        }
    }
}

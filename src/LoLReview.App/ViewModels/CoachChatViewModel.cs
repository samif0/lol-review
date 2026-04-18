#nullable enable

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoLReview.App.Services;
using Microsoft.Extensions.Logging;

namespace LoLReview.App.ViewModels;

public sealed partial class CoachChatViewModel : ObservableObject
{
    private readonly ICoachApiClient _api;
    private readonly ILogger<CoachChatViewModel> _logger;

    public ObservableCollection<CoachChatMessageViewModel> Messages { get; } = [];

    [ObservableProperty] private string _inputText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private long? _activeThreadId;
    [ObservableProperty] private string _scopeChipText = "";
    [ObservableProperty] private bool _hasScope;
    [ObservableProperty] private CoachScope? _pendingScope;
    [ObservableProperty] private string _providerTag = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _coachTotalsText = "";

    public CoachChatViewModel(ICoachApiClient api, ILogger<CoachChatViewModel> logger)
    {
        _api = api;
        _logger = logger;
    }

    /// <summary>
    /// Called by nav when the user lands on the Coach page with a pre-scoped
    /// question (e.g. "about this game" from ReviewPage).
    /// </summary>
    public void PinScope(CoachScope scope, string label)
    {
        PendingScope = scope;
        ScopeChipText = label;
        HasScope = true;
    }

    public void ClearScope()
    {
        PendingScope = null;
        ScopeChipText = "";
        HasScope = false;
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        var question = InputText.Trim();
        if (string.IsNullOrEmpty(question) || IsBusy) return;

        IsBusy = true;
        InputText = "";
        StatusText = "Asking...";

        // Show user message optimistically
        var tempUserMsg = new CoachChatMessageViewModel
        {
            Role = "user",
            Content = question,
            IsUser = true,
            ProviderTag = "",
        };
        Messages.Add(tempUserMsg);

        try
        {
            var result = await _api.AskAsync(question, ActiveThreadId, PendingScope);
            if (result is null)
            {
                StatusText = "No response. Check sidecar + provider.";
                return;
            }

            ActiveThreadId = result.ThreadId;
            // Replace temp id with the real one
            tempUserMsg.Id = result.UserMessage.Id;

            var assistantMsg = new CoachChatMessageViewModel
            {
                Id = result.AssistantMessage.Id,
                Role = "assistant",
                Content = result.AssistantMessage.Content,
                IsUser = false,
                ProviderTag = $"[{result.AssistantMessage.Provider} / {result.AssistantMessage.Model} / {result.AssistantMessage.LatencyMs}ms]",
            };
            Messages.Add(assistantMsg);

            ProviderTag = assistantMsg.ProviderTag;
            StatusText = "";
            UpdateTotals(result.CoachVisibleTotals);

            // After the first turn, the scope is baked into the thread — clear
            // the pending chip so subsequent turns go into the same thread
            // without re-scoping.
            if (HasScope)
            {
                // keep the label visible but note it's now thread-level
                StatusText = $"Scoped: {ScopeChipText}";
                PendingScope = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ask failed in viewmodel");
            StatusText = "Error. See log.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void NewConversation()
    {
        ActiveThreadId = null;
        Messages.Clear();
        ClearScope();
        StatusText = "";
        ProviderTag = "";
    }

    /// <summary>Load an existing thread by id (from a history list).</summary>
    public async Task LoadThreadAsync(long threadId)
    {
        IsBusy = true;
        try
        {
            var thread = await _api.GetThreadAsync(threadId);
            if (thread is null)
            {
                StatusText = $"Thread {threadId} not found.";
                return;
            }
            Messages.Clear();
            foreach (var m in thread.Messages)
            {
                Messages.Add(new CoachChatMessageViewModel
                {
                    Id = m.Id,
                    Role = m.Role,
                    Content = m.Content,
                    IsUser = m.Role == "user",
                    ProviderTag = m.Provider is null ? "" : $"[{m.Provider} / {m.Model} / {m.LatencyMs}ms]",
                });
            }
            ActiveThreadId = threadId;
            if (thread.Scope is not null)
            {
                PendingScope = thread.Scope;
                ScopeChipText = ScopeLabel(thread.Scope);
                HasScope = true;
            }
            else
            {
                ClearScope();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateTotals(IReadOnlyDictionary<string, int> totals)
    {
        if (totals.Count == 0)
        {
            CoachTotalsText = "";
            return;
        }
        var games = totals.TryGetValue("games_total", out var g) ? g : 0;
        var summaries = totals.TryGetValue("games_summarized", out var s) ? s : 0;
        var concepts = totals.TryGetValue("concepts_ranked", out var c) ? c : 0;
        var signals = totals.TryGetValue("signals_stable", out var sg) ? sg : 0;
        CoachTotalsText = $"Coach sees: {games} games ({summaries} summarized), {concepts} concepts, {signals} signals";
    }

    private static string ScopeLabel(CoachScope scope)
    {
        if (scope.GameId is not null) return $"Game #{scope.GameId}";
        if (scope.Since is not null && scope.Until is not null) return "Time window";
        if (scope.Since is not null) return "Since";
        if (scope.Until is not null) return "Until";
        return "Scoped";
    }
}

public sealed partial class CoachChatMessageViewModel : ObservableObject
{
    [ObservableProperty] private long _id;
    [ObservableProperty] private string _role = "";
    [ObservableProperty] private string _content = "";
    [ObservableProperty] private bool _isUser;
    [ObservableProperty] private string _providerTag = "";
}

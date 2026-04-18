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
        StatusText = "Connecting...";

        // Optimistically add the user message.
        var userMsg = new CoachChatMessageViewModel
        {
            Role = "user",
            Content = question,
            IsUser = true,
            ProviderTag = "",
        };
        Messages.Add(userMsg);

        // Add a placeholder assistant message that will be filled by streamed deltas.
        var assistantMsg = new CoachChatMessageViewModel
        {
            Role = "assistant",
            Content = "",
            IsUser = false,
            IsThinking = true,
            ProviderTag = "",
        };
        Messages.Add(assistantMsg);

        var completedCleanly = false;

        try
        {
            await foreach (var evt in _api.AskStreamAsync(question, ActiveThreadId, PendingScope))
            {
                switch (evt)
                {
                    case CoachAskStreamStarted started:
                        ActiveThreadId = started.ThreadId;
                        userMsg.Id = started.UserMessageId;
                        UpdateTotals(started.CoachVisibleTotals);
                        StatusText = "Thinking...";
                        break;

                    case CoachAskStreamDelta delta:
                        assistantMsg.IsThinking = false;
                        assistantMsg.Content += delta.Text;
                        StatusText = "";
                        break;

                    case CoachAskStreamDone done:
                        assistantMsg.Id = done.AssistantMessageId;
                        assistantMsg.IsThinking = false;
                        assistantMsg.ProviderTag = $"[{done.Provider} / {done.Model} / {done.LatencyMs}ms]";
                        ProviderTag = assistantMsg.ProviderTag;
                        StatusText = HasScope ? $"Scoped: {ScopeChipText}" : "";
                        if (HasScope) PendingScope = null;
                        completedCleanly = true;
                        break;

                    case CoachAskStreamError err:
                        assistantMsg.IsThinking = false;
                        // Remove the empty placeholder if nothing streamed
                        if (string.IsNullOrEmpty(assistantMsg.Content))
                        {
                            Messages.Remove(assistantMsg);
                        }
                        // Also remove the user message if the ask didn't even start
                        // (no thread id assigned by the started event).
                        if (ActiveThreadId is null || userMsg.Id == 0)
                        {
                            Messages.Remove(userMsg);
                        }
                        StatusText = $"Error: {err.Message}";
                        break;
                }
            }

            if (!completedCleanly && assistantMsg.IsThinking)
            {
                // Stream ended without a done or error event.
                if (string.IsNullOrEmpty(assistantMsg.Content))
                {
                    Messages.Remove(assistantMsg);
                }
                assistantMsg.IsThinking = false;
                StatusText = string.IsNullOrEmpty(StatusText) ? "Stream ended unexpectedly." : StatusText;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ask-stream failed in viewmodel");
            if (string.IsNullOrEmpty(assistantMsg.Content))
            {
                Messages.Remove(assistantMsg);
            }
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
    [ObservableProperty] private bool _isThinking;
}

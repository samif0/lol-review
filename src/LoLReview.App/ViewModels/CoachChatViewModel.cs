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

                    case CoachAskStreamThinking:
                        // Reasoning tokens are streaming on the server; keep
                        // the spinner on and do not leak them to the UI.
                        assistantMsg.IsThinking = true;
                        StatusText = "Thinking...";
                        break;

                    case CoachAskStreamDelta delta:
                        assistantMsg.IsThinking = false;
                        // Strip markdown emphasis the UI doesn't render —
                        // literal '*word*' / '_word_' / '`word`' look broken.
                        assistantMsg.Content += StripEmphasis(delta.Text);
                        StatusText = "";
                        break;

                    case CoachAskStreamDone done:
                        assistantMsg.Id = done.AssistantMessageId;
                        assistantMsg.IsThinking = false;
                        assistantMsg.ProviderTag = FormatLatency(done.LatencyMs);
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

    [ObservableProperty] private bool _isBackfilling;
    [ObservableProperty] private string _backfillStatus = "";

    [RelayCommand]
    private async Task BackfillAsync()
    {
        if (IsBackfilling) return;
        IsBackfilling = true;
        BackfillStatus = "Building summaries...";

        try
        {
            // Hit the existing bulk endpoints in sequence.
            var http = _api;
            // Best-effort: no progress stream, just status text.
            using (var client = new System.Net.Http.HttpClient())
            {
                // Concept backfill runs one LLM call per game; on a large
                // history (hundreds of games) the full pass can take 20+
                // minutes. Give it plenty of room.
                client.Timeout = TimeSpan.FromMinutes(45);
                var baseUrl = "http://127.0.0.1:5577";

                try
                {
                    BackfillStatus = "Summaries...";
                    await client.PostAsync($"{baseUrl}/summaries/build-all", null);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "summaries backfill failed"); }

                try
                {
                    BackfillStatus = "Features...";
                    await client.PostAsync($"{baseUrl}/signals/compute-features-all", null);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "features backfill failed"); }

                try
                {
                    BackfillStatus = "Signal ranking...";
                    await client.PostAsync($"{baseUrl}/signals/rerank", null);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "signal rerank failed"); }

                try
                {
                    // Concepts require an LLM call per review, so this is the
                    // slow stage — a few seconds per game. The sidecar's
                    // extract_all endpoint handles pacing + rate limiting
                    // internally and returns once done.
                    BackfillStatus = "Concepts (this can take a few minutes)...";
                    using var extractReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/concepts/extract-all");
                    using var extractResp = await client.SendAsync(extractReq, HttpCompletionOption.ResponseHeadersRead);
                    if (extractResp.StatusCode == System.Net.HttpStatusCode.NotImplemented)
                    {
                        BackfillStatus = "Concepts skipped — ML extras pack not installed.";
                    }
                    else
                    {
                        BackfillStatus = "Concept clustering...";
                        await client.PostAsync($"{baseUrl}/concepts/recluster", null);
                    }
                }
                catch (Exception ex) { _logger.LogWarning(ex, "concepts backfill failed"); }
            }

            BackfillStatus = "Done. Ask something to verify.";
            var totals = await _api.GetTotalsAsync();
            if (totals is not null) UpdateTotals(totals);
        }
        finally
        {
            IsBackfilling = false;
        }
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
                    ProviderTag = m.LatencyMs is null ? "" : FormatLatency(m.LatencyMs.Value),
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

    private static string FormatLatency(int latencyMs)
    {
        if (latencyMs < 1000) return $"{latencyMs}ms";
        var seconds = latencyMs / 1000.0;
        return seconds < 10
            ? $"{seconds:F1}s"
            : $"{(int)Math.Round(seconds)}s";
    }

    /// <summary>
    /// Strip markdown emphasis markers (*italic*, **bold**, _italic_, `code`)
    /// that Gemma still sneaks into output even when told not to. UI renders
    /// plain text so these would show as literal characters.
    /// Strip markdown emphasis + inline game-number references + quoted
    /// phrases from a streamed chunk. Python sanitizes the persisted
    /// message; this runs on each streamed delta so the UI doesn't
    /// flash the ugly pattern for a moment before the server-clean
    /// version lands.
    /// </summary>
    private static string StripEmphasis(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // Remove markdown emphasis characters the UI doesn't render.
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch == '*' || ch == '`') continue;
            sb.Append(ch);
        }
        var cleaned = sb.ToString();
        // Underscores between word chars only (don't touch file paths, ids).
        cleaned = System.Text.RegularExpressions.Regex.Replace(
            cleaned, @"(?<=\w)_(?=\w)", "");
        cleaned = System.Text.RegularExpressions.Regex.Replace(
            cleaned, @"(?<=\s|^)_|_(?=\s|$)", "");
        // Strip single-quoted phrases (but not contractions): `'jungle
        // proximity'` -> `jungle proximity`. Uses curly + straight
        // quotes. Inner content must not contain an apostrophe so we
        // don't eat "don't" or "it's".
        cleaned = System.Text.RegularExpressions.Regex.Replace(
            cleaned,
            @"['\u2018\u2019]([A-Za-z][A-Za-z0-9 \-/+]{0,60}[A-Za-z0-9])['\u2018\u2019]",
            "$1");
        // Strip inline game-number references — the user doesn't want
        // to see raw game IDs until we ship clickable matchup links.
        cleaned = System.Text.RegularExpressions.Regex.Replace(
            cleaned, @"\[game\s*#?\d+\]", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        cleaned = System.Text.RegularExpressions.Regex.Replace(
            cleaned, @"\bgames\s+#\d+(?:\s*(?:,|and|&)\s*#?\d+)*\b",
            "your recent games", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        cleaned = System.Text.RegularExpressions.Regex.Replace(
            cleaned, @"\bgame\s+#\d+\b",
            "that game", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // Collapse the artifacts from the removals.
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"  +", " ");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\(\s*\)", "");
        return cleaned;
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

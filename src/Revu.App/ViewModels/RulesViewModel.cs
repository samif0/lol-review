#nullable enable

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Revu.Core.Data.Repositories;

namespace Revu.App.ViewModels;

/// <summary>Display model for a rule card.</summary>
public sealed class RuleDisplayItem
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string RuleType { get; init; } = "custom";
    public string ConditionValue { get; init; } = "";
    public bool IsActive { get; init; } = true;

    // Violation info
    public bool IsViolated { get; init; }
    public string ViolationReason { get; init; } = "";
    public bool IsChecked { get; init; } // Has been checked (non-custom)
    public bool IsOk => IsChecked && !IsViolated;

    // Display properties
    public string TypeBadge => RuleType switch
    {
        "custom" => "CUSTOM",
        "no_play_day" => "NO-PLAY DAY",
        "no_play_after" => "NO PLAY AFTER",
        "loss_streak" => "LOSS STREAK",
        "max_games" => "MAX GAMES/DAY",
        "min_mental" => "MINIMUM MENTAL",
        _ => "CUSTOM"
    };

    public string ConditionText
    {
        get
        {
            if (string.IsNullOrEmpty(ConditionValue)) return "";
            return RuleType switch
            {
                "no_play_day" => $"Days: {ConditionValue}",
                "no_play_after" => FormatHour(ConditionValue),
                "loss_streak" => FormatLossStreak(ConditionValue),
                "max_games" => $"Max {ConditionValue} games per day",
                "min_mental" => $"Don't queue below mental {ConditionValue}",
                _ => ""
            };
        }
    }

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool HasCondition => !string.IsNullOrWhiteSpace(ConditionText);
    public bool HasViolationReason => IsViolated && !string.IsNullOrWhiteSpace(ViolationReason);
    public string ToggleText => IsActive ? "Disable" : "Enable";
    public bool IsCustom => RuleType == "custom";

    private static string FormatHour(string value)
    {
        if (!int.TryParse(value, out var h)) return "";
        var suffix = h < 12 ? "AM" : "PM";
        var displayH = h <= 12 ? h : h - 12;
        if (displayH == 0) displayH = 12;
        return $"No play after {displayH}:00 {suffix}";
    }

    private static string FormatLossStreak(string value)
    {
        var (threshold, cd) = RulesRepository.ParseLossStreakCondition(value);
        if (threshold <= 0) return "";
        if (cd is not int m || m <= 0) return $"Stop after {threshold} consecutive losses";
        var window = m >= 60 ? $"{m / 60}h{(m % 60 > 0 ? $" {m % 60}m" : "")}" : $"{m}m";
        return $"Stop after {threshold} losses (unlock after {window})";
    }
}

/// <summary>Display model for a violation banner entry.</summary>
public sealed class ViolationBannerItem
{
    public string RuleName { get; init; } = "";
    public string Reason { get; init; } = "";
}

/// <summary>A pre-built rule suggestion shown in the empty state.</summary>
public sealed class SuggestedRule
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string RuleType { get; init; } = "custom";
    public string ConditionValue { get; init; } = "";
    public string BadgeText { get; init; } = "";
    public string ConditionText { get; init; } = "";
}

/// <summary>ViewModel for the Rules page.</summary>
public partial class RulesViewModel : ObservableObject
{
    private readonly IRulesRepository _rulesRepo;
    private readonly IGameRepository _gameRepo;
    private readonly ISessionLogRepository _sessionLogRepo;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isCreating;

    [ObservableProperty]
    private bool _hasRules;

    [ObservableProperty]
    private bool _hasActiveRules;

    [ObservableProperty]
    private bool _hasInactiveRules;

    [ObservableProperty]
    private bool _hasViolations;

    // Create form fields
    [ObservableProperty]
    private string _newRuleName = "";

    [ObservableProperty]
    private int _newRuleTypeIndex; // index into RuleTypes list

    [ObservableProperty]
    private string _newConditionValue = "";

    [ObservableProperty]
    private string _newDescription = "";

    [ObservableProperty]
    private bool _canCreate;

    [ObservableProperty]
    private string _conditionLabel = "Condition";

    [ObservableProperty]
    private string _conditionPlaceholder = "";

    [ObservableProperty]
    private bool _showConditionField;

    // Second input only used by loss_streak: cooldown minutes (blank = rest-of-day).
    [ObservableProperty]
    private string _newLossStreakCooldown = "";

    [ObservableProperty]
    private bool _showLossStreakCooldown;

    // Non-null when the form is editing an existing rule; null when creating.
    [ObservableProperty]
    private long? _editingRuleId;

    [ObservableProperty]
    private string _formTitle = "New Rule";

    [ObservableProperty]
    private string _formSubmitLabel = "Create";

    public ObservableCollection<RuleDisplayItem> ActiveRules { get; } = new();
    public ObservableCollection<RuleDisplayItem> InactiveRules { get; } = new();
    public ObservableCollection<ViolationBannerItem> Violations { get; } = new();

    public IReadOnlyList<SuggestedRule> SuggestedRules { get; } =
    [
        new SuggestedRule
        {
            Name = "Stop after 2 losses",
            Description = "Tilt compounds quickly — two losses in a row is a good signal to take a break.",
            RuleType = "loss_streak",
            ConditionValue = "2",
            BadgeText = "LOSS STREAK",
            ConditionText = "Stop after 2 consecutive losses",
        },
        new SuggestedRule
        {
            Name = "Max 5 games per day",
            Description = "Marathon sessions rarely improve your play. Keep it focused.",
            RuleType = "max_games",
            ConditionValue = "5",
            BadgeText = "MAX GAMES/DAY",
            ConditionText = "Max 5 games per day",
        },
        new SuggestedRule
        {
            Name = "No ranked after midnight",
            Description = "Late-night games hurt decision-making and sleep quality.",
            RuleType = "no_play_after",
            ConditionValue = "0",
            BadgeText = "NO PLAY AFTER",
            ConditionText = "No play after 12:00 AM",
        },
        new SuggestedRule
        {
            Name = "Don't queue below mental 4",
            Description = "Playing on tilt is the fastest way to lose LP and reinforce bad habits.",
            RuleType = "min_mental",
            ConditionValue = "4",
            BadgeText = "MINIMUM MENTAL",
            ConditionText = "Don't queue below mental 4",
        },
    ];

    /// <summary>Rule type options for the ComboBox.</summary>
    public List<string> RuleTypeOptions { get; } =
    [
        "Custom",
        "No-Play Day",
        "No Play After",
        "Loss Streak",
        "Max Games/Day",
        "Minimum Mental",
    ];

    private static readonly string[] RuleTypeKeys =
    [
        "custom",
        "no_play_day",
        "no_play_after",
        "loss_streak",
        "max_games",
        "min_mental",
    ];

    public RulesViewModel(
        IRulesRepository rulesRepo,
        IGameRepository gameRepo,
        ISessionLogRepository sessionLogRepo)
    {
        _rulesRepo = rulesRepo;
        _gameRepo = gameRepo;
        _sessionLogRepo = sessionLogRepo;
    }

    partial void OnNewRuleNameChanged(string value)
    {
        CanCreate = !string.IsNullOrWhiteSpace(value);
    }

    partial void OnNewRuleTypeIndexChanged(int value)
    {
        UpdateConditionField(value);
    }

    private void UpdateConditionField(int typeIndex)
    {
        var typeKey = typeIndex >= 0 && typeIndex < RuleTypeKeys.Length
            ? RuleTypeKeys[typeIndex] : "custom";

        ShowLossStreakCooldown = typeKey == "loss_streak";

        switch (typeKey)
        {
            case "no_play_day":
                ShowConditionField = true;
                ConditionLabel = "Days (comma-separated)";
                ConditionPlaceholder = "e.g., Monday, Sunday";
                break;
            case "no_play_after":
                ShowConditionField = true;
                ConditionLabel = "Hour (0-23)";
                ConditionPlaceholder = "e.g., 23 for 11 PM";
                break;
            case "loss_streak":
                ShowConditionField = true;
                ConditionLabel = "Max consecutive losses";
                ConditionPlaceholder = "e.g., 3";
                break;
            case "max_games":
                ShowConditionField = true;
                ConditionLabel = "Max games per day";
                ConditionPlaceholder = "e.g., 5";
                break;
            case "min_mental":
                ShowConditionField = true;
                ConditionLabel = "Minimum mental rating";
                ConditionPlaceholder = "e.g., 4";
                break;
            default:
                ShowConditionField = false;
                ConditionLabel = "Condition";
                ConditionPlaceholder = "";
                break;
        }
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            await RefreshDataAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void ToggleCreateForm()
    {
        IsCreating = !IsCreating;
        if (!IsCreating)
        {
            ClearForm();
        }
        else
        {
            EditingRuleId = null;
            FormTitle = "New Rule";
            FormSubmitLabel = "Create";
        }
    }

    [RelayCommand]
    private void StartEditing(long ruleId)
    {
        var rule = FindRule(ruleId);
        if (rule is null) return;

        EditingRuleId = ruleId;
        FormTitle = "Edit Rule";
        FormSubmitLabel = "Save";

        NewRuleName = rule.Name;
        NewDescription = rule.Description;
        NewRuleTypeIndex = IndexForRuleType(rule.RuleType);

        if (rule.RuleType == "loss_streak")
        {
            var (threshold, cd) = RulesRepository.ParseLossStreakCondition(rule.ConditionValue);
            NewConditionValue = threshold > 0 ? threshold.ToString() : "";
            NewLossStreakCooldown = cd is int m && m > 0 ? m.ToString() : "";
        }
        else
        {
            NewConditionValue = rule.ConditionValue;
            NewLossStreakCooldown = "";
        }

        CanCreate = !string.IsNullOrWhiteSpace(NewRuleName);
        IsCreating = true;
    }

    private RuleDisplayItem? FindRule(long ruleId)
    {
        foreach (var r in ActiveRules) if (r.Id == ruleId) return r;
        foreach (var r in InactiveRules) if (r.Id == ruleId) return r;
        return null;
    }

    private static int IndexForRuleType(string ruleType)
    {
        for (var i = 0; i < RuleTypeKeys.Length; i++)
        {
            if (RuleTypeKeys[i] == ruleType) return i;
        }
        return 0;
    }

    [RelayCommand]
    private async Task CreateRuleAsync()
    {
        if (string.IsNullOrWhiteSpace(NewRuleName)) return;

        var typeKey = NewRuleTypeIndex >= 0 && NewRuleTypeIndex < RuleTypeKeys.Length
            ? RuleTypeKeys[NewRuleTypeIndex] : "custom";

        var conditionValue = NewConditionValue.Trim();
        if (typeKey == "loss_streak" && !string.IsNullOrWhiteSpace(NewLossStreakCooldown)
            && int.TryParse(NewLossStreakCooldown.Trim(), out var cd) && cd > 0)
        {
            conditionValue = $"{conditionValue}:{cd}";
        }

        if (EditingRuleId is long id)
        {
            await _rulesRepo.UpdateAsync(
                id,
                NewRuleName.Trim(),
                NewDescription.Trim(),
                typeKey,
                conditionValue);
        }
        else
        {
            await _rulesRepo.CreateAsync(
                NewRuleName.Trim(),
                NewDescription.Trim(),
                typeKey,
                conditionValue);
        }

        ClearForm();
        IsCreating = false;
        EditingRuleId = null;
        FormTitle = "New Rule";
        FormSubmitLabel = "Create";
        await RefreshDataAsync();
    }

    [RelayCommand]
    private async Task ToggleRuleAsync(long ruleId)
    {
        await _rulesRepo.ToggleAsync(ruleId);
        await RefreshDataAsync();
    }

    [RelayCommand]
    private async Task AddSuggestedRuleAsync(SuggestedRule suggestion)
    {
        await _rulesRepo.CreateAsync(
            suggestion.Name,
            suggestion.Description,
            suggestion.RuleType,
            suggestion.ConditionValue);
        await RefreshDataAsync();
    }

    [RelayCommand]
    private async Task DeleteRuleAsync(long ruleId)
    {
        await _rulesRepo.DeleteAsync(ruleId);
        await RefreshDataAsync();
    }

    private void ClearForm()
    {
        NewRuleName = "";
        NewRuleTypeIndex = 0;
        NewConditionValue = "";
        NewLossStreakCooldown = "";
        NewDescription = "";
    }

    private async Task RefreshDataAsync()
    {
        var allRules = await _rulesRepo.GetAllAsync();

        // Check violations for active rules
        var todaysGames = await _gameRepo.GetTodaysGamesAsync();
        var games = new List<RuleCheckGame>();
        foreach (var g in todaysGames)
        {
            games.Add(new RuleCheckGame(
                GameId: g.GameId,
                Win: g.Win,
                ChampionName: g.ChampionName,
                Timestamp: g.Timestamp));
        }

        var violations = await _rulesRepo.CheckViolationsAsync(games);
        var violationMap = new Dictionary<long, RuleViolation>();
        foreach (var v in violations)
        {
            violationMap[v.Rule.Id] = v;
        }

        ActiveRules.Clear();
        InactiveRules.Clear();
        Violations.Clear();

        foreach (var rule in allRules)
        {
            violationMap.TryGetValue(rule.Id, out var violation);

            var item = new RuleDisplayItem
            {
                Id = rule.Id,
                Name = rule.Name,
                Description = rule.Description,
                RuleType = rule.RuleType,
                ConditionValue = rule.ConditionValue,
                IsActive = rule.IsActive,
                IsViolated = violation?.Violated ?? false,
                ViolationReason = violation?.Reason ?? "",
                IsChecked = violation != null && rule.RuleType != "custom",
            };

            if (rule.IsActive)
            {
                ActiveRules.Add(item);
                if (item.IsViolated)
                {
                    Violations.Add(new ViolationBannerItem
                    {
                        RuleName = rule.Name,
                        Reason = violation!.Reason,
                    });
                }
            }
            else
            {
                InactiveRules.Add(item);
            }
        }

        HasActiveRules = ActiveRules.Count > 0;
        HasInactiveRules = InactiveRules.Count > 0;
        HasRules = HasActiveRules || HasInactiveRules;
        HasViolations = Violations.Count > 0;
    }
}

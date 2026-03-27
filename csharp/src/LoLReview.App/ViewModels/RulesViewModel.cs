#nullable enable

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoLReview.Core.Data.Repositories;

namespace LoLReview.App.ViewModels;

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
                "loss_streak" => $"Stop after {ConditionValue} consecutive losses",
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
}

/// <summary>Display model for a violation banner entry.</summary>
public sealed class ViolationBannerItem
{
    public string RuleName { get; init; } = "";
    public string Reason { get; init; } = "";
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

    public ObservableCollection<RuleDisplayItem> ActiveRules { get; } = new();
    public ObservableCollection<RuleDisplayItem> InactiveRules { get; } = new();
    public ObservableCollection<ViolationBannerItem> Violations { get; } = new();

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
    }

    [RelayCommand]
    private async Task CreateRuleAsync()
    {
        if (string.IsNullOrWhiteSpace(NewRuleName)) return;

        var typeKey = NewRuleTypeIndex >= 0 && NewRuleTypeIndex < RuleTypeKeys.Length
            ? RuleTypeKeys[NewRuleTypeIndex] : "custom";

        await _rulesRepo.CreateAsync(
            NewRuleName.Trim(),
            NewDescription.Trim(),
            typeKey,
            NewConditionValue.Trim());

        ClearForm();
        IsCreating = false;
        await RefreshDataAsync();
    }

    [RelayCommand]
    private async Task ToggleRuleAsync(long ruleId)
    {
        await _rulesRepo.ToggleAsync(ruleId);
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
        NewDescription = "";
    }

    private async Task RefreshDataAsync()
    {
        var allRules = await _rulesRepo.GetAllAsync();

        // Check violations for active rules
        var todaysGames = await _gameRepo.GetTodaysGamesAsync();
        var gamesDicts = new List<Dictionary<string, object?>>();
        foreach (var g in todaysGames)
        {
            gamesDicts.Add(new Dictionary<string, object?>
            {
                ["game_id"] = g.GameId,
                ["win"] = g.Win,
                ["champion_name"] = g.ChampionName,
                ["timestamp"] = g.Timestamp,
            });
        }

        var violations = await _rulesRepo.CheckViolationsAsync(gamesDicts);
        var violationMap = new Dictionary<long, RuleViolation>();
        foreach (var v in violations)
        {
            var ruleId = Convert.ToInt64(v.Rule.GetValueOrDefault("id") ?? 0);
            violationMap[ruleId] = v;
        }

        ActiveRules.Clear();
        InactiveRules.Clear();
        Violations.Clear();

        foreach (var rule in allRules)
        {
            var id = Convert.ToInt64(rule.GetValueOrDefault("id") ?? 0);
            var name = rule.GetValueOrDefault("name")?.ToString() ?? "";
            var description = rule.GetValueOrDefault("description")?.ToString() ?? "";
            var ruleType = rule.GetValueOrDefault("rule_type")?.ToString() ?? "custom";
            var conditionValue = rule.GetValueOrDefault("condition_value")?.ToString() ?? "";
            var isActive = Convert.ToBoolean(rule.GetValueOrDefault("is_active") ?? false);

            violationMap.TryGetValue(id, out var violation);

            var item = new RuleDisplayItem
            {
                Id = id,
                Name = name,
                Description = description,
                RuleType = ruleType,
                ConditionValue = conditionValue,
                IsActive = isActive,
                IsViolated = violation?.Violated ?? false,
                ViolationReason = violation?.Reason ?? "",
                IsChecked = violation != null && ruleType != "custom",
            };

            if (isActive)
            {
                ActiveRules.Add(item);
                if (item.IsViolated)
                {
                    Violations.Add(new ViolationBannerItem
                    {
                        RuleName = name,
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

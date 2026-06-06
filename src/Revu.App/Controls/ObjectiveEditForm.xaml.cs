#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Revu.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Revu.App.Controls;

/// <summary>
/// v2.18: shared objective create/edit form. Rendered both as the top-of-page
/// "New Objective" form and inline inside the card being edited (F4). Binds to
/// the host's <see cref="ObjectivesViewModel"/> (the page sets DataContext);
/// only one instance is visible at a time, so they share the VM's New* state.
/// Event handlers here just delegate to ViewModel commands — same behaviour as
/// when they lived on ObjectivesPage.
/// </summary>
public sealed partial class ObjectiveEditForm : UserControl
{
    public ObjectiveEditForm()
    {
        InitializeComponent();
    }

    /// <summary>The objectives view model, taken from the host's DataContext.</summary>
    public ObjectivesViewModel ViewModel => (ObjectivesViewModel)DataContext;

    private void RemovePromptDraftButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is PromptDraftItem draft)
        {
            ViewModel.RemovePromptCommand.Execute(draft);
        }
    }

    // v2.15.0: picked-chip remove handler (click an already-added chip to remove it).
    private void RemoveChampionChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string champ)
        {
            ViewModel.RemoveChampionCommand.Execute(champ);
        }
    }

    // v2.15.1: AutoSuggestBox type-to-filter replaces the champion-grid UI.
    private void ChampionSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // Only update the dropdown list when the text change is from user typing,
        // not programmatic (e.g. when we clear after a selection).
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;

        var query = (sender.Text ?? "").Trim();
        var source = ViewModel.AllChampionNames;
        if (source.Count == 0)
        {
            sender.ItemsSource = Array.Empty<string>();
            return;
        }

        // Hide champs the user already picked; filter the rest by substring
        // (case-insensitive). Prefix matches sort first so "Kai" shows Kai'Sa
        // above Kayle.
        var already = ViewModel.NewChampions
            .Select(n => n)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> filtered = source
            .Where(n => !already.Contains(n));

        if (!string.IsNullOrEmpty(query))
        {
            filtered = filtered
                .Where(n => n.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(n => n.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                .ThenBy(n => n, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            filtered = filtered.OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        }

        sender.ItemsSource = filtered.Take(50).ToList();
    }

    private void ChampionSearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string champ)
        {
            ViewModel.AddChampionCommand.Execute(champ);
            sender.Text = "";
        }
    }

    private void ChampionSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        // Enter pressed. If a suggestion was chosen use that; otherwise fall
        // back to the literal query (user typing a champ they haven't played).
        var name = args.ChosenSuggestion as string
                   ?? (args.QueryText ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        ViewModel.AddChampionCommand.Execute(name);
        sender.Text = "";
    }
}

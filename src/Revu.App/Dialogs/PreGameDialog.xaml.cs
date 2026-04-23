#nullable enable

using Revu.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Revu.App.Dialogs;

/// <summary>Pre-game focus dialog shown during champion select.</summary>
public sealed partial class PreGameDialog : ContentDialog
{
    public PreGameDialogViewModel ViewModel { get; }

    public PreGameDialog()
    {
        ViewModel = App.GetService<PreGameDialogViewModel>();
        InitializeComponent();
        Loaded += (_, _) => ViewModel.LoadCommand.Execute(null);
    }

    /// <summary>The focus text entered by the user.</summary>
    public string FocusText => ViewModel.FocusText;

    /// <summary>The selected pre-game mood (1-5, or 0 if not selected).</summary>
    public int SelectedMood => ViewModel.SelectedMood;

    /// <summary>The session intention (first game of the day).</summary>
    public string SessionIntention => ViewModel.SessionIntention;
}

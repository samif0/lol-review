#nullable enable

using Revu.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Revu.App.Dialogs;

/// <summary>Manual game entry dialog for games not auto-tracked.</summary>
public sealed partial class ManualEntryDialog : ContentDialog
{
    public ManualEntryDialogViewModel ViewModel { get; }

    public ManualEntryDialog()
    {
        ViewModel = App.GetService<ManualEntryDialogViewModel>();
        InitializeComponent();
    }

    /// <summary>Helper for error message visibility.</summary>
    public Visibility HasError =>
        string.IsNullOrWhiteSpace(ViewModel.ErrorMessage)
            ? Visibility.Collapsed
            : Visibility.Visible;

    /// <summary>
    /// Save the manual entry. Returns true if save succeeded.
    /// Called from the dialog service when PrimaryButton is clicked.
    /// </summary>
    public async Task<bool> TrySaveAsync() => await ViewModel.SaveAsync();
}

#nullable enable

using Revu.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Revu.App.Dialogs;

/// <summary>
/// Sidebar "Log in" modal: email → OTP → Riot ID + region, in one flow.
/// Reuses <see cref="LoginDialogViewModel"/> (same auth calls as onboarding).
/// </summary>
public sealed partial class LoginDialog : ContentDialog
{
    public LoginDialogViewModel ViewModel { get; }

    public LoginDialog()
    {
        ViewModel = App.GetService<LoginDialogViewModel>();
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.InitializeAsync();
    }

    /// <summary>True if the user completed the full flow (session + Riot ID).</summary>
    public bool Completed => ViewModel.Completed;
}

#nullable enable

using Revu.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Revu.App.Views;

/// <summary>
/// First-launch onboarding. Three-step state machine driven by
/// <see cref="OnboardingViewModel"/>. Exposes <see cref="Completed"/> so
/// the app host can swap in the shell when the user finishes or skips.
/// </summary>
public sealed partial class OnboardingPage : Page
{
    public OnboardingViewModel ViewModel { get; }

    /// <summary>Raised when the user finishes or skips the flow.</summary>
    public event Action? Completed;

    public OnboardingPage()
    {
        ViewModel = App.GetService<OnboardingViewModel>();
        InitializeComponent();
        ViewModel.Completed += () => Completed?.Invoke();
        Loaded += (_, _) =>
        {
            if (App.MainWindow is { } w && AppTitleBar is not null)
            {
                w.SetTitleBar(AppTitleBar);
            }
        };
    }

    // ── x:Bind helpers ──────────────────────────────────────────────

    public Visibility IsState(string current, string target)
        => string.Equals(current, target, System.StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility HasText(string? text)
        => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public bool Not(bool value) => !value;

    public string InviteToggleLabel(bool shown)
        => shown ? "Already have an account? Log in instead" : "Have an invite code? Sign up";

    /// <summary>Returns a highlighted brush for the selected role button, default otherwise.</summary>
    public Microsoft.UI.Xaml.Media.Brush RoleBrush(bool selected)
    {
        var key = selected ? "AccentBlueDimBrush" : "CardBackgroundBrush";
        return (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources[key];
    }
}

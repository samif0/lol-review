#nullable enable

using Microsoft.UI.Xaml.Controls;

namespace LoLReview.App.Contracts;

/// <summary>
/// Provides page navigation within the shell content frame.
/// </summary>
public interface INavigationService
{
    /// <summary>
    /// Initialize the service with the shell's content frame and NavigationView.
    /// Must be called once during shell setup.
    /// </summary>
    void Initialize(Frame frame, NavigationView? navigationView = null);

    /// <summary>
    /// Navigate to a page by its string key (e.g., "dashboard", "session", "settings").
    /// </summary>
    bool NavigateTo(string pageKey, object? parameter = null);

    /// <summary>
    /// Navigate to a page by its Type.
    /// </summary>
    bool NavigateTo<TPage>(object? parameter = null) where TPage : Microsoft.UI.Xaml.Controls.Page;

    /// <summary>
    /// Navigate back in the frame's back stack.
    /// </summary>
    void GoBack();

    /// <summary>
    /// Whether the frame can go back.
    /// </summary>
    bool CanGoBack { get; }

    /// <summary>
    /// The currently active page key.
    /// </summary>
    string? CurrentPageKey { get; }
}

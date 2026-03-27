#nullable enable

using Microsoft.UI.Xaml;

namespace LoLReview.App.Contracts;

/// <summary>
/// Manages the system tray (notification area) icon and context menu.
/// </summary>
public interface ISystemTrayService
{
    /// <summary>
    /// Initialize the tray icon with a reference to the main window.
    /// </summary>
    void Initialize(Window mainWindow);

    /// <summary>
    /// Update the tray icon status indicator (connected/disconnected).
    /// </summary>
    void UpdateStatus(bool connected);

    /// <summary>
    /// Show/activate the main window from the tray.
    /// </summary>
    void ShowWindow();

    /// <summary>
    /// Hide the main window to the tray.
    /// </summary>
    void HideWindow();
}

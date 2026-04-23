#nullable enable

using Revu.App.Contracts;
using Microsoft.UI.Xaml;

namespace Revu.App.Services;

/// <summary>
/// Stub system tray service. Tray library removed due to heap corruption.
/// TODO: Re-implement with a compatible tray library.
/// </summary>
public sealed class SystemTrayService : ISystemTrayService
{
    public void Initialize(Window mainWindow) { }
    public void UpdateStatus(bool connected) { }
    public void ShowWindow() { }
    public void HideWindow() { }
}

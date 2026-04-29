#nullable enable

using Revu.App.Contracts;
using Microsoft.UI.Xaml;

namespace Revu.App.Services;

/// <summary>
/// Stub system tray service. Tray library removed in v2.15.x due to
/// XamlControlsResources heap corruption — keeping a no-op stub so the
/// DI container resolves and the rest of the app behaves as if minimize-
/// to-tray simply isn't enabled. v2.17 backlog: re-implement with a
/// WinUI-3-compatible tray library, or drop the contract entirely if we
/// decide tray isn't worth the complexity for v1+.
/// </summary>
public sealed class SystemTrayService : ISystemTrayService
{
    public void Initialize(Window mainWindow) { }
    public void UpdateStatus(bool connected) { }
    public void ShowWindow() { }
    public void HideWindow() { }
}

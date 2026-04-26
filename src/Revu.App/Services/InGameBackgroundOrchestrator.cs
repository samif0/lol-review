#nullable enable

using System;
using CommunityToolkit.Mvvm.Messaging;
using Revu.App.Helpers;
using Revu.Core.Lcu;
using Revu.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;

namespace Revu.App.Services;

/// <summary>
/// v2.16.1: minimizes the window + suspends always-on UI animations during a
/// League game to cut steady-state GPU/CPU overhang while the user is in
/// match. Restores both on game end.
///
/// Subscribes to <see cref="GameStartedMessage"/> and
/// <see cref="GameEndedMessage"/>. Gated by
/// <see cref="IConfigService.MinimizeDuringGame"/> so users can opt out from
/// Settings → App Behavior.
///
/// Animation suspension is a single static-bool flip on
/// <see cref="SidebarEnergyDrainAnimator"/>; we capture the user's prior
/// value at game-start so restore returns to the configured state instead
/// of force-enabling.
/// </summary>
public sealed class InGameBackgroundOrchestrator
{
    private readonly IConfigService _config;
    private readonly ILogger<InGameBackgroundOrchestrator> _logger;
    private bool _wasMinimized;
    private bool _priorSidebarAnimationsEnabled = true;

    public InGameBackgroundOrchestrator(
        IConfigService config,
        ILogger<InGameBackgroundOrchestrator> logger)
    {
        _config = config;
        _logger = logger;

        WeakReferenceMessenger.Default.Register<InGameBackgroundOrchestrator, GameStartedMessage>(
            this, (recipient, _) => recipient.HandleGameStarted());

        WeakReferenceMessenger.Default.Register<InGameBackgroundOrchestrator, GameEndedMessage>(
            this, (recipient, _) => recipient.HandleGameEnded());
    }

    private void HandleGameStarted()
    {
        if (!_config.MinimizeDuringGame) return;

        DispatcherHelper.RunOnUIThread(() =>
        {
            try
            {
                _priorSidebarAnimationsEnabled = SidebarEnergyDrainAnimator.Enabled;
                SidebarEnergyDrainAnimator.Enabled = false;

                var window = App.MainWindow;
                if (window?.AppWindow?.Presenter is OverlappedPresenter op)
                {
                    op.Minimize();
                    _wasMinimized = true;
                }

                _logger.LogInformation("In-game: minimized window + suspended sidebar animations");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to minimize window on game start");
            }
        });
    }

    private void HandleGameEnded()
    {
        DispatcherHelper.RunOnUIThread(() =>
        {
            try
            {
                SidebarEnergyDrainAnimator.Enabled = _priorSidebarAnimationsEnabled;

                if (_wasMinimized)
                {
                    var window = App.MainWindow;
                    if (window?.AppWindow?.Presenter is OverlappedPresenter op)
                    {
                        op.Restore();
                    }
                    _wasMinimized = false;
                }

                _logger.LogInformation("Post-game: restored window + sidebar animations");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to restore window on game end");
            }
        });
    }
}

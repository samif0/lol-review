#nullable enable

using LoLReview.App.Contracts;
using LoLReview.App.Views;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace LoLReview.App.Services;

/// <summary>
/// Navigation service that maps page keys to page types and manages Frame navigation.
/// </summary>
public sealed class NavigationService : INavigationService
{
    private Frame? _frame;
    private NavigationView? _navigationView;
    private string? _currentPageKey;

    /// <summary>
    /// Maps navigation tags to page types.
    /// </summary>
    private static readonly Dictionary<string, Type> PageMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dashboard"] = typeof(DashboardPage),
        ["session"] = typeof(SessionLoggerPage),
        ["objectives"] = typeof(ObjectivesPage),
        ["rules"] = typeof(RulesPage),
        ["tiltcheck"] = typeof(TiltCheckPage),
        ["history"] = typeof(HistoryPage),
        ["analytics"] = typeof(AnalyticsPage),
        ["settings"] = typeof(SettingsPage),
        ["review"] = typeof(ReviewPage),
        ["vodplayer"] = typeof(VodPlayerPage),
        ["coachlab"] = typeof(CoachLabPage),
        ["pregame"] = typeof(PreGamePage),
        ["postgame"] = typeof(PostGamePage),
        ["manualentry"] = typeof(ManualEntryPage),
        ["objectivegames"] = typeof(ObjectiveGamesPage),
    };

    public string? CurrentPageKey => _currentPageKey;

    public bool CanGoBack => _frame?.CanGoBack ?? false;

    public void Initialize(Frame frame, NavigationView? navigationView = null)
    {
        _frame = frame;
        _navigationView = navigationView;
    }

    public bool NavigateTo(string pageKey, object? parameter = null)
    {
        if (_frame is null)
        {
            return false;
        }

        if (!PageMap.TryGetValue(pageKey, out var pageType))
        {
            return false;
        }

        // Don't navigate to the same page (skip for parameterized detail pages)
        if (_currentPageKey == pageKey && parameter is null && _frame.Content is not null)
        {
            return false;
        }

        var navigated = _frame.Navigate(
            pageType,
            parameter,
            new SlideNavigationTransitionInfo
            {
                Effect = SlideNavigationTransitionEffect.FromRight
            });

        if (navigated)
        {
            _currentPageKey = pageKey;
            SyncNavigationViewSelection(pageKey);
        }

        return navigated;
    }

    public bool NavigateTo<TPage>(object? parameter = null) where TPage : Page
    {
        var entry = PageMap.FirstOrDefault(kvp => kvp.Value == typeof(TPage));
        if (entry.Key is not null)
        {
            return NavigateTo(entry.Key, parameter);
        }

        // Direct navigation by type even if not in the map
        return _frame?.Navigate(typeof(TPage), parameter) ?? false;
    }

    public void GoBack()
    {
        if (_frame?.CanGoBack == true)
        {
            _frame.GoBack();

            // Try to sync the current page key after going back
            if (_frame.Content is Page page)
            {
                var entry = PageMap.FirstOrDefault(kvp => kvp.Value == page.GetType());
                if (entry.Key is not null)
                {
                    _currentPageKey = entry.Key;
                    SyncNavigationViewSelection(entry.Key);
                }
            }
        }
    }

    /// <summary>
    /// Keeps the NavigationView selection in sync when navigating programmatically.
    /// </summary>
    private void SyncNavigationViewSelection(string pageKey)
    {
        if (_navigationView is null) return;

        // Search in menu items
        foreach (var item in _navigationView.MenuItems)
        {
            if (item is NavigationViewItem navItem &&
                navItem.Tag is string tag &&
                string.Equals(tag, pageKey, StringComparison.OrdinalIgnoreCase))
            {
                _navigationView.SelectedItem = navItem;
                return;
            }
        }

        // Search in footer items
        foreach (var item in _navigationView.FooterMenuItems)
        {
            if (item is NavigationViewItem navItem &&
                navItem.Tag is string tag &&
                string.Equals(tag, pageKey, StringComparison.OrdinalIgnoreCase))
            {
                _navigationView.SelectedItem = navItem;
                return;
            }
        }
    }
}

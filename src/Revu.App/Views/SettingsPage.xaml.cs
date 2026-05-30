#nullable enable

using Revu.App.Helpers;
using Revu.App.Services;
using Revu.App.ViewModels;
using Revu.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Revu.App.Views;

/// <summary>Settings page -- app configuration, backup, and data management.</summary>
public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = App.GetService<SettingsViewModel>();
        InitializeComponent();
        Loaded += (_, _) =>
        {
            AnimationHelper.AnimatePageEnter(RootGrid);
        };
    }

    private void OnSettingsNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;

        var target = tag switch
        {
            "recordings" => RecordingsSection,
            "backups" => BackupsSection,
            "behavior" => BehaviorSection,
            "appearance" => AppearanceSection,
            "updates" => UpdatesSection,
            "account" => AccountSection,
            _ => null
        };

        ScrollToSection(target);
    }

    private void ScrollToSection(FrameworkElement? target)
    {
        if (target is null) return;

        var transform = target.TransformToVisual(SettingsScroll);
        var point = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
        var offset = Math.Max(0, SettingsScroll.VerticalOffset + point.Y - 16);
        SettingsScroll.ChangeView(null, offset, null, disableAnimation: false);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.LoadCommand.Execute(null);
        // v2.15.0: prime the restore-picker with available backups.
        await ViewModel.RefreshBackupsCommand.ExecuteAsync(null);

        // v2.17.8: deep-link support. Callers pass an x:Name string as the nav
        // parameter to scroll the matching card into view. Used by the VOD
        // viewer's auto-clipping hint banner ("Open Settings" link).
        if (e.Parameter is string anchorName && !string.IsNullOrWhiteSpace(anchorName))
        {
            // Defer until layout completes so BringIntoView has accurate metrics.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (FindName(anchorName) is FrameworkElement target)
                {
                    target.StartBringIntoView(new BringIntoViewOptions
                    {
                        AnimationDesired = true,
                        VerticalAlignmentRatio = 0.1,
                    });
                }
            });
        }
    }

    /// <summary>x:Bind helper — Visible when the given state string matches.</summary>
    public Visibility IsState(string current, string target)
        => string.Equals(current, target, System.StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>x:Bind helper — Visible when text is non-empty.</summary>
    public Visibility HasText(string? text)
        => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>x:Bind helper — logical NOT.</summary>
    public bool Not(bool value) => !value;

    private async void OnScanVodsClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            btn.IsEnabled = false;
            btn.Content = "Scanning...";
        }

        try
        {
            var vodService = App.GetService<IVodService>();

            // Show recording count for diagnostics
            var recordings = await vodService.FindRecordingsAsync();
            var matched = await vodService.AutoMatchRecordingsAsync();

            if (matched > 0)
            {
                ScanResultText.Text = $"Matched {matched} VOD(s) to games! ({recordings.Count} recordings found)";
            }
            else if (recordings.Count == 0)
            {
                ScanResultText.Text = "No video files found. Check that your Ascent folder is set and contains recordings.";
            }
            else
            {
                ScanResultText.Text = $"Found {recordings.Count} recordings but no new matches. Games may already be linked or outside the match window.";
            }
            ScanResultText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ScanResultText.Text = $"Scan failed: {ex.Message}";
            ScanResultText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(255, 211, 140, 144)); // #D38C90 negative
            ScanResultText.Visibility = Visibility.Visible;
        }
        finally
        {
            if (sender is Button btn2)
            {
                btn2.IsEnabled = true;
                btn2.Content = "Scan for VODs";
            }
        }
    }
}

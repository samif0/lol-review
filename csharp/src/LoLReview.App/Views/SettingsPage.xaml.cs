#nullable enable

using LoLReview.App.ViewModels;
using LoLReview.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LoLReview.App.Views;

/// <summary>Settings page -- app configuration, backup, and data management.</summary>
public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = App.GetService<SettingsViewModel>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.LoadCommand.Execute(null);
    }

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
            var matched = await vodService.AutoMatchRecordingsAsync();

            ScanResultText.Text = matched > 0
                ? $"Matched {matched} VOD(s) to games!"
                : "No new VODs matched. Check that your Ascent folder is correct.";
            ScanResultText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ScanResultText.Text = $"Scan failed: {ex.Message}";
            ScanResultText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(255, 239, 68, 68));
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

#nullable enable

using LoLReview.App.Helpers;
using Microsoft.UI.Xaml;

namespace LoLReview.App.Startup;

internal sealed class AppResourcesStartupTask : IUiThreadStartupTask
{
    public string Name => "app-resources";

    public Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Application.Current.Resources.MergedDictionaries.Add(new Microsoft.UI.Xaml.Controls.XamlControlsResources());
        }
        catch (Exception exception)
        {
            AppDiagnostics.WriteVerbose("startup.log", $"XamlControlsResources load failed: {exception.Message}");
        }

        try
        {
            Application.Current.Resources.MergedDictionaries.Add(
                new ResourceDictionary { Source = new Uri("ms-appx:///Themes/AppTheme.xaml") });
        }
        catch (Exception exception)
        {
            AppDiagnostics.WriteVerbose("startup.log", $"AppTheme load failed: {exception.Message}");
        }

        return Task.CompletedTask;
    }
}

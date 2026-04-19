#nullable enable

using LoLReview.App.Helpers;
using LoLReview.App.Services;
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

        // Register the FontSizes singleton as an application resource so
        // XAML can bind via {Binding <prop>, Source={StaticResource FontSizes}}.
        // We register it at app level rather than page level so it's
        // reachable from every Page's resource-lookup tree, including
        // inside DataTemplates where x:Bind can't reach static singletons.
        try
        {
            Application.Current.Resources["FontSizes"] = FontSizes.Instance;
        }
        catch (Exception exception)
        {
            AppDiagnostics.WriteVerbose("startup.log", $"FontSizes registration failed: {exception.Message}");
        }

        return Task.CompletedTask;
    }
}

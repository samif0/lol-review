#nullable enable

using System.Diagnostics;
using Windows.Storage.Pickers;

namespace Revu.App.Helpers;

internal static class MarkdownExportPicker
{
    public static async Task<string?> PickSavePathAsync(string suggestedFileName)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = suggestedFileName
            };
            picker.FileTypeChoices.Add("Markdown", [".md"]);

            var hwnd = App.MainWindow is not null
                ? WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow)
                : Process.GetCurrentProcess().MainWindowHandle;

            if (hwnd == nint.Zero)
            {
                return null;
            }

            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            return file?.Path;
        }
        catch
        {
            return null;
        }
    }
}

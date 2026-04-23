#nullable enable

using System.Runtime.InteropServices;

namespace Revu.App.Helpers;

internal static class WindowActivationHelper
{
    private const int SwRestore = 9;
    private const int SwShow = 5;

    public static void BringMainWindowToFront()
    {
        var window = App.MainWindow;
        if (window is null)
        {
            return;
        }

        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero)
            {
                window.Activate();
                return;
            }

            if (IsIconic(hwnd))
            {
                ShowWindow(hwnd, SwRestore);
            }
            else
            {
                ShowWindow(hwnd, SwShow);
            }

            SetForegroundWindow(hwnd);
            window.Activate();
        }
        catch
        {
            try
            {
                window.Activate();
            }
            catch
            {
                // Best effort only.
            }
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);
}

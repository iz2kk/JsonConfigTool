#if WINDOWS
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using WinRT.Interop;

namespace ConfigTool.Platforms.Windows;

public static class WindowsWindowSizer
{
    private const double StartupWidthRatio = 0.80d;
    private const double StartupHeightRatio = 0.90d;
    private const int MinimumWidth = 960;
    private const int MinimumHeight = 640;

    public static void ApplyStartupBounds(Microsoft.UI.Xaml.Window nativeWindow)
    {
        if (nativeWindow is null)
        {
            return;
        }

        // Queue once so WinUI has already created the AppWindow and selected the real monitor/work area.
        if (nativeWindow.DispatcherQueue?.TryEnqueue(() => ApplyStartupBoundsCore(nativeWindow)) != true)
        {
            ApplyStartupBoundsCore(nativeWindow);
        }
    }

    private static void ApplyStartupBoundsCore(Microsoft.UI.Xaml.Window nativeWindow)
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(nativeWindow);
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
            var workArea = displayArea.WorkArea; // WorkArea excludes the Windows taskbar.

            var targetWidth = Clamp((int)Math.Round(workArea.Width * StartupWidthRatio), MinimumWidth, workArea.Width);
            var targetHeight = Clamp((int)Math.Round(workArea.Height * StartupHeightRatio), MinimumHeight, workArea.Height);

            // Center on the usable screen area, not the raw monitor bounds, so taskbar is respected.
            var targetX = workArea.X + ((workArea.Width - targetWidth) / 2);
            var targetY = workArea.Y + ((workArea.Height - targetHeight) / 2);

            appWindow.MoveAndResize(new RectInt32(targetX, targetY, targetWidth, targetHeight));

            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = true;
                presenter.IsMaximizable = true;
                presenter.IsMinimizable = true;
            }
        }
        catch
        {
            // Window sizing must never block app startup; MAUI/WinUI can continue with default bounds.
        }
    }

    private static int Clamp(int value, int min, int max)
    {
        if (max <= 0)
        {
            return value;
        }

        return Math.Min(Math.Max(value, Math.Min(min, max)), max);
    }
}
#endif

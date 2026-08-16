using System.Drawing;
using System.Runtime.InteropServices;

namespace TwitchChatOverlay.UI.Overlay;

/// <summary>
/// Win32 interop for the overlay window. Two responsibilities live here: applying the
/// extended window styles that make the overlay safe to show over a fullscreen game,
/// and reading per-monitor DPI so anchor math stays correct on mixed-DPI setups.
/// </summary>
internal static class WindowStyleHelper
{
    private const int GWL_EXSTYLE = -20;

    private const long WS_EX_LAYERED = 0x00080000;
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const long WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static long GetWindowExStyle(IntPtr hWnd)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, GWL_EXSTYLE).ToInt64()
            : GetWindowLong32(hWnd, GWL_EXSTYLE);
    }

    private static void SetWindowExStyle(IntPtr hWnd, long exStyle)
    {
        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr64(hWnd, GWL_EXSTYLE, new IntPtr(exStyle));
        }
        else
        {
            SetWindowLong32(hWnd, GWL_EXSTYLE, unchecked((int)exStyle));
        }
    }

    /// <summary>
    /// WS_EX_NOACTIVATE is the one that matters most here: without it, showing the card
    /// steals focus from a borderless-fullscreen game and minimizes it — the classic reason
    /// homemade overlays get abandoned after the first day. WS_EX_TRANSPARENT makes every
    /// click pass through to the game; WS_EX_LAYERED enables per-pixel alpha; WS_EX_TOOLWINDOW
    /// keeps the window out of Alt+Tab and the taskbar.
    /// </summary>
    public static void ApplyOverlayStyles(IntPtr hwnd)
    {
        var exStyle = GetWindowExStyle(hwnd);
        exStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        SetWindowExStyle(hwnd, exStyle);
    }

    /// <summary>
    /// Toggles only WS_EX_TRANSPARENT. Setup mode needs the window to receive mouse input so
    /// it can be dragged; everything else — including WS_EX_NOACTIVATE — stays untouched, so
    /// dragging the card still never steals focus from the game.
    /// </summary>
    public static void SetClickThrough(IntPtr hwnd, bool clickThrough)
    {
        var exStyle = GetWindowExStyle(hwnd);
        exStyle = clickThrough
            ? exStyle | WS_EX_TRANSPARENT
            : exStyle & ~WS_EX_TRANSPARENT;
        SetWindowExStyle(hwnd, exStyle);
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    /// <summary>Cursor position in device pixels. Used during a setup-mode drag, where the
    /// window's own mouse coordinates are awkward to convert while the window is moving.</summary>
    public static (int X, int Y) GetCursorPosition() =>
        GetCursorPos(out var point) ? (point.X, point.Y) : (0, 0);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

    private enum MonitorDpiType
    {
        EffectiveDpi = 0
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>DPI scale (1.0 = 96 DPI) for the monitor containing the given device-pixel bounds.</summary>
    public static double GetDpiScaleForBounds(Rectangle bounds)
    {
        var rect = new RECT { Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom };
        var hMonitor = MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST);
        if (hMonitor != IntPtr.Zero && GetDpiForMonitor(hMonitor, MonitorDpiType.EffectiveDpi, out var dpiX, out _) == 0)
        {
            return dpiX / 96.0;
        }

        return 1.0;
    }
}

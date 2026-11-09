using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DevicePower.Taskbar;

/// <summary>
/// 前景窗口有没有铺满它自己那块屏。QUNS 只在 DX 独占全屏时报 3，英雄联盟这类
/// "无边框全屏"始终停在 5，所以必须直接量前台窗口的矩形。
/// </summary>
internal static class FullscreenWatcher
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    /// <summary>桌面、任务栏、外壳浮层本来就占满整块屏，不能算全屏应用。</summary>
    private static readonly string[] ShellClasses =
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
        "Taskband_OverflowWindow",
        "Windows.UI.Core.CoreWindow",
        "TopLevelWindowForOverflowXamlIsland",
        "WindowsDashboard",
    };

    public static bool IsCovering(IntPtr self) => CoveringReason(self) != null;

    /// <summary>null = 没有全屏窗口；否则返回「类名 宽x高」，直接写进 dock.log 好远程认窗口。</summary>
    public static string CoveringReason(IntPtr self)
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == self) return null;

        string className = ClassName(foreground);
        foreach (string shell in ShellClasses)
        {
            if (string.Equals(shell, className, StringComparison.Ordinal)) return null;
        }

        if (!GetWindowRect(foreground, out RECT window)) return null;
        MONITORINFO on = Info(MonitorFromWindow(foreground, 2 /* TONEAREST */));
        if (!Covers(window, on.rcMonitor)) return null;

        if (self != IntPtr.Zero)
        {
            // 副屏全屏不该把主屏的电量 UI 关掉
            MONITORINFO ours = Info(MonitorFromWindow(self, 2));
            if (!SameMonitor(on.rcMonitor, ours.rcMonitor)) return null;
        }

        return className + " " + (window.Right - window.Left) + "x" + (window.Bottom - window.Top);
    }

    private static bool Covers(RECT window, RECT monitor) =>
        window.Left <= monitor.Left && window.Top <= monitor.Top &&
        window.Right >= monitor.Right && window.Bottom >= monitor.Bottom;

    private static bool SameMonitor(RECT a, RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    private static string ClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(64);
        int length = GetClassName(hwnd, buffer, buffer.Capacity);
        return length <= 0 ? "" : buffer.ToString(0, length);
    }

    private static MONITORINFO Info(IntPtr monitor)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
        if (monitor != IntPtr.Zero) GetMonitorInfo(monitor, ref info);
        return info;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}

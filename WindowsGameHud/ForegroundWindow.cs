// SPDX-License-Identifier: AGPL-3.0-only
// Added 2026-11-12 for the local Windows gaming HUD.
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SapphireGameHud;

internal readonly record struct ForegroundWindow(int ProcessId, string Name, nint Monitor, bool IsFullscreen,
    string? Executable = null, string? PackageFamily = null);

internal static class Desktop
{
    private const uint NearestMonitor = 2;
    private const int ExtendedFrameBounds = 9;
    private const int Style = -16;
    private const int ExtendedStyle = -20;
    private const int Caption = 0x00C00000;
    private const int ExTransparent = 0x20;
    private const int ExToolWindow = 0x80;
    private const int ExAppWindow = 0x40000;
    private const int ExNoActivate = 0x08000000;
    private const int ProcessQueryLimitedInformation = 0x1000;

    internal const int HotkeyMessage = 0x0312;
    internal const int HotkeyId = 1;
    internal const int IslandHotkeyId = 2;

    internal static nint ForegroundHandle => GetForegroundWindow();
    internal static void RestoreForeground(nint window)
    {
        if (window != 0) SetForegroundWindow(window);
    }

    internal static ForegroundWindow ReadForeground(ForegroundWindow previous = default)
    {
        var window = GetForegroundWindow();
        var monitor = MonitorFromWindow(window, NearestMonitor);
        if (window == 0) return new(0, "Desktop", monitor, false);

        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0 || processId == Environment.ProcessId || window == GetShellWindow())
            return new(0, "Desktop", monitor, false);

        var className = WindowClass(window);
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return new(0, "Desktop", monitor, false);
        if (className == "ApplicationFrameWindow")
            processId = FramedAppProcess(window, processId);

        var name = previous.Name;
        var executable = previous.Executable;
        var packageFamily = previous.PackageFamily;
        if (processId != previous.ProcessId)
        {
            (executable, packageFamily) = ReadIdentity(processId);
            try
            {
                using var process = Process.GetProcessById(processId);
                name = process.ProcessName;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
            {
                return new(0, "Desktop", monitor, false);
            }
        }

        var maximizedWithCaption = IsZoomed(window) && (GetWindowLong(window, Style) & Caption) == Caption;
        var bounds = ReadMonitor(monitor);
        var hasFrame = DwmGetWindowAttribute(window, ExtendedFrameBounds, out var frame, Marshal.SizeOf<Rect>()) == 0
            || GetWindowRect(window, out frame);
        var fullscreen = hasFrame && bounds.Width > 0 && !maximizedWithCaption && CoversMonitor(frame, bounds);
        return new(processId, name, monitor, fullscreen, executable, packageFamily);
    }

    private static string WindowClass(nint window)
    {
        var name = new StringBuilder(128);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }

    private static int FramedAppProcess(nint window, int hostProcess)
    {
        var appProcess = hostProcess;
        EnumChildWindows(window, (child, _) =>
        {
            if (WindowClass(child) != "Windows.UI.Core.CoreWindow") return true;
            GetWindowThreadProcessId(child, out var processId);
            if (processId == 0 || processId == hostProcess) return true;
            appProcess = processId;
            return false;
        }, 0);
        return appProcess;
    }

    private static (string? Executable, string? PackageFamily) ReadIdentity(int processId)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid) return (null, null);
        var path = new StringBuilder(32768);
        var size = path.Capacity;
        var executable = QueryFullProcessImageName(process, 0, path, ref size) ? path.ToString() : null;
        var packageName = new StringBuilder(128);
        var packageLength = packageName.Capacity;
        var family = GetPackageFamilyName(process, ref packageLength, packageName) == 0 ? packageName.ToString() : null;
        return (executable, family);
    }

    internal static bool CoversMonitor(Rect window, Rect monitor)
    {
        const int tolerance = 3;
        return window.Left <= monitor.Left + tolerance && window.Top <= monitor.Top + tolerance
            && window.Right >= monitor.Right - tolerance && window.Bottom >= monitor.Bottom - tolerance
            && Math.Abs(window.Width - monitor.Width) <= 2 * tolerance
            && Math.Abs(window.Height - monitor.Height) <= 2 * tolerance;
    }

    internal static void ConfigureOverlay(nint window, bool clickThrough)
    {
        var style = GetWindowLong(window, ExtendedStyle);
        var desired = (style | ExToolWindow | ExNoActivate) & ~ExAppWindow;
        desired = clickThrough ? desired | ExTransparent : desired & ~ExTransparent;
        if (desired != style) SetWindowLong(window, ExtendedStyle, desired);
    }

    internal static bool ObserveClipboard(nint window) => AddClipboardFormatListener(window);
    internal static void StopObservingClipboard(nint window) => RemoveClipboardFormatListener(window);

    internal static void CenterAtTop(nint window, nint monitor, int topOffset = 0)
    {
        var bounds = ReadMonitor(monitor != 0 ? monitor : MonitorFromWindow(window, NearestMonitor));
        if (bounds.Width <= 0 || !GetWindowRect(window, out var frame)) return;
        var x = bounds.Left + (bounds.Width - frame.Width) / 2;
        var y = bounds.Top + topOffset;
        if (x == frame.Left && y == frame.Top) return;

        const uint noSize = 0x0001;
        const uint noActivate = 0x0010;
        SetWindowPos(window, new nint(-1), x, y, 0, 0, noSize | noActivate);
    }

    internal static bool CursorOver(nint window, double visibleWidth, double top, double height, double radius = 0)
    {
        if (!GetCursorPos(out var point) || !GetWindowRect(window, out var frame)) return false;
        var scale = GetDpiForWindow(window) / 96d;
        if (scale <= 0) return false;
        var left = frame.Left + (frame.Width - visibleWidth * scale) / 2;
        var x = (point.X - left) / scale;
        var y = (point.Y - frame.Top) / scale - top;
        if (x < 0 || x >= visibleWidth || y < 0 || y >= height) return false;
        var dx = x - Math.Clamp(x, radius, visibleWidth - radius);
        var dy = y - Math.Clamp(y, radius, height - radius);
        return dx * dx + dy * dy <= radius * radius;
    }

    internal static bool ClickedOutside(nint window, System.Windows.Rect bounds, double scale, System.Windows.Media.Geometry? outline)
    {
        // Observe the current mouse-button state without taking capture from the target window.
        const int leftButton = 1;
        const int rightButton = 2;
        if (GetAsyncKeyState(leftButton) >= 0 && GetAsyncKeyState(rightButton) >= 0) return false;
        if (outline is null || !GetCursorPos(out var cursor) || !GetWindowRect(window, out var frame)) return false;
        var dpiScale = GetDpiForWindow(window) / 96d * scale;
        if (dpiScale <= 0) return false;
        var left = frame.Left + (frame.Width - bounds.Width * dpiScale) / 2;
        return !outline.FillContains(new System.Windows.Point((cursor.X - left) / dpiScale,
            (cursor.Y - frame.Top) / dpiScale - bounds.Top));
    }

    internal static double MonitorWidth(nint window, nint monitor)
    {
        var bounds = ReadMonitor(monitor != 0 ? monitor : MonitorFromWindow(window, NearestMonitor));
        var dpi = GetDpiForWindow(window);
        return dpi == 0 ? 0 : bounds.Width * 96d / dpi;
    }

    internal static bool RegisterToggle(nint window) => RegisterHotKey(window, HotkeyId, 0x4003, 0x79);
    internal static void UnregisterToggle(nint window) => UnregisterHotKey(window, HotkeyId);
    internal static bool RegisterIslandToggle(nint window) => RegisterHotKey(window, IslandHotkeyId, 0x4003, 0x78);
    internal static void UnregisterIslandToggle(nint window) => UnregisterHotKey(window, IslandHotkeyId);

    internal static void AllowActivation(nint window)
    {
        var style = GetWindowLong(window, ExtendedStyle);
        SetWindowLong(window, ExtendedStyle, style & ~ExNoActivate);
    }

    private static Rect ReadMonitor(nint monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(monitor, ref info) ? info.Monitor : default;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left, Top, Right, Bottom;
        internal readonly int Width => Right - Left;
        internal readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal int Size;
        internal Rect Monitor;
        internal Rect Work;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        internal int X, Y;
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("kernel32.dll")] private static extern SafeProcessHandle OpenProcess(int access, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(SafeProcessHandle process, ref int length, StringBuilder packageFamilyName);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out int processId);
    private delegate bool EnumChildCallback(nint child, nint data);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumChildCallback callback, nint data);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int length);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(nint window);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(nint window);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out Rect rect, int size);
}

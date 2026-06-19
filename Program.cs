using System.Drawing;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: InternalsVisibleTo("TaskbarOpacityController.Tests")]

namespace TaskbarOpacityController;

internal static class ShellState
{
    public static bool IsIgnoredShellClass(string className)
    {
        return className is "Progman"
            or "WorkerW"
            or "Shell_TrayWnd"
            or "Shell_SecondaryTrayWnd"
            or "TrayNotifyWnd"
            or "TrayClockWClass"
            or "MSTaskListWClass"
            or "ReBarWindow32";
    }

    public static bool IsDesktopClass(string className)
    {
        return className is "Progman"
            or "WorkerW"
            or "Shell_TrayWnd"
            or "Shell_SecondaryTrayWnd";
    }

    public static bool IsActiveShellClass(string className)
    {
        return className is "Windows.UI.Core.CoreWindow"
            or "Windows.UI.Composition.DesktopWindowContentBridge"
            or "XamlExplorerHostIslandWindow"
            or "MultitaskingViewFrame"
            or "TaskSwitcherWnd"
            or "SearchPane"
            or "SearchAppWindow"
            or "WindowsSearchBox"
            or "SearchHome"
            or "StartMenu"
            or "NotifyIconOverflowWindow"
            or "DV2ControlHost";
    }
}

internal enum ShellActivityState
{
    Desktop,
    Active
}

internal readonly record struct WindowSnapshot(
    string ClassName,
    bool IsVisible,
    bool IsMinimized,
    bool IsCloaked,
    int ExStyle,
    bool HasOwner,
    string ProcessName = "",
    int TextLength = 1,
    bool IsEffectivelyHidden = false);

internal static class WindowClassifier
{
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;

    public static ShellActivityState ResolveShellState(string className, bool hasVisibleApplicationWindows)
    {
        if (ShellState.IsDesktopClass(className))
        {
            return ShellActivityState.Desktop;
        }

        return ShellState.IsActiveShellClass(className) || hasVisibleApplicationWindows
            ? ShellActivityState.Active
            : ShellActivityState.Desktop;
    }

    public static bool IsApplicationWindow(WindowSnapshot window)
    {
        if (!window.IsVisible
            || window.IsMinimized
            || window.IsCloaked
            || window.IsEffectivelyHidden)
        {
            return false;
        }

        if (ShellState.IsIgnoredShellClass(window.ClassName)
            || ShellState.IsActiveShellClass(window.ClassName)
            || IsIgnoredBackgroundProcess(window.ProcessName))
        {
            return false;
        }

        if ((window.ExStyle & WsExToolWindow) != 0 && (window.ExStyle & WsExAppWindow) == 0)
        {
            return false;
        }

        if (window.HasOwner && (window.ExStyle & WsExAppWindow) == 0)
        {
            return false;
        }

        return (window.ExStyle & WsExAppWindow) != 0 || window.TextLength > 0;
    }

    internal static bool IsIgnoredBackgroundProcess(string processName)
    {
        return processName.Contains("typeless", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class TaskbarController
{
    private const int GwlExStyle = -20;
    private const int LwaAlpha = 0x00000002;
    private const int WsExLayered = 0x00080000;
    private const int MinimumHoverBandPixels = 40;

    private IntPtr taskbar;
    private byte? currentAlpha;
    private int hoverBandPixels = MinimumHoverBandPixels;

    public void Prime()
    {
        RefreshMetrics();
    }

    public void RefreshMetrics()
    {
        var hwnd = GetTaskbar();
        if (hwnd != IntPtr.Zero)
        {
            RefreshHoverBandPixels(hwnd);
        }
    }

    public void SetAlpha(byte alpha)
    {
        if (currentAlpha == alpha)
        {
            return;
        }

        var hwnd = GetTaskbar();
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetLayeredWindowAttributes(hwnd, 0, alpha, LwaAlpha);
        currentAlpha = alpha;
    }

    public void Restore()
    {
        currentAlpha = null;
        SetAlpha(255);
    }

    public int GetHoverBandPixels()
    {
        return hoverBandPixels;
    }

    private IntPtr GetTaskbar()
    {
        if (taskbar != IntPtr.Zero && NativeMethods.IsWindow(taskbar))
        {
            return taskbar;
        }

        taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        currentAlpha = null;

        if (taskbar == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var style = NativeMethods.GetWindowLong(taskbar, GwlExStyle);
        if ((style & WsExLayered) == 0)
        {
            NativeMethods.SetWindowLong(taskbar, GwlExStyle, style | WsExLayered);
        }

        RefreshHoverBandPixels(taskbar);

        return taskbar;
    }

    private void RefreshHoverBandPixels(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return;
        }

        var height = Math.Max(0, rect.Bottom - rect.Top);
        hoverBandPixels = Math.Max(MinimumHoverBandPixels, height + 8);
    }
}

internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TaskbarOpacityController";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value
            && string.Equals(value, GetStartupCommand(), StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

        if (key is null)
        {
            throw new InvalidOperationException("Unable to open the Windows startup registry key.");
        }

        if (enabled)
        {
            key.SetValue(ValueName, GetStartupCommand(), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    private static string GetStartupCommand()
    {
        return $"\"{Application.ExecutablePath}\"";
    }
}

internal static class LocalizedText
{
    private static readonly bool UseChinese =
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    public static string AppName => UseChinese ? "任务栏透明控制器" : "Taskbar Dock Controller";

    public static string RunningStatus => UseChinese
        ? "任务栏透明控制器：运行中"
        : "Taskbar Dock Controller: Running";

    public static string ShowTaskbarNow => UseChinese
        ? "立即显示任务栏"
        : "Show taskbar now";

    public static string StartWithWindows => UseChinese
        ? "开机自启动"
        : "Start with Windows";

    public static string Exit => UseChinese ? "退出" : "Exit";

    public static string StartupErrorTitle => AppName;

    public static string StartupErrorMessage(string detail)
    {
        return UseChinese
            ? $"更新开机自启动设置失败：\n\n{detail}"
            : $"Failed to update startup setting:\n\n{detail}";
    }
}

internal static class Program
{
    private const int GwlExStyle = -20;
    private const int GwOwner = 4;
    private const int DwmwaCloaked = 14;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutofcontext = 0x0000;
    private const int TickIntervalMs = 200;
    private const int EdgeTolerancePixels = 8;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const int VkD = 0x44;
    private const int VkTab = 0x09;
    private const int VkMenu = 0x12;
    private static readonly TimeSpan DesktopHold = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan AppWindowScanInterval = TimeSpan.FromMilliseconds(1000);

    private static readonly TaskbarController Taskbar = new();
    private static readonly Rectangle[] EmptyScreenBounds = Array.Empty<Rectangle>();
    private static readonly Dictionary<uint, bool> ignoredProcessCache = new();

    private static NativeMethods.WinEventDelegate? hookDelegate;
    private static IntPtr foregroundHook;
    private static NotifyIcon? notifyIcon;
    private static System.Windows.Forms.Timer? timer;
    private static DateTime forceDesktopUntilUtc = DateTime.MinValue;
    private static DateTime lastAppWindowScanUtc = DateTime.MinValue;
    private static bool isDesktop;
    private static bool isActiveShellUi;
    private static bool hasVisibleAppWindow;
    private static bool isCleaningUp;
    private static bool suppressStartupToggle;
    [ThreadStatic]
    private static StringBuilder? classNameBuffer;
    private static Rectangle[] cachedScreenBounds = EmptyScreenBounds;

    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        hookDelegate = WinEventCallback;
        foregroundHook = NativeMethods.SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            IntPtr.Zero,
            hookDelegate,
            0,
            0,
            WineventOutofcontext);

        Application.ApplicationExit += (_, _) => Cleanup();
        Application.ThreadExit += (_, _) => Cleanup();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        SetupTrayIcon();
        RefreshScreenCache();
        Taskbar.Prime();
        RefreshForegroundState(NativeMethods.GetForegroundWindow());
        RefreshVisibleAppWindowState();
        ApplyState();

        timer = new System.Windows.Forms.Timer { Interval = TickIntervalMs };
        timer.Tick += (_, _) => Tick();
        timer.Start();

        Application.Run();
    }

    private static void SetupTrayIcon()
    {
        var menu = new ContextMenuStrip();

        var statusItem = new ToolStripMenuItem(LocalizedText.RunningStatus)
        {
            Enabled = false
        };

        var showItem = new ToolStripMenuItem(LocalizedText.ShowTaskbarNow);
        showItem.Click += (_, _) => Taskbar.SetAlpha(255);

        var startupItem = new ToolStripMenuItem(LocalizedText.StartWithWindows)
        {
            CheckOnClick = true,
            Checked = StartupManager.IsEnabled()
        };
        startupItem.CheckedChanged += (_, _) =>
        {
            if (suppressStartupToggle)
            {
                return;
            }

            try
            {
                StartupManager.SetEnabled(startupItem.Checked);
            }
            catch (Exception ex)
            {
                suppressStartupToggle = true;
                startupItem.Checked = !startupItem.Checked;
                suppressStartupToggle = false;

                MessageBox.Show(
                    LocalizedText.StartupErrorMessage(ex.Message),
                    LocalizedText.StartupErrorTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        };

        var exitItem = new ToolStripMenuItem(LocalizedText.Exit);
        exitItem.Click += (_, _) => Application.ExitThread();

        menu.Items.Add(statusItem);
        menu.Items.Add(showItem);
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        notifyIcon = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
            Text = LocalizedText.AppName,
            ContextMenuStrip = menu,
            Visible = true
        };
    }

    private static void Tick()
    {
        RefreshForegroundState(NativeMethods.GetForegroundWindow());
        if (DateTime.UtcNow - lastAppWindowScanUtc >= AppWindowScanInterval)
        {
            RefreshVisibleAppWindowState();
        }

        if (IsWinKeyPressed() && IsKeyDown(VkD))
        {
            forceDesktopUntilUtc = DateTime.UtcNow + DesktopHold;
            isDesktop = true;
            isActiveShellUi = false;
            hasVisibleAppWindow = false;
        }

        ApplyState();
    }

    private static void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        RefreshScreenCache();
        Taskbar.RefreshMetrics();
    }

    private static void RefreshScreenCache()
    {
        var screens = Screen.AllScreens;
        var bounds = new Rectangle[screens.Length];

        for (var i = 0; i < screens.Length; i++)
        {
            bounds[i] = screens[i].Bounds;
        }

        cachedScreenBounds = bounds;
    }

    private static void ApplyState()
    {
        var now = DateTime.UtcNow;
        var hover = IsCursorInBottomBand();

        var shouldShow = now >= forceDesktopUntilUtc
            && (hover
                || IsWinKeyPressed()
                || IsAltTabPressed()
                || isActiveShellUi
                || hasVisibleAppWindow
                || !isDesktop);

        Taskbar.SetAlpha(shouldShow ? (byte)255 : (byte)0);
    }

    private static void WinEventCallback(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        RefreshForegroundState(hwnd);
        ApplyState();
    }

    private static void RefreshVisibleAppWindowState()
    {
        hasVisibleAppWindow = HasVisibleNormalAppWindow();
        lastAppWindowScanUtc = DateTime.UtcNow;
    }

    private static bool HasVisibleNormalAppWindow()
    {
        var found = false;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (IsVisibleNormalAppWindow(hwnd))
            {
                found = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    private static bool IsVisibleNormalAppWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero
            || !NativeMethods.IsWindow(hwnd)
            || !NativeMethods.IsWindowVisible(hwnd)
            || NativeMethods.IsIconic(hwnd))
        {
            return false;
        }

        var className = GetClassName(hwnd);
        if (ShellState.IsIgnoredShellClass(className) || ShellState.IsActiveShellClass(className))
        {
            return false;
        }

        var exStyle = NativeMethods.GetWindowLong(hwnd, GwlExStyle);
        if ((exStyle & WsExToolWindow) != 0 && (exStyle & WsExAppWindow) == 0)
        {
            return false;
        }

        var owner = NativeMethods.GetWindow(hwnd, GwOwner);
        if (owner != IntPtr.Zero && (exStyle & WsExAppWindow) == 0)
        {
            return false;
        }

        if ((exStyle & WsExAppWindow) == 0 && NativeMethods.GetWindowTextLength(hwnd) == 0)
        {
            return false;
        }

        if (IsDwmCloaked(hwnd) || IsEffectivelyHiddenAtScreenEdge(hwnd) || IsIgnoredBackgroundProcess(hwnd))
        {
            return false;
        }

        return true;
    }

    private static void RefreshForegroundState(IntPtr hwnd)
    {
        var className = GetClassName(hwnd);

        if (IsDesktopLikeForeground(hwnd, className))
        {
            isActiveShellUi = false;
            isDesktop = true;
            return;
        }

        if (ShellState.IsActiveShellClass(className))
        {
            isActiveShellUi = true;
            isDesktop = false;
            return;
        }

        isActiveShellUi = false;
        isDesktop = ShellState.IsDesktopClass(className);
    }

    private static bool IsDesktopLikeForeground(IntPtr hwnd, string className)
    {
        if (hwnd == IntPtr.Zero || string.IsNullOrWhiteSpace(className))
        {
            return true;
        }

        if (ShellState.IsDesktopClass(className))
        {
            return true;
        }

        return !NativeMethods.IsWindow(hwnd)
            || !NativeMethods.IsWindowVisible(hwnd)
            || NativeMethods.IsIconic(hwnd)
            || IsDwmCloaked(hwnd)
            || IsEffectivelyHiddenAtScreenEdge(hwnd);
    }

    private static bool IsDwmCloaked(IntPtr hwnd)
    {
        return NativeMethods.DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, Marshal.SizeOf<int>()) == 0
            && cloaked != 0;
    }

    private static bool IsEffectivelyHiddenAtScreenEdge(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var windowArea = width * height;
        if (width <= 0 || height <= 0 || windowArea <= 0)
        {
            return true;
        }

        var visibleArea = 0;
        var screenBounds = cachedScreenBounds;
        if (screenBounds.Length == 0)
        {
            RefreshScreenCache();
            screenBounds = cachedScreenBounds;
        }

        foreach (var bounds in screenBounds)
        {
            var left = Math.Max(rect.Left, bounds.Left);
            var top = Math.Max(rect.Top, bounds.Top);
            var right = Math.Min(rect.Right, bounds.Right);
            var bottom = Math.Min(rect.Bottom, bounds.Bottom);

            if (right > left && bottom > top)
            {
                visibleArea += (right - left) * (bottom - top);
            }
        }

        if (visibleArea == 0)
        {
            return true;
        }

        var visibleRatio = visibleArea / (double)windowArea;
        return (visibleArea < 10000 && (width < 80 || height < 80))
            || visibleRatio < 0.08;
    }

    private static string GetClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        var className = classNameBuffer ??= new StringBuilder(128);
        className.Clear();
        return NativeMethods.GetClassName(hwnd, className, className.Capacity) > 0
            ? className.ToString()
            : string.Empty;
    }

    private static bool IsIgnoredBackgroundProcess(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
        {
            return false;
        }

        if (ignoredProcessCache.TryGetValue(processId, out var cached))
        {
            return cached;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            var ignored = WindowClassifier.IsIgnoredBackgroundProcess(process.ProcessName);
            ignoredProcessCache[processId] = ignored;
            return ignored;
        }
        catch
        {
            ignoredProcessCache[processId] = false;
            return false;
        }
    }

    private static bool IsCursorInBottomBand()
    {
        var cursor = Cursor.Position;
        var hoverBandPixels = Taskbar.GetHoverBandPixels();

        var screenBounds = cachedScreenBounds;
        if (screenBounds.Length == 0)
        {
            RefreshScreenCache();
            screenBounds = cachedScreenBounds;
        }

        foreach (var bounds in screenBounds)
        {
            if (cursor.X >= bounds.Left - EdgeTolerancePixels
                && cursor.X <= bounds.Right + EdgeTolerancePixels
                && cursor.Y >= bounds.Bottom - hoverBandPixels
                && cursor.Y <= bounds.Bottom + EdgeTolerancePixels)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWinKeyPressed()
    {
        return IsKeyDown(VkLWin) || IsKeyDown(VkRWin);
    }

    private static bool IsAltTabPressed()
    {
        return IsKeyDown(VkMenu) && IsKeyDown(VkTab);
    }

    private static bool IsKeyDown(int virtualKey)
    {
        return (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    private static void Cleanup()
    {
        if (isCleaningUp)
        {
            return;
        }

        isCleaningUp = true;

        timer?.Stop();
        timer?.Dispose();
        timer = null;

        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        Taskbar.Restore();

        if (foregroundHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(foregroundHook);
            foregroundHook = IntPtr.Zero;
        }

        if (notifyIcon is not null)
        {
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
            notifyIcon = null;
        }
    }
}

internal static partial class NativeMethods
{
    internal delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    internal struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
}

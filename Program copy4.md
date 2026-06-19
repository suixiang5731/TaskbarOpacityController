using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

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

internal sealed class TaskbarController
{
    private const int GwlExStyle = -20;
    private const int LwaAlpha = 0x00000002;
    private const int WsExLayered = 0x00080000;

    private IntPtr taskbar;
    private byte? currentAlpha;

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

        return taskbar;
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
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutofcontext = 0x0000;
    private const int TickIntervalMs = 100;
    private const int HoverBandPixels = 40;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const int VkD = 0x44;
    private const int VkTab = 0x09;
    private const int VkMenu = 0x12;
    private static readonly TimeSpan DesktopHold = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan AppWindowScanInterval = TimeSpan.FromMilliseconds(500);

    private static readonly TaskbarController Taskbar = new();

    private static NativeMethods.WinEventDelegate? hookDelegate;
    private static IntPtr foregroundHook;
    private static NotifyIcon? notifyIcon;
    private static System.Windows.Forms.Timer? timer;
    private static DateTime forceDesktopUntilUtc = DateTime.MinValue;
    private static DateTime lastAppWindowScanUtc = DateTime.MinValue;
    private static bool isDesktop;
    private static bool isActiveShellUi;
    private static bool hasVisibleAppWindow;
    private static bool wasInDesktopMode;
    private static bool suppressHoverUntilCursorLeavesBottom;
    private static bool isCleaningUp;
    private static bool suppressStartupToggle;

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

        SetupTrayIcon();
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
            suppressHoverUntilCursorLeavesBottom = IsCursorInBottomBand();
        }

        ApplyState();
    }

    private static void ApplyState()
    {
        var now = DateTime.UtcNow;
        var hover = IsCursorInBottomBand();
        var desktopMode = isDesktop && !hasVisibleAppWindow && !isActiveShellUi;

        if (desktopMode && !wasInDesktopMode && hover)
        {
            suppressHoverUntilCursorLeavesBottom = true;
        }

        if (!hover)
        {
            suppressHoverUntilCursorLeavesBottom = false;
        }

        wasInDesktopMode = desktopMode;

        var shouldShow = now >= forceDesktopUntilUtc
            && ((hover && !suppressHoverUntilCursorLeavesBottom)
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
        RefreshVisibleAppWindowState();
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

        if (IsEffectivelyHiddenAtScreenEdge(hwnd))
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
            || IsEffectivelyHiddenAtScreenEdge(hwnd);
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
        foreach (var screen in Screen.AllScreens)
        {
            var bounds = screen.Bounds;
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

        var className = new StringBuilder(128);
        return NativeMethods.GetClassName(hwnd, className, className.Capacity) > 0
            ? className.ToString()
            : string.Empty;
    }

    private static bool IsCursorInBottomBand()
    {
        var cursor = Cursor.Position;

        foreach (var screen in Screen.AllScreens)
        {
            var bounds = screen.Bounds;
            if (cursor.X >= bounds.Left
                && cursor.X < bounds.Right
                && cursor.Y >= bounds.Bottom - HoverBandPixels
                && cursor.Y < bounds.Bottom)
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

    internal struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
}

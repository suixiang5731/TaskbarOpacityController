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
        return className is "Progman" or "WorkerW" or "Shell_TrayWnd"
            or "Shell_SecondaryTrayWnd" or "TrayNotifyWnd" or "TrayClockWClass"
            or "MSTaskListWClass" or "ReBarWindow32";
    }

    public static bool IsDesktopClass(string className)
    {
        return className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    public static bool IsActiveShellClass(string className)
    {
        // 对应功能矩阵：开始菜单、搜索、任务视图等
        return className is "Windows.UI.Core.CoreWindow"
            or "Windows.UI.Composition.DesktopWindowContentBridge"
            or "XamlExplorerHostIslandWindow"
            or "MultitaskingViewFrame" or "TaskSwitcherWnd"
            or "SearchPane" or "SearchAppWindow" or "WindowsSearchBox"
            or "SearchHome" or "StartMenu" or "NotifyIconOverflowWindow"
            or "DV2ControlHost";
    }
}

internal sealed class TaskbarController
{
    private const int GwlExStyle = -20;
    private const int LwaAlpha = 0x00000002;
    private const int WsExLayered = 0x00080000;
    private const int MinimumHoverBandPixels = 40; // 对应矩阵：底部 40px 区域

    private IntPtr taskbar;
    private byte? currentAlpha;
    private int hoverBandPixels = MinimumHoverBandPixels;

    public void Prime() => GetTaskbar();

    public void SetAlpha(byte alpha)
    {
        if (currentAlpha == alpha) return;

        var hwnd = GetTaskbar();
        if (hwnd == IntPtr.Zero) return;

        NativeMethods.SetLayeredWindowAttributes(hwnd, 0, alpha, LwaAlpha);
        currentAlpha = alpha;
    }

    public void Restore()
    {
        currentAlpha = null;
        SetAlpha(255);
    }

    public int GetHoverBandPixels() => hoverBandPixels;

    private IntPtr GetTaskbar()
    {
        if (taskbar != IntPtr.Zero && NativeMethods.IsWindow(taskbar)) return taskbar;

        taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        currentAlpha = null;

        if (taskbar == IntPtr.Zero) return IntPtr.Zero;

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
        if (NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            var height = Math.Max(0, rect.Bottom - rect.Top);
            hoverBandPixels = Math.Max(MinimumHoverBandPixels, height + 8);
        }
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

        if (enabled) key.SetValue(ValueName, GetStartupCommand(), RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static string GetStartupCommand() => $"\"{Application.ExecutablePath}\"";
}

internal static class LocalizedText
{
    private static readonly bool UseChinese = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
    public static string AppName => UseChinese ? "任务栏透明控制器" : "Taskbar Dock Controller";
    public static string RunningStatus => UseChinese ? "任务栏透明控制器：运行中" : "Taskbar Dock Controller: Running";
    public static string ShowTaskbarNow => UseChinese ? "立即显示任务栏" : "Show taskbar now";
    public static string StartWithWindows => UseChinese ? "开机自启动" : "Start with Windows";
    public static string Exit => UseChinese ? "退出" : "Exit";
    public static string StartupErrorTitle => AppName;
    public static string StartupErrorMessage(string detail) => UseChinese ? $"更新开机自启动设置失败：\n\n{detail}" : $"Failed to update startup setting:\n\n{detail}";
}

internal static class Program
{
    private const int GwlExStyle = -20;
    private const int GwOwner = 4;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutofcontext = 0x0000;
    private const int TickIntervalMs = 200; // [优化] 降低轮询频率至200ms
    private const int EdgeTolerancePixels = 8;
    
    // 虚拟键码
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const int VkD = 0x44;
    private const int VkTab = 0x09;
    private const int VkMenu = 0x12;

    private static readonly TimeSpan DesktopHold = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan AppWindowScanInterval = TimeSpan.FromMilliseconds(1000);
    private static readonly TaskbarController Taskbar = new();
    
    // [优化] 全局复用 StringBuilder 减少 GC
    private static readonly StringBuilder SharedStringBuilder = new(256);
    
    // [优化] 缓存屏幕边界，避免高频访问底层 API
    private static Rectangle[] cachedScreens = Array.Empty<Rectangle>();

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

    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 初始化屏幕缓存并监听变动
        UpdateScreenCache();
        SystemEvents.DisplaySettingsChanged += (_, _) => UpdateScreenCache();

        hookDelegate = WinEventCallback;
        foregroundHook = NativeMethods.SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, hookDelegate, 0, 0, WineventOutofcontext);

        Application.ApplicationExit += (_, _) => Cleanup();
        Application.ThreadExit += (_, _) => Cleanup();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();

        SetupTrayIcon();
        Taskbar.Prime();
        RefreshForegroundState(NativeMethods.GetForegroundWindow());
        RefreshVisibleAppWindowState();
        ApplyState();

        timer = new System.Windows.Forms.Timer { Interval = TickIntervalMs };
        timer.Tick += (_, _) => Tick();
        timer.Start();

        Application.Run();
    }

    private static void UpdateScreenCache()
    {
        cachedScreens = Screen.AllScreens.Select(s => s.Bounds).ToArray();
    }

    private static void SetupTrayIcon()
    {
        var menu = new ContextMenuStrip();
        var startupItem = new ToolStripMenuItem(LocalizedText.StartWithWindows)
        {
            CheckOnClick = true,
            Checked = StartupManager.IsEnabled()
        };
        startupItem.CheckedChanged += (_, _) =>
        {
            try { StartupManager.SetEnabled(startupItem.Checked); }
            catch (Exception ex)
            {
                MessageBox.Show(LocalizedText.StartupErrorMessage(ex.Message), LocalizedText.StartupErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        menu.Items.Add(new ToolStripMenuItem(LocalizedText.RunningStatus) { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem(LocalizedText.ShowTaskbarNow, null, (_, _) => Taskbar.SetAlpha(255)));
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(LocalizedText.Exit, null, (_, _) => Application.ExitThread()));

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
        // 定期扫描依然保留，但底层开销已极小化
        if (DateTime.UtcNow - lastAppWindowScanUtc >= AppWindowScanInterval)
        {
            RefreshVisibleAppWindowState();
        }

        // 检测 Win+D (显示桌面)
        if (IsWinKeyPressed() && IsKeyDown(VkD))
        {
            forceDesktopUntilUtc = DateTime.UtcNow + DesktopHold;
            isDesktop = true;
            isActiveShellUi = false;
            hasVisibleAppWindow = false;
        }

        ApplyState();
    }

    private static void ApplyState()
    {
        // 核心逻辑映射矩阵表
        bool isHovering = IsCursorInBottomBand();
        bool isAltTab = IsKeyDown(VkMenu) && IsKeyDown(VkTab);
        bool isWinMenu = IsWinKeyPressed();
        
        bool shouldShow = DateTime.UtcNow >= forceDesktopUntilUtc
            && (isHovering          // 鼠标底部 40px
            || isWinMenu            // 开始菜单 (按键级)
            || isAltTab             // Alt+Tab
            || isActiveShellUi      // 搜索、任务视图、开始菜单 (窗口级)
            || hasVisibleAppWindow  // 有正常应用窗口可见且未最小化
            || !isDesktop);         // 当前前景非桌面

        Taskbar.SetAlpha(shouldShow ? (byte)255 : (byte)0);
    }

    private static void WinEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
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
                return false; // 找到一个即可终止遍历
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static bool IsVisibleNormalAppWindow(IntPtr hwnd)
    {
        // [优化] 过滤顺序：先做最廉价的 API 调用 (短路求值)
        if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)) return false;

        var exStyle = NativeMethods.GetWindowLong(hwnd, GwlExStyle);
        bool isToolWindow = (exStyle & WsExToolWindow) != 0;
        bool isAppWindow = (exStyle & WsExAppWindow) != 0;

        if (isToolWindow && !isAppWindow) return false;
        if (NativeMethods.GetWindow(hwnd, GwOwner) != IntPtr.Zero && !isAppWindow) return false;
        if (!isAppWindow && NativeMethods.GetWindowTextLength(hwnd) == 0) return false;

        var className = GetClassName(hwnd);
        if (ShellState.IsIgnoredShellClass(className) || ShellState.IsActiveShellClass(className)) return false;

        // 最昂贵的检测放在最后
        if (IsEffectivelyHiddenAtScreenEdge(hwnd)) return false;

        return true;
    }

    private static void RefreshForegroundState(IntPtr hwnd)
    {
        var className = GetClassName(hwnd);

        if (IsDesktopLikeForeground(hwnd, className))
        {
            isActiveShellUi = false;
            isDesktop = true;
        }
        else if (ShellState.IsActiveShellClass(className))
        {
            isActiveShellUi = true;
            isDesktop = false;
        }
        else
        {
            isActiveShellUi = false;
            isDesktop = ShellState.IsDesktopClass(className);
        }
    }

    private static bool IsDesktopLikeForeground(IntPtr hwnd, string className)
    {
        if (hwnd == IntPtr.Zero || string.IsNullOrWhiteSpace(className)) return true;
        if (ShellState.IsDesktopClass(className)) return true;

        return !NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd) || IsEffectivelyHiddenAtScreenEdge(hwnd);
    }

    private static bool IsEffectivelyHiddenAtScreenEdge(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return false;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return true;

        // [优化] 快速剔除极小窗口
        if (width < 80 || height < 80) return true;

        int visibleArea = 0;
        // [优化] 使用缓存的屏幕数组，无内存分配
        foreach (var bounds in cachedScreens)
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

        var visibleRatio = visibleArea / (double)(width * height);
        return visibleRatio < 0.08;
    }

    private static string GetClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;
        
        // [优化] 复用 StringBuilder 彻底消除 GC 压力
        SharedStringBuilder.Clear();
        return NativeMethods.GetClassName(hwnd, SharedStringBuilder, SharedStringBuilder.Capacity) > 0
            ? SharedStringBuilder.ToString()
            : string.Empty;
    }

    private static bool IsCursorInBottomBand()
    {
        var cursor = Cursor.Position;
        var hoverBandPixels = Taskbar.GetHoverBandPixels();

        // [优化] 使用缓存的屏幕数组
        foreach (var bounds in cachedScreens)
        {
            if (cursor.X >= bounds.Left - EdgeTolerancePixels &&
                cursor.X <= bounds.Right + EdgeTolerancePixels &&
                cursor.Y >= bounds.Bottom - hoverBandPixels &&
                cursor.Y <= bounds.Bottom + EdgeTolerancePixels)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsWinKeyPressed() => IsKeyDown(VkLWin) || IsKeyDown(VkRWin);
    private static bool IsKeyDown(int virtualKey) => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static void Cleanup()
    {
        if (isCleaningUp) return;
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
    internal delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")] internal static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWinEvent(IntPtr hWinEventHook);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);
    [DllImport("user32.dll", SetLastError = true)] internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] internal static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    internal struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
}
using System.Runtime.InteropServices;
using System.Windows.Forms;

class Program
{
    const int GWL_EXSTYLE = -20;
    const int WS_EX_LAYERED = 0x80000;

    const int LWA_ALPHA = 0x2;

    static IntPtr taskbarHwnd;

    [DllImport("user32.dll")]
    static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern int GetClassName(
        IntPtr hWnd,
        System.Text.StringBuilder lpClassName,
        int nMaxCount);

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    static extern int SetWindowLong(
        IntPtr hWnd,
        int nIndex,
        int dwNewLong);

    [DllImport("user32.dll")]
    static extern bool SetLayeredWindowAttributes(
        IntPtr hwnd,
        uint crKey,
        byte bAlpha,
        uint dwFlags);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int X;
        public int Y;
    }

    static void Main()
    {
        taskbarHwnd = FindWindow("Shell_TrayWnd", null);

        if (taskbarHwnd == IntPtr.Zero)
        {
            MessageBox.Show("找不到任务栏");
            return;
        }

        int style = GetWindowLong(taskbarHwnd, GWL_EXSTYLE);

        SetWindowLong(
            taskbarHwnd,
            GWL_EXSTYLE,
            style | WS_EX_LAYERED);

        Console.WriteLine("Running... F10退出");

        while (true)
        {
            if ((GetAsyncKeyState(0x79) & 0x8000) != 0)
            {
                SetAlpha(255);
                break;
            }

            bool desktop = IsDesktopActive();
            bool hover = IsMouseOnTaskbar();

            if (hover || !desktop)
            {
                SetAlpha(255);
            }
            else
            {
                SetAlpha(0);
            }

            Thread.Sleep(100);
        }
    }

    static bool IsDesktopActive()
    {
        IntPtr hwnd = GetForegroundWindow();

        if (hwnd == IntPtr.Zero)
            return true;

        var sb = new System.Text.StringBuilder(256);

        GetClassName(hwnd, sb, sb.Capacity);

        string cls = sb.ToString();

        return cls == "Progman"
            || cls == "WorkerW"
            || cls == "Shell_TrayWnd";
    }

    static bool IsMouseOnTaskbar()
    {
        GetCursorPos(out POINT p);

        int screenHeight = Screen.PrimaryScreen!.Bounds.Height;

        return p.Y >= screenHeight - 40;
    }

    static void SetAlpha(byte alpha)
    {
        SetLayeredWindowAttributes(
            taskbarHwnd,
            0,
            alpha,
            LWA_ALPHA);
    }
}
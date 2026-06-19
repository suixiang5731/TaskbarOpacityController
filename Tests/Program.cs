using TaskbarOpacityController;

var tests = new (string Name, Action Run)[]
{
    ("foreground desktop classes resolve to desktop", () =>
    {
        AssertEqual(ShellActivityState.Desktop, WindowClassifier.ResolveShellState("Progman", hasVisibleApplicationWindows: true));
        AssertEqual(ShellActivityState.Desktop, WindowClassifier.ResolveShellState("WorkerW", hasVisibleApplicationWindows: true));
    }),
    ("start menu and alt tab classes resolve to active", () =>
    {
        AssertEqual(ShellActivityState.Active, WindowClassifier.ResolveShellState("Windows.UI.Core.CoreWindow", hasVisibleApplicationWindows: false));
        AssertEqual(ShellActivityState.Active, WindowClassifier.ResolveShellState("MultitaskingViewFrame", hasVisibleApplicationWindows: false));
        AssertEqual(ShellActivityState.Active, WindowClassifier.ResolveShellState("TaskSwitcherWnd", hasVisibleApplicationWindows: false));
    }),
    ("show desktop through taskbar with no visible app resolves to desktop", () =>
    {
        AssertEqual(ShellActivityState.Desktop, WindowClassifier.ResolveShellState("Shell_TrayWnd", hasVisibleApplicationWindows: false));
    }),
    ("visible application window resolves to active", () =>
    {
        AssertEqual(ShellActivityState.Active, WindowClassifier.ResolveShellState("CabinetWClass", hasVisibleApplicationWindows: true));
    }),
    ("minimized application window is ignored for desktop detection", () =>
    {
        var snapshot = new WindowSnapshot(
            ClassName: "CabinetWClass",
            IsVisible: true,
            IsMinimized: true,
            IsCloaked: false,
            ExStyle: 0,
            HasOwner: false);

        AssertEqual(false, WindowClassifier.IsApplicationWindow(snapshot));
    }),
    ("normal visible application window counts as active", () =>
    {
        var snapshot = new WindowSnapshot(
            ClassName: "CabinetWClass",
            IsVisible: true,
            IsMinimized: false,
            IsCloaked: false,
            ExStyle: 0,
            HasOwner: false);

        AssertEqual(true, WindowClassifier.IsApplicationWindow(snapshot));
    }),
    ("typeless background process window is ignored", () =>
    {
        var snapshot = new WindowSnapshot(
            ClassName: "Chrome_WidgetWin_1",
            IsVisible: true,
            IsMinimized: false,
            IsCloaked: false,
            ExStyle: 0x00040000,
            HasOwner: false,
            ProcessName: "Typeless",
            TextLength: 8);

        AssertEqual(false, WindowClassifier.IsApplicationWindow(snapshot));
    }),
};

var failed = 0;

foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL {test.Name}");
        Console.WriteLine(ex.Message);
    }
}

if (failed > 0)
{
    Console.WriteLine($"{failed} test(s) failed.");
    Environment.Exit(1);
}

Console.WriteLine($"{tests.Length} test(s) passed.");

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}

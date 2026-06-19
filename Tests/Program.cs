using TaskbarOpacityController;

var tests = new (string Name, Action Run)[]
{
    ("desktop without hover fades target to hidden", () =>
    {
        var state = new DockStateMachine();

        state.UpdateTarget(isDesktop: true, isHover: false);

        AssertEqual((byte)0, state.TargetAlpha);
    }),
    ("active window keeps target visible", () =>
    {
        var state = new DockStateMachine();

        state.UpdateTarget(isDesktop: false, isHover: false);

        AssertEqual((byte)255, state.TargetAlpha);
    }),
    ("hover overrides desktop and forces visible target", () =>
    {
        var state = new DockStateMachine();

        state.UpdateTarget(isDesktop: true, isHover: true);

        AssertEqual((byte)255, state.TargetAlpha);
    }),
    ("fade out steps down without byte underflow", () =>
    {
        var state = new DockStateMachine();
        state.UpdateTarget(isDesktop: true, isHover: false);

        state.AnimateFrame();

        AssertTrue(state.CurrentAlpha < 255, $"Expected alpha below 255, got {state.CurrentAlpha}.");
    }),
    ("fade animation reaches target without overshoot", () =>
    {
        var state = new DockStateMachine();
        state.UpdateTarget(isDesktop: true, isHover: false);

        for (var i = 0; i < 100; i++)
        {
            state.AnimateFrame();
        }

        AssertEqual((byte)0, state.CurrentAlpha);

        state.UpdateTarget(isDesktop: false, isHover: false);

        for (var i = 0; i < 100; i++)
        {
            state.AnimateFrame();
        }

        AssertEqual((byte)255, state.CurrentAlpha);
    }),
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

static void AssertTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

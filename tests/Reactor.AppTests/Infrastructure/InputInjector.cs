using System.Runtime.InteropServices;

namespace Microsoft.UI.Reactor.AppTests.Infrastructure;

/// <summary>
/// Thin Win32 <c>SendInput</c> fallback for the few E2E tests that need real
/// per-keystroke keyboard input or a real mouse drag — capabilities winapp 0.3.2
/// <c>ui</c> does not yet provide (it has no keyboard typing and only single/double/
/// right click, no drag).
///
/// This is deliberately minimal and used ONLY by the input-injection tests
/// (keystroke-level NumberBox/TextBox, OnKeyDown capture, Tab navigation, gesture +
/// drag-drop + docking tear-off drags). Everything else drives the app through
/// <see cref="WinAppUi"/>.
///
/// TODO: replace with native winapp verbs once they ship —
/// winappCli #562 (send-keys) and #498 (drag). When those land, delete this class
/// and route the input-injection tests back through <see cref="WinAppUi"/>.
/// </summary>
public static class InputInjector
{
    /// <summary>Last window passed to <see cref="Foreground"/> — the window injected input targets.</summary>
    private static IntPtr _foregroundTarget;

    /// <summary>
    /// Mark the test process Per-Monitor-V2 DPI aware as early as possible (assembly load).
    ///
    /// winapp (UIA) reports element bounds in PHYSICAL screen pixels. If this process is
    /// DPI-unaware on a mixed-DPI multi-monitor desktop, GetSystemMetrics/GetCursorPos report
    /// a VIRTUALIZED virtual-screen (e.g. 6400×3612 instead of the real 7680×4332), so the
    /// SendInput absolute-coordinate normalization maps winapp's physical target onto the wrong
    /// physical pixel — the cursor lands off-target and clicks/gestures miss, even though
    /// GetCursorPos (also virtualized) reports the target as reached. Becoming PMv2-aware makes
    /// our metrics true physical pixels, matching winapp, so injection lands correctly. On an
    /// all-100%-scale desktop (e.g. CI) virtualized == physical, so this is a no-op there.
    /// </summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void EnsureProcessDpiAware()
    {
        try { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
        catch { /* older OS without the API — best effort */ }
    }

    /// <summary>Bring the target window to the foreground so injected input is routed to it.</summary>
    public static void Foreground(long hwnd)
    {
        // Fail-fast (Inconclusive, not Failed) when this process can't reach the input
        // desktop — otherwise every SendInput below is silently dropped (ACCESS_DENIED)
        // and the test just times out on its assertion. See the guard for why.
        SessionInteractivityGuard.EnsureInputInjectable($"foreground+inject on hwnd {hwnd:X}");

        var h = (IntPtr)hwnd;
        _foregroundTarget = h;
        ShowWindow(h, SW_RESTORE);

        // SendInput routes to the FOREGROUND window's thread input queue, so the Host MUST be
        // foreground before we inject — otherwise the input lands on whatever else is foreground
        // (the vstest host, a previously-activated Host window) and the test sees nothing.
        //
        // A bare SetForegroundWindow is unreliable here: Windows' foreground lock makes it a
        // silent no-op when the caller doesn't already own the foreground, which is exactly the
        // case in a batch run where focus drifts between sequential Host windows. The documented
        // workaround is to ATTACH our input queue to the target window's thread for the duration
        // of the activation calls, which lets SetForegroundWindow/BringWindowToTop/SetActiveWindow
        // actually take effect. We then VERIFY the window really became foreground before
        // returning, and fail fast (diagnosable) if it never does.
        uint targetThread = GetWindowThreadProcessId(h, out _);
        uint thisThread = GetCurrentThreadId();
        bool attached = targetThread != 0 && targetThread != thisThread &&
                        AttachThreadInput(thisThread, targetThread, true);
        try
        {
            SetForegroundWindow(h);
            BringWindowToTop(h);
            SetActiveWindow(h);
        }
        finally
        {
            if (attached)
                AttachThreadInput(thisThread, targetThread, false);
        }

        // Verify the activation actually took, polling up to ~1s so a slightly-delayed activation
        // still passes without a fixed worst-case sleep.
        for (int i = 0; i < 10; i++)
        {
            if (GetForegroundWindow() == h)
                return;
            Thread.Sleep(100);
        }

        if (GetForegroundWindow() == h)
            return;

        throw new WinAppException(
            $"could not bring Host window to foreground for input injection; hwnd=0x{hwnd:X}. " +
            "Injected input would be routed to the wrong window. This usually means another " +
            "window holds the foreground lock (e.g. a batch run where focus drifted).");
    }

    // ─── Keyboard ────────────────────────────────────────────────────────────

    /// <summary>
    /// Type a string of keys. Literal characters are sent as Unicode; embedded
    /// <see cref="Keys"/> sentinels (Tab/Enter/Space/…) are sent as virtual-key presses.
    /// </summary>
    public static void TypeKeys(string keys)
    {
        foreach (var ch in keys)
        {
            if (TryMapSentinel(ch, out var vk))
                PressVirtualKey(vk);
            else
                PressUnicode(ch);
            Thread.Sleep(15); // small inter-key delay so per-keystroke handlers observe each char
        }
    }

    /// <summary>Press Tab.</summary>
    public static void Tab() => PressVirtualKey(VK_TAB);

    /// <summary>Press Shift+Tab.</summary>
    public static void ShiftTab()
    {
        KeyDown(VK_SHIFT);
        PressVirtualKey(VK_TAB);
        KeyUp(VK_SHIFT);
    }

    /// <summary>Select-all + delete (Ctrl+A, Delete) to clear an editable control.</summary>
    public static void ClearViaKeyboard()
    {
        KeyDown(VK_CONTROL);
        PressVirtualKey(VK_A);
        KeyUp(VK_CONTROL);
        Thread.Sleep(15);
        PressVirtualKey(VK_DELETE);
    }

    /// <summary>
    /// Press End to collapse any active text selection to the end of the field, without
    /// adding or removing content. Used before re-typing into a control that a UIA SetFocus
    /// just select-all'd, so consecutive SendKeys calls append instead of overwrite.
    /// </summary>
    public static void CollapseSelectionToEnd()
    {
        PressVirtualKey(VK_END);
        Thread.Sleep(15);
    }

    private static bool TryMapSentinel(char ch, out ushort vk)
    {
        vk = ch switch
        {
            '\ue004' => VK_TAB,
            '\ue006' or '\ue007' => VK_RETURN,
            '\ue00d' => VK_SPACE,
            '\ue00c' => VK_ESCAPE,
            '\ue003' => VK_BACK,
            '\ue017' => VK_DELETE,
            '\ue008' => VK_SHIFT,
            '\ue009' => VK_CONTROL,
            _ => 0,
        };
        return vk != 0;
    }

    private static void PressVirtualKey(ushort vk)
    {
        KeyDown(vk);
        KeyUp(vk);
    }

    private static void KeyDown(ushort vk) => SendKey(vk, 0, 0);
    private static void KeyUp(ushort vk) => SendKey(vk, 0, KEYEVENTF_KEYUP);

    private static void SendKey(ushort vk, ushort scan, uint flags)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void PressUnicode(char ch)
    {
        var down = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE, time = 0, dwExtraInfo = IntPtr.Zero }
            }
        };
        var up = down;
        up.U.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
        SendInput(2, new[] { down, up }, Marshal.SizeOf<INPUT>());
    }

    // ─── Mouse drag ──────────────────────────────────────────────────────────

    /// <summary>
    /// Perform a left-button drag following <paramref name="screenPath"/> (physical screen
    /// pixels). The first point is the press location; the last is the release location.
    /// Intermediate points are moved through with small delays so WinUI observes continuous
    /// pointer motion and crosses its drag-detection threshold.
    /// </summary>
    public static void Drag(IReadOnlyList<(int X, int Y)> screenPath)
    {
        if (screenPath.Count < 2)
            throw new ArgumentException("Drag needs at least a start and end point.", nameof(screenPath));

        MoveTo(screenPath[0].X, screenPath[0].Y);
        Thread.Sleep(60);
        MouseLeft(MOUSEEVENTF_LEFTDOWN);
        Thread.Sleep(80);

        for (int i = 1; i < screenPath.Count; i++)
        {
            MoveTo(screenPath[i].X, screenPath[i].Y);
            Thread.Sleep(60);
        }

        Thread.Sleep(80);
        MouseLeft(MOUSEEVENTF_LEFTUP);
        Thread.Sleep(60);
    }

    /// <summary>
    /// Drag for the dock tear-off→merge pipeline: press at <paramref name="grab"/> (a tab header),
    /// move past WinUI's drag-detection threshold to fire the IMMEDIATE tear-off — the dragged pane
    /// floats off and the dock layout settles — then, with the button still held, move to
    /// <paramref name="drop"/>, dwell so the merge overlay's hit-test latches its "Add as tab"
    /// target, and release.
    ///
    /// <para>The approach-from-above plus the pulsed dwell at <paramref name="drop"/> matter: the
    /// drop overlay only exists mid-drag and arms the hovered target from repeated pointer-move
    /// events, so the cursor must cross and settle on the target before the button-up rather than
    /// release the instant it arrives.</para>
    /// </summary>
    public static void DragTearOffMerge((int X, int Y) grab, (int X, int Y) drop, int dwellBeforeReleaseMs = 350)
    {
        MoveTo(grab.X, grab.Y);
        Thread.Sleep(60);
        MouseLeft(MOUSEEVENTF_LEFTDOWN);
        Thread.Sleep(80);

        // Clear the drag threshold to fire the immediate tear-off, then settle so the layout has
        // stabilised before moving to the drop target.
        MoveTo(grab.X - 8, grab.Y); Thread.Sleep(60);
        MoveTo(grab.X - 16, grab.Y); Thread.Sleep(60);
        MoveTo(grab.X - 36, grab.Y); Thread.Sleep(150);

        // Approach the merge target from above so the cursor crosses it before settling, giving the
        // overlay repeated pointer-move events to latch "Add as tab".
        MoveTo(drop.X, drop.Y - 24); Thread.Sleep(60);
        MoveTo(drop.X, drop.Y - 8); Thread.Sleep(60);
        MoveTo(drop.X, drop.Y); Thread.Sleep(60);

        if (dwellBeforeReleaseMs > 0)
        {
            const int pulses = 4;
            var slice = Math.Max(1, dwellBeforeReleaseMs / pulses);
            for (int i = 0; i < pulses; i++)
            {
                MoveTo(drop.X, drop.Y);
                Thread.Sleep(slice);
            }
        }

        Thread.Sleep(80);
        MouseLeft(MOUSEEVENTF_LEFTUP);
        Thread.Sleep(60);
    }

    /// <summary>Build a drag path from a start point to an end point with two threshold-clearing
    /// micro-moves near the start (matches the old Appium MoveByOffset convention).</summary>
    public static IReadOnlyList<(int X, int Y)> DragPath(int fromX, int fromY, int toX, int toY)
    {
        return new[]
        {
            (fromX, fromY),
            (fromX + 8, fromY),
            (fromX + 16, fromY),
            (toX, toY),
        };
    }

    /// <summary>Move to a point and press-hold the left button for <paramref name="holdMs"/>,
    /// then release — drives WinUI's long-press / press-and-hold detection.</summary>
    public static void PressHoldRelease(int x, int y, int holdMs)
    {
        MoveTo(x, y);
        Thread.Sleep(60);
        MouseLeft(MOUSEEVENTF_LEFTDOWN);
        Thread.Sleep(holdMs);
        MouseLeft(MOUSEEVENTF_LEFTUP);
        Thread.Sleep(60);
    }

    /// <summary>Single left-click at a screen point.</summary>
    public static void Click(int x, int y)
    {
        MoveTo(x, y);
        Thread.Sleep(40);
        MouseLeft(MOUSEEVENTF_LEFTDOWN);
        Thread.Sleep(30);
        MouseLeft(MOUSEEVENTF_LEFTUP);
        Thread.Sleep(40);
    }

    private static void MoveTo(int x, int y)
    {
        // Normalize to 0..65535 across the whole virtual desktop.
        int vsLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vsTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vsWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vsHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (vsWidth <= 1) vsWidth = 2;
        if (vsHeight <= 1) vsHeight = 2;

        int nx = (int)Math.Round((x - vsLeft) * 65535.0 / (vsWidth - 1));
        int ny = (int)Math.Round((y - vsTop) * 65535.0 / (vsHeight - 1));

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = nx,
                    dy = ny,
                    mouseData = 0,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void MouseLeft(uint flag)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT { dx = 0, dy = 0, mouseData = 0, dwFlags = flag, time = 0, dwExtraInfo = IntPtr.Zero }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    // ─── P/Invoke ────────────────────────────────────────────────────────────

    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    private const ushort VK_BACK = 0x08;
    private const ushort VK_TAB = 0x09;
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_ESCAPE = 0x1B;
    private const ushort VK_SPACE = 0x20;
    private const ushort VK_END = 0x23;
    private const ushort VK_DELETE = 0x2E;
    private const ushort VK_A = 0x41;

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private const int SW_RESTORE = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (IntPtr)(-4);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}

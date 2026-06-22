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
    /// <summary>Bring the target window to the foreground so injected input is routed to it.</summary>
    public static void Foreground(long hwnd)
    {
        // Fail-fast (Inconclusive, not Failed) when this process can't reach the input
        // desktop — otherwise every SendInput below is silently dropped (ACCESS_DENIED)
        // and the test just times out on its assertion. See the guard for why.
        SessionInteractivityGuard.EnsureInputInjectable($"foreground+inject on hwnd {hwnd:X}");

        var h = (IntPtr)hwnd;
        ShowWindow(h, SW_RESTORE);
        SetForegroundWindow(h);
        Thread.Sleep(120); // let the activation settle before injecting input
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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}

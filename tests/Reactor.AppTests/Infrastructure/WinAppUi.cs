using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Microsoft.UI.Reactor.AppTests.Infrastructure;

/// <summary>
/// Bounds of a UIA element in physical screen pixels (winapp's BoundingRectangle).
/// </summary>
public readonly record struct UiRect(int X, int Y, int Width, int Height)
{
    public int CenterX => X + Width / 2;
    public int CenterY => Y + Height / 2;
}

/// <summary>One element returned by <c>winapp ui search</c>.</summary>
public sealed record UiMatch(
    string? Type,
    string? Name,
    string? AutomationId,
    string? ClassName,
    bool IsEnabled,
    bool IsOffscreen,
    int X, int Y, int Width, int Height,
    string Selector,
    bool IsInvokable);

/// <summary>A visible top-level window returned by <c>winapp ui list-windows</c>.</summary>
public sealed record UiWindow(long Hwnd, int ProcessId, string? Title, string? ClassName, bool IsForeground);

/// <summary>
/// Thin wrapper over the <c>winapp ui</c> CLI (UI Automation). Each method spawns a
/// short-lived <c>winapp.exe</c> process targeting the Host app's window (by HWND for
/// stability — survives the multi-window state docking tear-off creates) and parses the
/// <c>--json</c> envelope with System.Text.Json.
///
/// This replaces the persistent Appium <c>WindowsDriver</c> session. winapp has no
/// persistent session (process-per-call), so polling helpers map onto winapp's own
/// internal <c>wait-for</c> (single process, 100ms internal poll) to avoid spawning a
/// process per poll tick.
/// </summary>
public sealed class WinAppUi
{
    private static readonly string WinAppExe = ResolveWinAppExe();

    private readonly int _pid;

    /// <summary>HWND of the primary Host window, captured at session start.</summary>
    public long HostHwnd { get; }

    public WinAppUi(int pid, long hostHwnd)
    {
        _pid = pid;
        HostHwnd = hostHwnd;
    }

    private static string ResolveWinAppExe()
    {
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrEmpty(local))
        {
            var candidate = Path.Combine(local, "Microsoft", "WindowsApps", "winapp.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return "winapp"; // fall back to PATH lookup
    }

    // ─── Process plumbing ────────────────────────────────────────────────────

    private readonly record struct RunResult(int ExitCode, string StdOut, string StdErr);

    private RunResult Run(int processTimeoutMs, params string[] args)
    {
        var psi = new ProcessStartInfo(WinAppExe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("ui");
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var sbOut = new StringBuilder();
        var sbErr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) sbOut.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) sbErr.AppendLine(e.Data); };

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            throw new WinAppException(
                $"Failed to launch winapp ({WinAppExe}). Ensure winapp CLI is installed and on PATH.", ex);
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        if (!proc.WaitForExit(processTimeoutMs))
        {
            try { proc.Kill(true); } catch { }
            throw new WinAppTimeoutException(
                $"winapp ui {string.Join(' ', args)} did not exit within {processTimeoutMs}ms.");
        }
        // Ensure async buffers are flushed.
        proc.WaitForExit();

        return new RunResult(proc.ExitCode, sbOut.ToString(), sbErr.ToString());
    }

    /// <summary>Append the window target + --json to a verb's args.</summary>
    private string[] Args(string verb, long hwnd, params string[] rest)
    {
        var list = new List<string>(rest.Length + 5) { verb };
        list.AddRange(rest);
        list.Add("-w");
        list.Add(hwnd.ToString(CultureInfo.InvariantCulture));
        list.Add("--json");
        return list.ToArray();
    }

    private static JsonDocument Parse(RunResult r)
    {
        var text = r.StdOut.Trim();
        if (text.Length == 0)
            throw new WinAppException($"winapp returned empty output (exit {r.ExitCode}). stderr: {r.StdErr.Trim()}");
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new WinAppException($"Could not parse winapp JSON (exit {r.ExitCode}): {text}", ex);
        }
    }

    // ─── Connection ──────────────────────────────────────────────────────────

    /// <summary>Resolve the primary Host window HWND for a process by title.</summary>
    public static long FindWindowHwnd(int pid, string title, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var ui = new WinAppUi(pid, 0);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                foreach (var w in ui.ListWindowsForPid())
                {
                    if (w.ProcessId == pid &&
                        (w.Title?.Contains(title, StringComparison.OrdinalIgnoreCase) ?? false))
                        return w.Hwnd;
                }
            }
            catch (Exception ex) { last = ex; }
            Thread.Sleep(200);
        }
        throw new WinAppTimeoutException(
            $"Host window '{title}' (pid {pid}) did not appear within {timeoutMs}ms." +
            (last is null ? "" : $" Last error: {last.Message}"));
    }

    private IEnumerable<UiWindow> ListWindowsForPid()
    {
        var r = Run(15000, "list-windows", "-a", _pid.ToString(CultureInfo.InvariantCulture), "--json");
        if (r.StdOut.Trim().Length == 0) yield break;
        using var doc = Parse(r);
        foreach (var w in EnumerateWindows(doc.RootElement)) yield return w;
    }

    /// <summary>All visible windows belonging to the Host process (host + floating tear-off windows).</summary>
    public IReadOnlyList<UiWindow> ListWindows()
    {
        var result = new List<UiWindow>();
        var r = Run(15000, "list-windows", "-a", _pid.ToString(CultureInfo.InvariantCulture), "--json");
        if (r.StdOut.Trim().Length == 0) return result;
        using var doc = Parse(r);
        result.AddRange(EnumerateWindows(doc.RootElement));
        return result;
    }

    private static IEnumerable<UiWindow> EnumerateWindows(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) yield break;
        foreach (var w in root.EnumerateArray())
        {
            yield return new UiWindow(
                GetLong(w, "hwnd"),
                (int)GetLong(w, "processId"),
                GetString(w, "title"),
                GetString(w, "className"),
                GetBool(w, "isForeground"));
        }
    }

    // ─── Search / existence ──────────────────────────────────────────────────

    /// <summary>Run <c>winapp ui search</c> against the given window. Empty list on miss.</summary>
    public IReadOnlyList<UiMatch> Search(string selector, long? hwnd = null)
    {
        var r = Run(15000, Args("search", hwnd ?? HostHwnd, selector));
        var matches = new List<UiMatch>();
        if (r.StdOut.Trim().Length == 0) return matches;
        using var doc = Parse(r);
        if (!doc.RootElement.TryGetProperty("matches", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return matches;
        foreach (var m in arr.EnumerateArray())
        {
            matches.Add(new UiMatch(
                GetString(m, "type"), GetString(m, "name"), GetString(m, "automationId"),
                GetString(m, "className"), GetBool(m, "isEnabled"), GetBool(m, "isOffscreen"),
                (int)GetLong(m, "x"), (int)GetLong(m, "y"), (int)GetLong(m, "width"), (int)GetLong(m, "height"),
                GetString(m, "selector") ?? selector, GetBool(m, "isInvokable")));
        }
        return matches;
    }

    /// <summary>True if any element matches the selector in the target window.</summary>
    public bool Exists(string selector, long? hwnd = null) => Search(selector, hwnd).Count > 0;

    // ─── Reads ───────────────────────────────────────────────────────────────

    /// <summary>Smart value read (TextPattern → ValuePattern → Name). Null if absent.</summary>
    public string? GetValue(string selector, long? hwnd = null)
    {
        var r = Run(15000, Args("get-value", hwnd ?? HostHwnd, selector));
        if (r.ExitCode != 0 || r.StdOut.Trim().Length == 0) return null;
        using var doc = Parse(r);
        return GetString(doc.RootElement, "text");
    }

    /// <summary>Read a single UIA property. Null if winapp can't surface it (caller may fall back to UIA).</summary>
    public string? GetProperty(string selector, string property, long? hwnd = null)
    {
        var r = Run(15000, Args("get-property", hwnd ?? HostHwnd, selector, "-p", property));
        if (r.ExitCode != 0 || r.StdOut.Trim().Length == 0) return null;
        using var doc = Parse(r);
        if (!doc.RootElement.TryGetProperty("properties", out var props)) return null;
        if (!props.TryGetProperty(property, out var val)) return null;
        return val.ValueKind == JsonValueKind.Null ? null : val.GetString();
    }

    /// <summary>Bounds of the first element matching the selector. Null if absent.</summary>
    public UiRect? GetBounds(string selector, long? hwnd = null)
    {
        var matches = Search(selector, hwnd);
        if (matches.Count == 0) return null;
        var m = matches[0];
        return new UiRect(m.X, m.Y, m.Width, m.Height);
    }

    // ─── Actions ─────────────────────────────────────────────────────────────

    /// <summary>Activate via UIA patterns (Invoke → Toggle → SelectionItem → ExpandCollapse).</summary>
    public void Invoke(string selector, long? hwnd = null)
    {
        var r = Run(15000, Args("invoke", hwnd ?? HostHwnd, selector));
        if (r.ExitCode != 0)
        {
            // Fall back to a real mouse click for elements that support no invoke pattern.
            Click(selector, hwnd: hwnd);
        }
    }

    /// <summary>Mouse-simulation click (for elements without InvokePattern).</summary>
    public void Click(string selector, bool doubleClick = false, bool rightClick = false, long? hwnd = null)
    {
        // winapp's click verb is real SendInput under the hood — it fails with
        // ACCESS_DENIED off the interactive input desktop (non-uiAccess / disconnected
        // session). Surface that as Inconclusive (not Failed), matching InputInjector.
        SessionInteractivityGuard.EnsureInputInjectable($"click '{selector}'");

        var extra = new List<string> { selector };
        if (doubleClick) extra.Add("--double");
        if (rightClick) extra.Add("--right");
        var r = Run(15000, Args("click", hwnd ?? HostHwnd, extra.ToArray()));
        if (r.ExitCode != 0)
            throw new WinAppException($"winapp ui click '{selector}' failed: {r.StdErr.Trim()} {r.StdOut.Trim()}");
    }

    /// <summary>Set a value via UIA ValuePattern (TextBox/ComboBox/Slider).</summary>
    public void SetValue(string selector, string value, long? hwnd = null)
    {
        var r = Run(15000, Args("set-value", hwnd ?? HostHwnd, selector, value));
        if (r.ExitCode != 0)
            throw new WinAppException($"winapp ui set-value '{selector}' failed: {r.StdErr.Trim()} {r.StdOut.Trim()}");
    }

    /// <summary>Move keyboard focus to the element via UIA SetFocus.</summary>
    public void Focus(string selector, long? hwnd = null)
    {
        var r = Run(15000, Args("focus", hwnd ?? HostHwnd, selector));
        if (r.ExitCode != 0)
            throw new WinAppException($"winapp ui focus '{selector}' failed: {r.StdErr.Trim()} {r.StdOut.Trim()}");
    }

    // ─── Waits (winapp-internal polling) ─────────────────────────────────────

    /// <summary>Wait for the element to exist. Returns false on timeout.</summary>
    public bool WaitForExists(string selector, int timeoutMs = 5000, long? hwnd = null)
    {
        var r = Run(timeoutMs + 30000,
            Args("wait-for", hwnd ?? HostHwnd, selector, "--timeout", timeoutMs.ToString(CultureInfo.InvariantCulture)));
        return r.ExitCode == 0;
    }

    /// <summary>Wait for the element to disappear. Returns false on timeout.</summary>
    public bool WaitForGone(string selector, int timeoutMs = 5000, long? hwnd = null)
    {
        var r = Run(timeoutMs + 30000,
            Args("wait-for", hwnd ?? HostHwnd, selector, "--gone",
                "--timeout", timeoutMs.ToString(CultureInfo.InvariantCulture)));
        return r.ExitCode == 0;
    }

    /// <summary>Wait until the element's value equals (or contains) the target. False on timeout.</summary>
    public bool WaitForValue(string selector, string value, bool contains = false,
        int timeoutMs = 5000, long? hwnd = null)
    {
        var args = new List<string> { selector, "--value", value, "--timeout",
            timeoutMs.ToString(CultureInfo.InvariantCulture) };
        if (contains) args.Add("--contains");
        var r = Run(timeoutMs + 30000, Args("wait-for", hwnd ?? HostHwnd, args.ToArray()));
        return r.ExitCode == 0;
    }

    // ─── JSON helpers ────────────────────────────────────────────────────────

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long GetLong(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out var l) ? l : 0,
            JsonValueKind.String => long.TryParse(v.GetString(), out var l) ? l : 0,
            _ => 0,
        };
    }

    private static bool GetBool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False && v.GetBoolean();
}

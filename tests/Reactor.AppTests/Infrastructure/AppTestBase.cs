using System.Drawing;
using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.UI.Reactor.AppTests.Infrastructure;

/// <summary>
/// Base class for all UI test classes. Provides helpers for navigation, element lookup,
/// waiting, and DPI-aware assertions.
///
/// Drives the running Host app through <see cref="WinAppUi"/> (the <c>winapp ui</c> CLI,
/// UIA-based) instead of an Appium <c>WindowsDriver</c> session. Method signatures are kept
/// identical to the former Appium harness so existing test bodies keep their shape — element
/// handles are now <see cref="UiElement"/> rather than <c>WindowsElement</c>.
/// </summary>
public class AppTestBase
{
    /// <summary>The winapp-backed UI automation driver bound to the Host window.</summary>
    protected static WinAppUi App => TestSession.App;

    /// <summary>In-process UIA property reader (fallback for properties winapp can't surface).</summary>
    protected static UiaPropertyReader Uia => TestSession.Uia;

    /// <summary>HWND of the primary Host window.</summary>
    protected static long HostHwnd => TestSession.HostHwnd;

    /// <summary>Build a <see cref="UiElement"/> handle for a selector against the host window.</summary>
    protected static UiElement Element(string selector, string? automationId = null, long hwnd = 0) =>
        new(App, Uia, selector, automationId ?? selector, hwnd == 0 ? TestSession.HostHwnd : hwnd);

    // Per-test interactivity preflight — bails out as Inconclusive (not Failed)
    // when the workstation is locked or the session is disconnected, so flake
    // reports don't drown in environmental noise.
    [TestInitialize]
    public void GuardSessionInteractive()
    {
        SessionInteractivityGuard.EnsureInteractive("TestInitialize");
    }

    private static string? _currentFixture;

    /// <summary>
    /// Navigates to a named test fixture by clicking its nav element and waiting
    /// for the fixture status to indicate it has loaded. Skips if already on
    /// the requested fixture (safe for read-only tests like accessibility checks).
    /// </summary>
    protected void NavigateToFixture(string name)
    {
        if (_currentFixture == name)
            return;

        var expected = $"Loaded: {name}";

        // Click + wait. If the click is silently absorbed (observed when the
        // previous test left a flyout open, or when a Reset re-render races the
        // navigator's hit-test rebuild), the wait times out — retry the click
        // once before giving up.
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                App.Invoke($"Nav_{name}");
                if (App.WaitForValue("FixtureStatus", expected, timeoutMs: 5000))
                {
                    _currentFixture = name;
                    return;
                }
                if (attempt == 0)
                    Thread.Sleep(250);
            }

            var lastSeen = App.GetValue("FixtureStatus") ?? "<not found>";
            throw new WinAppTimeoutException(
                $"Timed out waiting for fixture '{name}' to load (FixtureStatus expected " +
                $"'{expected}', last-seen '{lastSeen}').");
        }
        catch (WinAppException)
        {
            // The screen may have locked between the preflight check and the click.
            // Recheck — if locked, surface as Inconclusive; otherwise rethrow as a
            // real test failure.
            SessionInteractivityGuard.RecheckAfterFailure($"NavigateToFixture({name})");
            throw;
        }
    }

    /// <summary>
    /// Forces re-navigation to the fixture even if it's the current one.
    /// Use when the test modifies fixture state and needs a fresh start.
    /// </summary>
    protected void NavigateToFixtureFresh(string name)
    {
        ResetFixture();
        _currentFixture = null;
        NavigateToFixture(name);
    }

    /// <summary>
    /// Resets the current fixture to its default state.
    /// </summary>
    protected void ResetFixture()
    {
        try
        {
            if (!App.Exists("ResetFixture"))
                return; // not present yet (e.g., before first navigation)
            App.Invoke("ResetFixture");
            App.WaitForValue("FixtureStatus", "Ready", timeoutMs: 3000);
        }
        catch (WinAppException)
        {
            // Reset button may not be present yet (e.g., before first navigation).
        }
    }

    /// <summary>
    /// Finds an element by its AutomationId (UIA accessibility identifier).
    /// Throws when no element matches, mirroring the former FindElement contract.
    /// </summary>
    protected UiElement FindById(string automationId)
    {
        if (!App.Exists(automationId))
            throw new WinAppException($"No element found with AutomationId '{automationId}'.");
        return Element(automationId, automationId);
    }

    /// <summary>
    /// Finds an element by its Name property.
    /// </summary>
    protected UiElement FindByName(string name)
    {
        var matches = App.Search(name);
        // Prefer an exact Name match; winapp may also return substring hits.
        var exact = matches.FirstOrDefault(m => m.Name == name) ?? matches.FirstOrDefault();
        if (exact is null)
            throw new WinAppException($"No element found with Name '{name}'.");
        var selector = !string.IsNullOrEmpty(exact.AutomationId) ? exact.AutomationId! : exact.Selector;
        return Element(selector, exact.AutomationId);
    }

    /// <summary>
    /// Waits for an element with the given AutomationId to appear.
    /// </summary>
    protected UiElement WaitForElement(string automationId, int timeoutMs = 5000)
    {
        if (!App.WaitForExists(automationId, timeoutMs))
            throw new WinAppTimeoutException(
                $"Timed out after {timeoutMs}ms waiting for AutomationId='{automationId}' to appear.");
        return Element(automationId, automationId);
    }

    /// <summary>
    /// Waits until the element with the given AutomationId displays the expected text.
    /// </summary>
    protected void WaitForText(string automationId, string expectedText, int timeoutMs = 5000)
    {
        if (App.WaitForValue(automationId, expectedText, contains: false, timeoutMs: timeoutMs))
            return;

        var lastSeen = App.GetValue(automationId) ?? "<not found>";
        throw new WinAppTimeoutException(
            $"Timed out after {timeoutMs}ms waiting for AutomationId='{automationId}' " +
            $"to have text '{expectedText}'. Last-seen text: '{lastSeen}'.");
    }

    /// <summary>
    /// Waits until the element's text contains the expected substring.
    /// Returns the element text for use in assertion messages.
    /// </summary>
    protected string WaitForTextContaining(string automationId, string substring, int timeoutMs = 5000)
    {
        if (!App.WaitForValue(automationId, substring, contains: true, timeoutMs: timeoutMs))
        {
            var seen = App.GetValue(automationId) ?? "<not found>";
            throw new WinAppTimeoutException(
                $"Timed out after {timeoutMs}ms waiting for AutomationId='{automationId}' " +
                $"text to contain '{substring}'. Last-seen text: '{seen}'.");
        }
        return App.GetValue(automationId) ?? "";
    }

    /// <summary>
    /// Reads the DPI scale factor from the TestHostRoot element.
    /// The Host app sets its Name property to "DpiScale:X.XXXX".
    /// </summary>
    protected double GetDpiScale()
    {
        var name = App.GetProperty("TestHostRoot", "Name");

        // Expected format: "DpiScale:1.5000"
        if (name != null && name.StartsWith("DpiScale:") &&
            double.TryParse(name["DpiScale:".Length..],
                NumberStyles.Float, CultureInfo.InvariantCulture, out var scale))
        {
            return scale;
        }

        // Default to 1.0 if not available.
        return 1.0;
    }

    /// <summary>
    /// Asserts that <paramref name="actual"/> is within <paramref name="tolerance"/>
    /// of <paramref name="expected"/>.
    /// </summary>
    protected static void AssertNear(double actual, double expected, double tolerance)
    {
        var diff = Math.Abs(actual - expected);
        Assert.IsTrue(
            diff <= tolerance,
            $"Expected {expected} ± {tolerance}, but got {actual} (off by {diff}).");
    }

    /// <summary>
    /// Returns the UIA BoundingRectangle of the element as a <see cref="Rectangle"/>.
    /// </summary>
    protected Rectangle GetElementRect(string automationId)
    {
        return FindById(automationId).Rect;
    }

    /// <summary>
    /// Returns the logical (DPI-independent) size of an element as (width, height).
    /// </summary>
    protected (double Width, double Height) GetLogicalSize(string automationId)
    {
        var rect = GetElementRect(automationId);
        var dpi = GetDpiScale();
        return (rect.Width / dpi, rect.Height / dpi);
    }

    /// <summary>
    /// Clicks a button by AccessibilityId first, falling back to Name.
    /// </summary>
    protected void ClickButton(string nameOrId)
    {
        if (App.Exists(nameOrId))
        {
            App.Invoke(nameOrId);
            return;
        }
        FindByName(nameOrId).Click();
    }
}

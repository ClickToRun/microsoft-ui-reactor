using System.Drawing;

namespace Microsoft.UI.Reactor.AppTests.Infrastructure;

/// <summary>
/// Lightweight handle to a UIA element, addressed by selector (AutomationId — stable —
/// or a winapp slug). Mirrors the slice of the old Appium <c>WindowsElement</c> surface
/// the test suite actually used (<see cref="Click"/>, <see cref="SendKeys"/>,
/// <see cref="Clear"/>, <see cref="Text"/>, <see cref="GetAttribute"/>, <see cref="Rect"/>)
/// so existing test bodies keep their shape.
///
/// Reads/actions go through <see cref="WinAppUi"/>; keystroke input goes through
/// <see cref="InputInjector"/>; UIA properties winapp can't surface fall back to
/// <see cref="UiaPropertyReader"/>.
/// </summary>
public sealed class UiElement
{
    private readonly WinAppUi _app;
    private readonly UiaPropertyReader _uia;
    private readonly long _hwnd;

    /// <summary>The selector used to address this element (AutomationId when available).</summary>
    public string Selector { get; }

    /// <summary>The AutomationId, when this handle was resolved from one (enables UIA fallback).</summary>
    public string? AutomationId { get; }

    internal UiElement(WinAppUi app, UiaPropertyReader uia, string selector, string? automationId, long hwnd)
    {
        _app = app;
        _uia = uia;
        Selector = selector;
        AutomationId = automationId;
        _hwnd = hwnd;
    }

    /// <summary>Activate the element (UIA invoke patterns, falling back to a real click).</summary>
    public void Click() => _app.Invoke(Selector, _hwnd);

    /// <summary>The element's text/value (TextPattern → ValuePattern → Name).</summary>
    public string? Text => _app.GetValue(Selector, _hwnd);

    /// <summary>
    /// Read a UIA property by name. Tries winapp <c>get-property</c> first; for properties
    /// winapp 0.3.2 returns null on (and when this handle has an AutomationId), falls back to
    /// the in-process UIA reader so the accessibility suite keeps parity with WinAppDriver.
    /// </summary>
    public string? GetAttribute(string name)
    {
        var viaWinApp = _app.GetProperty(Selector, name, _hwnd);
        if (viaWinApp != null) return viaWinApp;

        if (AutomationId != null && UiaPropertyReader.Handles(name))
            return _uia.ReadByAutomationId(AutomationId, name);

        return viaWinApp;
    }

    /// <summary>Bounding rectangle in physical screen pixels.</summary>
    public Rectangle Rect
    {
        get
        {
            var b = _app.GetBounds(Selector, _hwnd)
                ?? throw new WinAppException($"Element '{Selector}' has no bounds (not found).");
            return new Rectangle(b.X, b.Y, b.Width, b.Height);
        }
    }

    /// <summary>
    /// Type into the element. Foregrounds the host window, focuses this element via UIA,
    /// then injects the keystrokes with <see cref="InputInjector"/> (winapp has no typing).
    /// </summary>
    public void SendKeys(string keys)
    {
        InputInjector.Foreground(_hwnd == 0 ? _app.HostHwnd : _hwnd);
        TryFocus();
        InputInjector.TypeKeys(keys);
    }

    /// <summary>Clear the editable control (select-all + delete via injected keys).</summary>
    public void Clear()
    {
        InputInjector.Foreground(_hwnd == 0 ? _app.HostHwnd : _hwnd);
        TryFocus();
        InputInjector.ClearViaKeyboard();
    }

    /// <summary>Move keyboard focus to this element via UIA SetFocus.</summary>
    public void Focus() => TryFocus();

    private void TryFocus()
    {
        try { _app.Focus(Selector, _hwnd); }
        catch (WinAppException) { /* some elements reject SetFocus; typing still targets the foreground focus */ }
    }
}

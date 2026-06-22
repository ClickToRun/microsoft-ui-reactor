using System.Drawing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.UI.Reactor.AppTests.Infrastructure;

namespace Microsoft.UI.Reactor.AppTests.Tests;

/// <summary>
/// Spec 045 — E2E keyboard-input + focus tests across docking layout
/// mutations. The bug class this guards against: a parent component's
/// setState (here, a controlled TextBox's <c>OnChanged</c> handler)
/// causes the docking host to re-render, and some unconditional
/// property write deep inside the reconciler's tab-header update steals
/// focus from the focused TextBox on every keystroke. Symptom:
/// "I type one character and have to click back in the textbox to type
/// another." The fix lives in
/// <c>Reconciler.Update.UpdateTabView</c> +
/// <c>Reconciler.Mount.TryUpdatePinHeaderInPlace</c>.
///
/// Keyboard input + tab-header drags are synthesized via the Win32
/// <see cref="InputInjector"/> fallback (winapp ui has no typing or drag verb).
/// </summary>
[TestClass]
public class DockingInputTests : AppTestBase
{
    [ClassInitialize]
    public static void StartAppSession(TestContext context) => TestSession.AssemblyInit(context);

    [ClassCleanup]
    public static void StopAppSession() => TestSession.AssemblyCleanup();

    private static (int X, int Y) Center(Rectangle r) => (r.X + r.Width / 2, r.Y + r.Height / 2);

    /// <summary>Drag one tab header onto another to merge the panes into one tab group.</summary>
    private void DragTabOnto(string fromName, string toName)
    {
        var f = Center(FindByName(fromName).Rect);
        var t = Center(FindByName(toName).Rect);
        InputInjector.Foreground(HostHwnd);
        InputInjector.Drag(new[]
        {
            f, (f.X - 8, f.Y), (f.X - 16, f.Y), (f.X - 36, f.Y),
            ((f.X + t.X) / 2, (f.Y + t.Y) / 2), t,
        });
    }

    /// <summary>
    /// Type a multi-character string into the left pane's TextBox,
    /// then Tab to the right pane and type another string. Both panes
    /// are pinnable ToolWindows in separate tab groups — the
    /// configuration that previously triggered the
    /// <see cref="WinUI.TabView"/> pin-header rebuild on every
    /// keystroke. Every character must land; focus must not bounce.
    /// </summary>
    [TestMethod]
    public void DockingInput_TypeAndTabAcrossPanes()
    {
        NavigateToFixtureFresh("DockingInput_TwoPaneTextBoxes");

        // Baseline: both states empty (the state TextBlocks read
        // "Left state: " / "Right state: " with a trailing space).
        WaitForText("DockEditor_Left_State", "Left state: ");
        WaitForText("DockEditor_Right_State", "Right state: ");

        // Click into the left TextBox and type. The Thread.Sleep
        // gives WinUI time to settle focus into the inner Edit
        // control before SendKeys delivers the first character.
        var leftField = FindById("DockEditor_Left");
        leftField.Click();
        Thread.Sleep(250);
        leftField.SendKeys("hello");

        WaitForText("DockEditor_Left_State", "Left state: hello", timeoutMs: 5000);

        // Tab from the focused left field. WinUI's tab traversal should
        // hop out of the left pane (past any tab strip / splitter
        // chrome) and land on the right pane's TextBox.
        leftField.SendKeys(Keys.Tab);

        var rightField = FindById("DockEditor_Right");
        rightField.SendKeys("world");

        WaitForText("DockEditor_Right_State", "Right state: world", timeoutMs: 5000);
        // Left state must be preserved across the Tab traversal +
        // right pane edits.
        WaitForText("DockEditor_Left_State", "Left state: hello");
    }

    /// <summary>
    /// Control variant of <see cref="DockingInput_TypeAndTabAcrossPanes"/>:
    /// identical scenario but the fixture uses bare DockableContent with
    /// <c>CanPin: false</c> on both panes. If this test PASSES while the
    /// pinned-pane variant fails, the bug is gated by the pin-affordance
    /// reconcile path in <c>UpdateTabView</c>. If both fail, the bug is
    /// more general and lives in the docking host's render itself.
    /// </summary>
    [TestMethod]
    public void DockingInput_NoPin_TypeAndTabAcrossPanes()
    {
        NavigateToFixtureFresh("DockingInput_TwoPaneTextBoxesNoPin");

        WaitForText("DockEditorNoPin_Left_State", "Left state: ");
        WaitForText("DockEditorNoPin_Right_State", "Right state: ");

        var leftField = FindById("DockEditorNoPin_Left");
        leftField.Click();
        Thread.Sleep(250);
        leftField.SendKeys("hello");

        WaitForText("DockEditorNoPin_Left_State", "Left state: hello", timeoutMs: 5000);

        leftField.SendKeys(Keys.Tab);
        var rightField = FindById("DockEditorNoPin_Right");
        rightField.SendKeys("world");
        WaitForText("DockEditorNoPin_Right_State", "Right state: world", timeoutMs: 5000);
        WaitForText("DockEditorNoPin_Left_State", "Left state: hello");
    }

    /// <summary>
    /// Drag the right pane's tab into the left pane's tab group
    /// (Center drop = tabbed siblings). After the layout mutation the
    /// shape-only override stores just the pane Keys; the §2.30
    /// resolve step substitutes back the app-supplied Content (which
    /// holds the Memo state, which holds the typed text). Typing into
    /// the newly-tabbed pane must still work, and the pre-existing
    /// values from both editors must survive the layout change.
    /// </summary>
    [TestMethod]
    public void DockingInput_DragToTab_PreservesFocusAndState()
    {
        NavigateToFixtureFresh("DockingInput_TwoPaneTextBoxes");

        // Seed both editors before the layout change so we can verify
        // post-mutation state survival.
        var leftField = FindById("DockEditor_Left");
        leftField.Click();
        leftField.SendKeys("alpha");
        WaitForText("DockEditor_Left_State", "Left state: alpha");

        leftField.SendKeys(Keys.Tab);
        var rightField = FindById("DockEditor_Right");
        rightField.SendKeys("beta");
        WaitForText("DockEditor_Right_State", "Right state: beta");

        // Drag the right tab's header onto the left tab's header. The
        // mid-travel offsets force WinUI to observe continuous pointer
        // motion (matches the DragDrop test convention).
        DragTabOnto("Right", "Left");

        // After the drag, both panes are tabs in the same group.
        // Verify that typing into the post-merge active tab still works.
        InputInjector.Foreground(HostHwnd);
        InputInjector.TypeKeys("X");
        Thread.Sleep(250);

        // One of the two state TextBlocks must end in "X" (whichever
        // pane is active after the merge).
        var leftAfter = FindById("DockEditor_Left_State").Text ?? "";
        var rightAfter = FindById("DockEditor_Right_State").Text ?? "";
        Assert.IsTrue(
            leftAfter.EndsWith("X") || rightAfter.EndsWith("X"),
            $"Typing into the post-merge active tab should append 'X' to one of the " +
            $"state labels. Left='{leftAfter}', Right='{rightAfter}'.");
    }

    /// <summary>
    /// Companion to <see cref="DockingInput_DragToTab_PreservesFocusAndState"/>:
    /// after dragging the right pane's tab into the left pane's group,
    /// both pre-drag state values ("alpha" / "beta") must survive.
    /// </summary>
    [TestMethod]
    public void DockingInput_DragToTab_PreservesPreDragState()
    {
        NavigateToFixtureFresh("DockingInput_TwoPaneTextBoxes");

        var leftField = FindById("DockEditor_Left");
        leftField.Click();
        Thread.Sleep(250);
        leftField.SendKeys("alpha");
        WaitForText("DockEditor_Left_State", "Left state: alpha");

        leftField.SendKeys(Keys.Tab);
        var rightField = FindById("DockEditor_Right");
        rightField.SendKeys("beta");
        WaitForText("DockEditor_Right_State", "Right state: beta");

        DragTabOnto("Right", "Left");
        Thread.Sleep(500);

        WaitForText("DockEditor_Left_State", "Left state: alpha", timeoutMs: 5000);
        WaitForText("DockEditor_Right_State", "Right state: beta", timeoutMs: 5000);
    }
}

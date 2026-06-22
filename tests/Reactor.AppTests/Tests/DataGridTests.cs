using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.UI.Reactor.AppTests.Infrastructure;

namespace Microsoft.UI.Reactor.AppTests.Tests;

/// <summary>
/// E2E tests for DataGrid inline editing. Exercises click-to-edit, real keyboard input,
/// cross-row commit, and same-row cell switching through the full WinUI accessibility
/// pipeline. Cells are located + clicked via winapp ui; the inline editor TextBox has no
/// stable AutomationId, so text is typed into the focused control with the Win32
/// <see cref="InputInjector"/> fallback (winapp ui has no keyboard typing).
/// </summary>
[TestClass]
public class DataGridTests : AppTestBase
{
    [ClassInitialize]
    public static void StartAppSession(TestContext context) => TestSession.AssemblyInit(context);

    [ClassCleanup]
    public static void StopAppSession() => TestSession.AssemblyCleanup();

    /// <summary>
    /// Click a cell to enter edit mode, type a new value, click a different
    /// row to commit, then click the second column to edit it, type, and
    /// press Enter to commit. Verifies the full editing pipeline through
    /// real mouse and keyboard input.
    /// </summary>
    [TestMethod]
    public void Interactive_DataGrid_ClickEditTabCommit()
    {
        NavigateToFixtureFresh("DataGrid_EditableGrid");

        // 1. Wait for grid data
        WaitForText("EditStatus", "Last edit: none");
        Assert.IsNotNull(WaitForName("Alice"), "'Alice' should be visible");
        Assert.IsNotNull(FindByName("Smith"), "'Smith' should be visible");

        // 2. Click "Alice" to start editing FirstName in row 1
        FindByName("Alice").Click();

        // 3. Clear and type new value into the now-focused inline editor
        TypeIntoFocusedEditor("Alicia");

        // 4. Click "Bob" (different row) to commit the FirstName edit
        Assert.IsNotNull(FindByName("Bob"), "'Bob' should be visible while editing");
        FindByName("Bob").Click();

        // 5. Verify first edit committed
        WaitForText("EditStatus", "Last edit: 1:Alicia,Smith");
        Assert.IsNotNull(WaitForName("Alicia"), "'Alicia' should be visible after commit");

        // 6. Click "Smith" to edit LastName in row 1
        Assert.IsNotNull(WaitForName("Smith"), "'Smith' should be visible");
        FindByName("Smith").Click();

        // 7. Clear and type, 8. press Enter to commit
        TypeIntoFocusedEditor("Johnson", commitWithEnter: true);

        // 9. Verify second edit committed
        WaitForText("EditStatus", "Last edit: 1:Alicia,Johnson");
        Assert.IsNotNull(WaitForName("Alicia"), "'Alicia' should still be visible");
        Assert.IsNotNull(WaitForName("Johnson"), "'Johnson' should be visible after commit");
    }

    /// <summary>
    /// Replace the contents of the inline editor that received focus when its cell was clicked.
    /// The editor has no AutomationId, so we clear + type into whatever control currently holds
    /// keyboard focus (the cell click puts the editor in focus).
    /// </summary>
    private void TypeIntoFocusedEditor(string value, bool commitWithEnter = false)
    {
        Thread.Sleep(300); // let the editor mount + take focus after the cell click
        InputInjector.Foreground(HostHwnd);
        InputInjector.ClearViaKeyboard();
        InputInjector.TypeKeys(value);
        if (commitWithEnter)
            InputInjector.TypeKeys(Keys.Enter);
    }

    private UiElement? WaitForName(string name, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var matches = App.Search(name);
            var exact = matches.FirstOrDefault(m => m.Name == name) ?? matches.FirstOrDefault();
            if (exact is not null)
            {
                var selector = !string.IsNullOrEmpty(exact.AutomationId) ? exact.AutomationId! : exact.Selector;
                return Element(selector, exact.AutomationId);
            }
            Thread.Sleep(100);
        }
        return null;
    }
}

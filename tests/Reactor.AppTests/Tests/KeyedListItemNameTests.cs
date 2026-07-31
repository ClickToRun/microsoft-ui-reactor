using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.UI.Reactor.AppTests.Infrastructure;

namespace Microsoft.UI.Reactor.AppTests.Tests;

/// <summary>
/// Issue #951 — the accessible name of a row in a keyed (data-driven)
/// <c>ListView</c> / <c>GridView</c>, measured through the real cross-process
/// UIA tree.
///
/// <para>This is the tier the bug was reported at. Reactor binds keyed items
/// controls to an internally-owned collection of <c>ReactorRow</c> records, and
/// WinUI builds each row's UIA node from that <em>data item</em>. When the row
/// had no name of its own, the data item's string representation became the
/// announced name — so Narrator read out <c>"Row[0]=&lt;guid&gt;"</c> instead of
/// the row's visible content. The in-process selftests (<c>KLIA_*</c>) pin the
/// automation peers directly; these tests confirm the same thing survives to an
/// out-of-process UIA client, which is what assistive technology actually
/// uses.</para>
/// </summary>
[TestClass]
public class KeyedListItemNameTests : AppTestBase
{
    // Must match CollectionFixtures.LeakKeyPrefix — the fixture keys are literal
    // GUID text so a leak is unambiguous rather than coincidentally readable.
    private const string LeakKeyPrefix = "e951a000000000000000000000000";

    [ClassInitialize]
    public static void StartAppSession(TestContext context) => TestSession.AssemblyInit(context);

    [ClassCleanup]
    public static void StopAppSession() => TestSession.AssemblyCleanup();

    private void NavigateToKeyedListFixture() => NavigateToFixture("KeyedList_ItemNames");

    [TestMethod]
    public void KeyedListRows_DoNotExposeInternalRowIdentity()
    {
        NavigateToKeyedListFixture();

        // Guard the guard: if the fixture didn't render, the two searches below
        // would trivially find nothing and the test would pass for the wrong
        // reason.
        Assert.IsNotNull(FindById("KeyedUnnamedList"), "Keyed list fixture did not render.");

        var rowIdentityMatches = App.Search("Row[")
            .Where(m => m.Name?.Contains("Row[", StringComparison.Ordinal) == true)
            .ToList();

        Assert.AreEqual(0, rowIdentityMatches.Count,
            "Keyed list rows must not announce Reactor's internal row identity. Leaked names: "
            + string.Join(", ", rowIdentityMatches.Select(m => $"{m.Type}:'{m.Name}'")));
    }

    [TestMethod]
    public void KeyedListRows_DoNotExposeTheirKey()
    {
        NavigateToKeyedListFixture();

        Assert.IsNotNull(FindById("KeyedUnnamedList"), "Keyed list fixture did not render.");

        // The key is the author's opaque identifier — a database id or GUID. It
        // is never something a user should hear, whatever wrapping it comes in.
        var keyMatches = App.Search(LeakKeyPrefix)
            .Where(m => m.Name?.Contains(LeakKeyPrefix, StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        Assert.AreEqual(0, keyMatches.Count,
            "Keyed list rows must not announce their key. Leaked names: "
            + string.Join(", ", keyMatches.Select(m => $"{m.Type}:'{m.Name}'")));
    }

    [TestMethod]
    public void KeyedListRows_ExposeAuthorDeclaredName()
    {
        NavigateToKeyedListFixture();

        // A composite row (stack / border / card) has no plain text at its
        // template root, so WinUI composes no name for it. .AutomationName(...)
        // on the item view is the supported way to name such a row, and this is
        // the assertion that it reaches a real UIA client — without it the row
        // is silently unnamed, which is how the original bug hid.
        foreach (var expected in new[] { "Apples", "Bananas", "Carrots" }.Select(label => $"Fruit {label}"))
        {
            var row = App.Search(expected)
                .FirstOrDefault(m =>
                    string.Equals(m.Name, expected, StringComparison.Ordinal) &&
                    string.Equals(m.Type, "ListItem", StringComparison.Ordinal));

            Assert.IsNotNull(row,
                $"Expected a list row (ListItem) named '{expected}' in the UIA tree — "
                + ".AutomationName on a keyed item view must name the row itself, not just "
                + "an element inside it.");
        }
    }
}

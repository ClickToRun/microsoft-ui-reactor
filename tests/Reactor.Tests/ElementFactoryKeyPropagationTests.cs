using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;
using Xunit;

namespace Microsoft.UI.Reactor.Tests;

/// <summary>
/// Issue #326 — unit coverage for the keySelector → row <see cref="Element.Key"/>
/// propagation logic used by <see cref="ElementFactory{T}"/>'s recycle path.
/// The reused-container reconcile (GetElement) relies on a different key landing
/// on the row's top-level element so <see cref="Reconciler.CanUpdate"/> flips to
/// false for a different logical item and the row's Component state resets.
/// These exercise the pure projection helper directly (no WinUI thread needed).
/// </summary>
public class ElementFactoryKeyPropagationTests
{
    [Fact]
    public void ApplyItemIdentityKey_Sets_Key_When_Row_Has_None()
    {
        var row = VStack(TextBlock("hello"));
        Assert.Null(row.Key);

        var keyed = ElementFactory<int>.ApplyItemIdentityKey(row, "item-42");

        Assert.Equal("item-42", keyed.Key);
    }

    [Fact]
    public void ApplyItemIdentityKey_Preserves_Concrete_Record_Type()
    {
        // `with` on the base Element reference must clone the most-derived
        // record so downstream Mount/Update dispatch still sees the real type.
        Element row = TextBlock("hello");
        var keyed = ElementFactory<int>.ApplyItemIdentityKey(row, "k");

        Assert.IsType<TextBlockElement>(keyed);
        Assert.Equal("k", keyed.Key);
    }

    [Fact]
    public void ApplyItemIdentityKey_Explicit_Author_Key_Wins()
    {
        // A `.WithKey(...)` written inside the row builder must not be
        // overwritten by the implicit per-item key (issue #326 Q2).
        var row = VStack(TextBlock("hello")).WithKey("author-key");

        var keyed = ElementFactory<int>.ApplyItemIdentityKey(row, "item-42");

        Assert.Equal("author-key", keyed.Key);
        Assert.Same(row, keyed); // no clone allocated when the key already exists
    }

    [Fact]
    public void ApplyItemIdentityKey_Different_Items_Get_Different_Keys()
    {
        // The whole point: two logical items projecting through keySelector
        // produce row elements whose Keys differ, so a recycled container
        // reused across them fails CanUpdate and remounts (state reset).
        var a = ElementFactory<int>.ApplyItemIdentityKey(VStack(TextBlock("a")), "id-a");
        var b = ElementFactory<int>.ApplyItemIdentityKey(VStack(TextBlock("b")), "id-b");

        Assert.NotEqual(a.Key, b.Key);
    }
}

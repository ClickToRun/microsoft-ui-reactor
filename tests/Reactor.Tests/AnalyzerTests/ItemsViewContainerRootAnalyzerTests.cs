using Microsoft.UI.Reactor.Analyzers;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.AnalyzerTests;

/// <summary>
/// Tests for <see cref="ItemsViewContainerRootAnalyzer"/> (<c>REACTOR_ITEMS_002</c>) and its
/// <see cref="ItemsViewContainerRootCodeFix"/>. Stubs the minimum Reactor surface the rule anchors
/// on — the <c>Element</c> hierarchy in <c>Microsoft.UI.Reactor.Core</c> (including
/// <c>ItemContainerElement</c> and a type derived from it), the <c>Factories</c> hub with the real
/// <c>ItemsView(items, keySelector, viewBuilder)</c> signature plus a sibling collection factory of
/// the <em>same</em> shape that carries no container requirement, and the generic fluent modifiers
/// that preserve an element's concrete type through a chain — so the analyzer's type reasoning fires
/// without pulling the framework in.
/// </summary>
public class ItemsViewContainerRootAnalyzerTests
{
    private const string Stubs = @"
using System;
using System.Collections.Generic;

namespace System.Runtime.CompilerServices { public static class IsExternalInit { } }

namespace Microsoft.UI.Reactor.Core
{
    using System;
    using System.Collections.Generic;

    public abstract record Element { public string Key { get; init; } }
    public sealed record BorderElement(Element Child) : Element;
    public sealed record StackPanelElement(Element[] Children) : Element;
    public sealed record TextBlockElement(string Text) : Element;
    public record ItemContainerElement(Element Child) : Element { public bool IsSelected { get; init; } }
    public sealed record FancyContainerElement(Element Child) : ItemContainerElement(Child);
    public sealed record ItemsViewElement<T>(IReadOnlyList<T> Items, Func<T, string> KeySelector, Func<T, int, Element> ViewBuilder) : Element;
    public sealed record TemplatedListViewElement<T> : Element;
}

namespace Microsoft.UI.Reactor
{
    using System;
    using System.Collections.Generic;
    using Microsoft.UI.Reactor.Core;

    public static class Factories
    {
        public static ItemsViewElement<T> ItemsView<T>(
            IReadOnlyList<T> items, Func<T, string> keySelector, Func<T, int, Element> viewBuilder)
            => new ItemsViewElement<T>(items, keySelector, viewBuilder);

        // Sibling collection factory with the IDENTICAL parameter shape but no container
        // requirement — must never trip the rule.
        public static TemplatedListViewElement<T> ListView<T>(
            IReadOnlyList<T> items, Func<T, string> keySelector, Func<T, int, Element> viewBuilder)
            => new TemplatedListViewElement<T>();

        // A near-miss overload on the SAME Factories type with a parameter genuinely named
        // viewBuilder, but a different delegate shape — the Func<T, int, Element> pin must reject it.
        public static TemplatedListViewElement<T> ItemsView<T>(
            IReadOnlyList<T> items, Func<T, string> keySelector, Func<T, string, Element> viewBuilder, bool grouped)
            => new TemplatedListViewElement<T>();

        public static ItemContainerElement ItemContainer(Element child) => new ItemContainerElement(child);
        public static FancyContainerElement FancyContainer(Element child) => new FancyContainerElement(child);
        public static BorderElement Border(Element child) => new BorderElement(child);
        public static StackPanelElement HStack(params Element[] children) => new StackPanelElement(children);
        public static TextBlockElement TextBlock(string text) => new TextBlockElement(text);
    }

    // Generic modifiers preserve the concrete element type through a fluent chain, exactly like
    // the real ElementExtensions.
    public static class ElementStubModifiers
    {
        public static T Margin<T>(this T el, double value) where T : Core.Element => el;
        public static T Padding<T>(this T el, double value) where T : Core.Element => el;
    }
}

// A same-named factory on an unrelated type — the near-miss the semantic gate must reject.
namespace Look
{
    using System;
    using System.Collections.Generic;
    using Microsoft.UI.Reactor.Core;

    public static class Widgets
    {
        public static Element ItemsView<T>(
            IReadOnlyList<T> items, Func<T, string> keySelector, Func<T, int, Element> viewBuilder) => null;
    }
}
";

    private static string Wrap(string body) => Stubs + @"
namespace TestApp
{
    using System;
    using System.Collections.Generic;
    using Microsoft.UI.Reactor;
    using Microsoft.UI.Reactor.Core;
    using static Microsoft.UI.Reactor.Factories;

    public sealed record Product(string Name);

    public static class Rows
    {
        public static ItemContainerElement Typed(Product p) => ItemContainer(TextBlock(p.Name));
        public static Element Loose(Product p) => ItemContainer(TextBlock(p.Name));
        public static Element Build(Product p, int i) => ItemContainer(TextBlock(p.Name));
        public static Element BuildLoose(Product p, int i) => Border(TextBlock(p.Name));
        public static BorderElement BuildBorder(Product p, int i) => Border(TextBlock(p.Name));
        public static ItemContainerElement BuildTyped(Product p, int i) => ItemContainer(TextBlock(p.Name));
        public static TElement BuildGeneric<TElement>(Product p, int i) where TElement : Element => default;
        public static BorderElement BuildOverloaded(Product p, int i) => Border(TextBlock(p.Name));
        public static ItemContainerElement BuildOverloaded(Product p, string s) => ItemContainer(TextBlock(p.Name));
    }

    public static class C
    {
        public static Element Build(IReadOnlyList<Product> products)
        {
" + body + @"
            return null;
        }
    }
}";

    private static Task Verify(string body) =>
        new CSharpAnalyzerTest<ItemsViewContainerRootAnalyzer, DefaultVerifier>
        {
            TestCode = Wrap(body),
        }.RunAsync(TestContext.Current.CancellationToken);

    private static Task Fix(string before, string after) =>
        new CSharpCodeFixTest<ItemsViewContainerRootAnalyzer, ItemsViewContainerRootCodeFix, DefaultVerifier>
        {
            TestCode = Wrap(before),
            FixedCode = Wrap(after),
        }.RunAsync(TestContext.Current.CancellationToken);

    // ── Positive: fires ─────────────────────────────────────────────────

    [Fact]
    public Task Fires_For_Expression_Bodied_NonContainer() =>
        Verify(@"            ItemsView(products, p => p.Name, (p, i) => {|REACTOR_ITEMS_002:Border(TextBlock(p.Name))|});");

    [Fact]
    public Task Fires_For_Expression_Body_With_Trailing_Modifiers() =>
        // The exact gallery shape (G1): the chain's type is still BorderElement.
        Verify(@"            ItemsView(products, p => p.Name, (p, i) => {|REACTOR_ITEMS_002:Border(TextBlock(p.Name)).Margin(4)|});");

    [Fact]
    public Task Fires_For_Layout_Container_Root() =>
        // The gallery's second builder: HStack(...).Padding(8) → StackPanelElement.
        Verify(@"            ItemsView(products, p => p.Name, (p, i) => {|REACTOR_ITEMS_002:HStack(TextBlock(p.Name)).Padding(8)|});");

    [Fact]
    public Task Fires_For_Block_Bodied_Return() =>
        Verify(@"            ItemsView(products, p => p.Name, (p, i) =>
            {
                var text = TextBlock(p.Name);
                return {|REACTOR_ITEMS_002:Border(text)|};
            });");

    [Fact]
    public Task Fires_Only_For_The_NonContainer_Path_Of_A_Multi_Return_Body() =>
        Verify(@"            ItemsView(products, p => p.Name, (p, i) =>
            {
                if (i == 0) return ItemContainer(TextBlock(p.Name));
                return {|REACTOR_ITEMS_002:Border(TextBlock(p.Name))|};
            });");

    [Fact]
    public Task Fires_Once_Per_Offending_Return_Path() =>
        Verify(@"            ItemsView(products, p => p.Name, (p, i) =>
            {
                if (i == 0) return {|REACTOR_ITEMS_002:HStack(TextBlock(p.Name))|};
                return {|REACTOR_ITEMS_002:Border(TextBlock(p.Name))|};
            });");

    [Fact]
    public Task Fires_For_Named_ViewBuilder_Argument() =>
        Verify(@"            ItemsView(products, keySelector: p => p.Name, viewBuilder: (p, i) => {|REACTOR_ITEMS_002:Border(TextBlock(p.Name))|});");

    [Fact]
    public Task Fires_For_Named_Arguments_In_Reversed_Order() =>
        Verify(@"            ItemsView(products, viewBuilder: (p, i) => {|REACTOR_ITEMS_002:Border(TextBlock(p.Name))|}, keySelector: p => p.Name);");

    [Fact]
    public Task Fires_For_Qualified_Factories_Call() =>
        Verify(@"            Factories.ItemsView(products, p => p.Name, (p, i) => {|REACTOR_ITEMS_002:Border(TextBlock(p.Name))|});");

    [Fact]
    public Task Fires_For_Conditional_With_Uniform_NonContainer_Branches() =>
        // Both arms are BorderElement, so the conditional's own type is BorderElement — the
        // realized root can never be a container.
        Verify(@"            ItemsView(products, p => p.Name, (p, i) => {|REACTOR_ITEMS_002:i == 0 ? Border(TextBlock(p.Name)) : Border(null)|});");

    [Fact]
    public Task Fires_For_With_Expression_On_A_NonContainer() =>
        Verify(@"            ItemsView(products, p => p.Name, (p, i) => {|REACTOR_ITEMS_002:Border(TextBlock(p.Name)) with { Key = p.Name }|});");

    [Fact]
    public Task Fires_For_Anonymous_Method() =>
        Verify(@"            ItemsView(products, p => p.Name, delegate (Product p, int i)
            {
                return {|REACTOR_ITEMS_002:Border(TextBlock(p.Name))|};
            });");

    [Fact]
    public Task Fires_For_Parameterless_Anonymous_Method() =>
        // `delegate { … }` is convertible to any delegate type, so it binds to viewBuilder with no
        // parameter list at all.
        Verify(@"            ItemsView(products, p => p.Name, delegate
            {
                return {|REACTOR_ITEMS_002:Border(TextBlock(""row""))|};
            });");

    [Fact]
    public Task Fires_For_Helper_Typed_To_A_Concrete_NonContainer() =>
        Verify(@"            ItemsView(products, p => p.Name, (p, i) => {|REACTOR_ITEMS_002:Border(Rows.Loose(p))|});");

    [Fact]
    public Task Fires_For_Method_Group_Declared_To_Return_A_Concrete_NonContainer() =>
        // No lambda, but the helper's declared return type decides every realized root just as
        // surely — the same soundness test applies.
        Verify(@"            ItemsView(products, p => p.Name, {|REACTOR_ITEMS_002:Rows.BuildBorder|});");

    [Fact]
    public Task Fires_For_The_Overload_A_Method_Group_Actually_Converts_To() =>
        // BuildOverloaded has a (Product, string) overload returning ItemContainerElement; the
        // delegate conversion selects the (Product, int) one, which returns BorderElement.
        Verify(@"            ItemsView(products, p => p.Name, {|REACTOR_ITEMS_002:Rows.BuildOverloaded|});");

    // ── Negative: silent ────────────────────────────────────────────────

    [Fact]
    public Task Silent_For_ItemContainer_Root() =>
        Verify(@"            ItemsView(products, p => p.Name, (p, i) => ItemContainer(TextBlock(p.Name)));");

    [Fact]
    public Task Silent_For_ItemContainer_With_Trailing_Modifiers() =>
        Verify(@"            ItemsView(products, p => p.Name, (p, i) => ItemContainer(TextBlock(p.Name)).Padding(8));");

    [Fact]
    public Task Silent_For_ItemContainer_With_Expression() =>
        Verify(@"            ItemsView(products, p => p.Name, (p, i) => ItemContainer(TextBlock(p.Name)) with { IsSelected = true });");

    [Fact]
    public Task Silent_For_A_Type_Derived_From_ItemContainer() =>
        Verify(@"            ItemsView(products, p => p.Name, (p, i) => FancyContainer(TextBlock(p.Name)));");

    [Fact]
    public Task Silent_For_Helper_Returning_ItemContainerElement() =>
        Verify(@"            ItemsView(products, p => p.Name, (p, i) => Rows.Typed(p));");

    [Fact]
    public Task Silent_For_Helper_Returning_Only_Element() =>
        // Statically known as the base type only — the runtime value may well be a container, so
        // the rule must stay silent and leave this to the mount-time guard.
        Verify(@"            ItemsView(products, p => p.Name, (p, i) => Rows.Loose(p));");

    [Fact]
    public Task Silent_For_Null_Return() =>
        Verify(@"            ItemsView(products, p => p.Name, (p, i) => null);");

    [Fact]
    public Task Silent_For_Conditional_With_Mixed_Branch_Types() =>
        Verify(@"            ItemsView(products, p => p.Name, (p, i) => i == 0 ? (Element)ItemContainer(TextBlock(p.Name)) : Border(null));");

    [Fact]
    public Task Silent_For_Switch_Expression_With_Mixed_Arms() =>
        Verify(@"            ItemsView(products, p => p.Name, (p, i) => i switch { 0 => (Element)ItemContainer(TextBlock(p.Name)), _ => Border(null) });");

    [Fact]
    public Task Silent_For_Switch_Expression_With_Container_Arms() =>
        Verify(@"            ItemsView(products, p => p.Name, (p, i) => i switch { 0 => ItemContainer(TextBlock(p.Name)), _ => ItemContainer(null) });");

    [Fact]
    public Task Silent_For_Method_Group_Declared_To_Return_Element() =>
        // Statically known as the base type only — the helper may well return a container, so this
        // is left to the mount-time guard. This is the shape samples/Reactor.TestApp uses.
        Verify(@"            ItemsView(products, p => p.Name, Rows.BuildLoose);");

    [Fact]
    public Task Silent_For_Method_Group_Returning_ItemContainerElement() =>
        Verify(@"            ItemsView(products, p => p.Name, Rows.BuildTyped);");

    [Fact]
    public Task Silent_For_Method_Group_With_A_Type_Parameter_Return() =>
        Verify(@"            ItemsView(products, p => p.Name, Rows.BuildGeneric<ItemContainerElement>);");

    [Fact]
    public Task Silent_For_A_Delegate_Typed_Local() =>
        // A local holding a delegate is opaque: the analyzer cannot see what it points at.
        Verify(@"            Func<Product, int, Element> builder = (p, i) => Border(TextBlock(p.Name));
            ItemsView(products, p => p.Name, builder);");

    [Fact]
    public Task Silent_For_Return_Inside_A_Nested_Lambda() =>
        // The inner `return` belongs to an unconstrained Func<Element>, not to the viewBuilder.
        // The nested lambda is deliberately block-bodied so it owns a real ReturnStatementSyntax —
        // an expression-bodied one would pass even without the descend guard.
        Verify(@"            ItemsView(products, p => p.Name, (p, i) =>
            {
                Func<Element> inner = () => { return Border(TextBlock(p.Name)); };
                return ItemContainer(inner());
            });");

    [Fact]
    public Task Silent_For_Return_Inside_A_Local_Function() =>
        Verify(@"            ItemsView(products, p => p.Name, (p, i) =>
            {
                Element Inner() { return Border(TextBlock(p.Name)); }
                return ItemContainer(Inner());
            });");

    [Fact]
    public Task Silent_For_Sibling_Collection_Factory() =>
        // ListView has the same parameter shape and no container requirement.
        Verify(@"            ListView(products, p => p.Name, (p, i) => Border(TextBlock(p.Name)));");

    [Fact]
    public Task Silent_For_SameNamed_ItemsView_On_An_Unrelated_Type() =>
        Verify(@"            Look.Widgets.ItemsView(products, p => p.Name, (p, i) => Border(TextBlock(p.Name)));");

    [Fact]
    public Task Silent_For_A_ViewBuilder_Overload_With_A_Different_Delegate_Shape() =>
        // Same Factories type, same parameter name, but Func<T, string, Element> rather than
        // Func<T, int, Element> — a future grouped/sectioned builder must not be misread as the
        // per-item one whose root ItemsView constrains.
        Verify(@"            ItemsView(products, p => p.Name, (p, s) => Border(TextBlock(p.Name)), true);");

    // ── Code fix ────────────────────────────────────────────────────────

    [Fact]
    public Task Fix_Wraps_The_Expression_Body() => Fix(
        before: @"            ItemsView(products, p => p.Name, (p, i) => {|REACTOR_ITEMS_002:Border(TextBlock(p.Name))|});",
        after: @"            ItemsView(products, p => p.Name, (p, i) => ItemContainer(Border(TextBlock(p.Name))));");

    [Fact]
    public Task Fix_Wraps_The_Whole_Chain_Including_Trailing_Modifiers() => Fix(
        // ItemContainer(Border(...).Margin(4)) — NOT ItemContainer(Border(...)).Margin(4), which
        // would silently re-target every modifier from the border onto the container.
        before: @"            ItemsView(products, p => p.Name, (p, i) => {|REACTOR_ITEMS_002:Border(TextBlock(p.Name)).Margin(4).Padding(8)|});",
        after: @"            ItemsView(products, p => p.Name, (p, i) => ItemContainer(Border(TextBlock(p.Name)).Margin(4).Padding(8)));");

    [Fact]
    public Task Fix_Wraps_A_Return_Statement_In_A_Block_Body() => Fix(
        before: @"            ItemsView(products, p => p.Name, (p, i) =>
            {
                return {|REACTOR_ITEMS_002:HStack(TextBlock(p.Name))|};
            });",
        after: @"            ItemsView(products, p => p.Name, (p, i) =>
            {
                return ItemContainer(HStack(TextBlock(p.Name)));
            });");

    [Fact]
    public Task Fix_Wraps_A_Conditional_Whole() => Fix(
        before: @"            ItemsView(products, p => p.Name, (p, i) => {|REACTOR_ITEMS_002:i == 0 ? Border(TextBlock(p.Name)) : Border(null)|});",
        after: @"            ItemsView(products, p => p.Name, (p, i) => ItemContainer(i == 0 ? Border(TextBlock(p.Name)) : Border(null)));");

    [Fact]
    public Task Fix_Wraps_Every_Offending_Return_In_One_Document() => Fix(
        // Two diagnostics in one lambda — exercises the batch / fix-all path.
        before: @"            ItemsView(products, p => p.Name, (p, i) =>
            {
                if (i == 0) return {|REACTOR_ITEMS_002:HStack(TextBlock(p.Name))|};
                return {|REACTOR_ITEMS_002:Border(TextBlock(p.Name))|};
            });",
        after: @"            ItemsView(products, p => p.Name, (p, i) =>
            {
                if (i == 0) return ItemContainer(HStack(TextBlock(p.Name)));
                return ItemContainer(Border(TextBlock(p.Name)));
            });");

    [Fact]
    public Task Fix_Qualifies_ItemContainer_When_The_Factories_Static_Import_Is_Absent()
    {
        // No `using static Factories;` here, so a bare `ItemContainer(...)` would not bind — the
        // fix must emit the qualified form.
        const string Before = @"
namespace Unimported
{
    using System.Collections.Generic;
    using Microsoft.UI.Reactor;
    using Microsoft.UI.Reactor.Core;

    public static class D
    {
        public static Element Build(IReadOnlyList<string> products) =>
            Factories.ItemsView(products, p => p, (p, i) => {|REACTOR_ITEMS_002:Factories.Border(Factories.TextBlock(p))|});
    }
}";
        const string After = @"
namespace Unimported
{
    using System.Collections.Generic;
    using Microsoft.UI.Reactor;
    using Microsoft.UI.Reactor.Core;

    public static class D
    {
        public static Element Build(IReadOnlyList<string> products) =>
            Factories.ItemsView(products, p => p, (p, i) => Factories.ItemContainer(Factories.Border(Factories.TextBlock(p))));
    }
}";

        return new CSharpCodeFixTest<ItemsViewContainerRootAnalyzer, ItemsViewContainerRootCodeFix, DefaultVerifier>
        {
            TestCode = Stubs + Before,
            FixedCode = Stubs + After,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public Task Fix_Qualifies_ItemContainer_When_A_Same_Named_Member_Shadows_The_Factory()
    {
        // A member of the enclosing type wins name lookup over `using static`, so emitting the bare
        // name here would bind to the wrong method (and change behaviour silently).
        const string Before = @"
namespace Shadowed
{
    using System.Collections.Generic;
    using Microsoft.UI.Reactor;
    using Microsoft.UI.Reactor.Core;
    using static Microsoft.UI.Reactor.Factories;

    public static class D
    {
        private static Element ItemContainer(Element child) => child;

        public static Element Build(IReadOnlyList<string> products) =>
            ItemsView(products, p => p, (p, i) => {|REACTOR_ITEMS_002:Border(TextBlock(p))|});
    }
}";
        const string After = @"
namespace Shadowed
{
    using System.Collections.Generic;
    using Microsoft.UI.Reactor;
    using Microsoft.UI.Reactor.Core;
    using static Microsoft.UI.Reactor.Factories;

    public static class D
    {
        private static Element ItemContainer(Element child) => child;

        public static Element Build(IReadOnlyList<string> products) =>
            ItemsView(products, p => p, (p, i) => Factories.ItemContainer(Border(TextBlock(p))));
    }
}";

        return new CSharpCodeFixTest<ItemsViewContainerRootAnalyzer, ItemsViewContainerRootCodeFix, DefaultVerifier>
        {
            TestCode = Stubs + Before,
            FixedCode = Stubs + After,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public Task Fix_Fully_Qualifies_ItemContainer_When_The_Reactor_Namespace_Is_Not_Imported()
    {
        // Neither `using Microsoft.UI.Reactor;` nor the static import is present, so
        // ToMinimalDisplayString has to render the containing type as a dotted, namespace-qualified
        // name. Exercises the multi-segment path through BuildFactoryReference; the harness compiles
        // FixedCode, so this fails if the emitted syntax is not a valid expression.
        const string Before = @"
namespace FullyQualified
{
    using System.Collections.Generic;
    using Microsoft.UI.Reactor.Core;

    public static class D
    {
        public static Element Build(IReadOnlyList<string> products) =>
            Microsoft.UI.Reactor.Factories.ItemsView(products, p => p, (p, i) => {|REACTOR_ITEMS_002:Microsoft.UI.Reactor.Factories.Border(Microsoft.UI.Reactor.Factories.TextBlock(p))|});
    }
}";
        const string After = @"
namespace FullyQualified
{
    using System.Collections.Generic;
    using Microsoft.UI.Reactor.Core;

    public static class D
    {
        public static Element Build(IReadOnlyList<string> products) =>
            Microsoft.UI.Reactor.Factories.ItemsView(products, p => p, (p, i) => Microsoft.UI.Reactor.Factories.ItemContainer(Microsoft.UI.Reactor.Factories.Border(Microsoft.UI.Reactor.Factories.TextBlock(p))));
    }
}";

        return new CSharpCodeFixTest<ItemsViewContainerRootAnalyzer, ItemsViewContainerRootCodeFix, DefaultVerifier>
        {
            TestCode = Stubs + Before,
            FixedCode = Stubs + After,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public Task Fix_Is_Not_Offered_For_A_Method_Group()
    {
        // There is no returned expression at the call site, so wrapping would produce
        // `ItemContainer(Rows.BuildBorder)`, which does not compile. TestCode == FixedCode asserts
        // the diagnostic still fires but no rewrite happens.
        var code = Wrap(@"            ItemsView(products, p => p.Name, {|REACTOR_ITEMS_002:Rows.BuildBorder|});");

        return new CSharpCodeFixTest<ItemsViewContainerRootAnalyzer, ItemsViewContainerRootCodeFix, DefaultVerifier>
        {
            TestCode = code,
            FixedCode = code,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public Task Fix_Is_Withheld_When_No_ItemContainer_Factory_Exists()
    {
        // A Reactor surface without the ItemContainer factory: the diagnostic still fires (the
        // element type is what the rule reasons about) but no fix may be offered, because there is
        // nothing that would compile. TestCode == FixedCode asserts no rewrite happens.
        const string NoFactoryStubs = @"
using System;
using System.Collections.Generic;

namespace System.Runtime.CompilerServices { public static class IsExternalInit { } }

namespace Microsoft.UI.Reactor.Core
{
    using System;
    using System.Collections.Generic;

    public abstract record Element { }
    public sealed record BorderElement(Element Child) : Element;
    public sealed record TextBlockElement(string Text) : Element;
    public record ItemContainerElement(Element Child) : Element;
    public sealed record ItemsViewElement<T>(IReadOnlyList<T> Items, Func<T, string> KeySelector, Func<T, int, Element> ViewBuilder) : Element;
}

namespace Microsoft.UI.Reactor
{
    using System;
    using System.Collections.Generic;
    using Microsoft.UI.Reactor.Core;

    public static class Factories
    {
        public static ItemsViewElement<T> ItemsView<T>(
            IReadOnlyList<T> items, Func<T, string> keySelector, Func<T, int, Element> viewBuilder)
            => new ItemsViewElement<T>(items, keySelector, viewBuilder);

        public static BorderElement Border(Element child) => new BorderElement(child);
        public static TextBlockElement TextBlock(string text) => new TextBlockElement(text);
        // NOTE: no ItemContainer factory in this compilation.
    }
}

namespace NoFactory
{
    using System.Collections.Generic;
    using Microsoft.UI.Reactor;
    using Microsoft.UI.Reactor.Core;
    using static Microsoft.UI.Reactor.Factories;

    public static class D
    {
        public static Element Build(IReadOnlyList<string> products) =>
            ItemsView(products, p => p, (p, i) => {|REACTOR_ITEMS_002:Border(TextBlock(p))|});
    }
}";

        return new CSharpCodeFixTest<ItemsViewContainerRootAnalyzer, ItemsViewContainerRootCodeFix, DefaultVerifier>
        {
            TestCode = NoFactoryStubs,
            FixedCode = NoFactoryStubs,
        }.RunAsync(TestContext.Current.CancellationToken);
    }
}

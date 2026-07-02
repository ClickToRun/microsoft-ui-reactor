using Microsoft.UI.Reactor.Analyzers;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.AnalyzerTests;

/// <summary>
/// Tests for <c>REACTOR_HOOKS_010</c> (mutate-then-set reference state) and
/// <see cref="MutateThenSetCodeFix"/>. The state is seeded from a field so the initial value is
/// not itself an allocation (which would also trip <c>REACTOR_HOOKS_013</c>).
/// </summary>
public class MutateThenSetAnalyzerTests
{
    private const string Stubs = @"
namespace Microsoft.UI.Reactor.Core
{
    public class RenderContext { }

    // A record HAS value equality, so mutate-then-set on it re-renders correctly (negative case).
    public record ValueList
    {
        public void Add(string s) { }
    }

    public abstract class Component
    {
        protected internal RenderContext Context { get; } = new RenderContext();
        public abstract string Render();
        protected (T Value, System.Action<T> Set) UseState<T>(T initialValue, bool threadSafe = false) => (initialValue, _ => { });

        protected System.Collections.Generic.List<string> Seed = new System.Collections.Generic.List<string>();
        protected ValueList ValueSeed = new ValueList();
    }
}
";

    private static Task VerifyAnalyzer(string body) =>
        new CSharpAnalyzerTest<HookRulesAnalyzer, DefaultVerifier>
        {
            TestCode = Stubs + body,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        }.RunAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Add_Then_Set_Same_Reference_Flags()
    {
        await VerifyAnalyzer(@"
class C : Microsoft.UI.Reactor.Core.Component
{
    public override string Render()
    {
        var (items, setItems) = UseState(Seed);
        items.Add(""x"");
        {|REACTOR_HOOKS_010:setItems(items)|};
        return """";
    }
}");
    }

    [Fact]
    public async Task Clear_Then_Set_Same_Reference_Flags()
    {
        await VerifyAnalyzer(@"
class C : Microsoft.UI.Reactor.Core.Component
{
    public override string Render()
    {
        var (items, setItems) = UseState(Seed);
        items.Clear();
        {|REACTOR_HOOKS_010:setItems(items)|};
        return """";
    }
}");
    }

    [Fact]
    public async Task Indexer_Set_Then_Set_Same_Reference_Flags()
    {
        await VerifyAnalyzer(@"
class C : Microsoft.UI.Reactor.Core.Component
{
    public override string Render()
    {
        var (items, setItems) = UseState(Seed);
        items[0] = ""x"";
        {|REACTOR_HOOKS_010:setItems(items)|};
        return """";
    }
}");
    }

    // Negative: a record has value equality, so the setter re-renders on the mutated copy.
    [Fact]
    public async Task Mutate_Then_Set_ValueEqualityType_DoesNotFlag()
    {
        await VerifyAnalyzer(@"
class C : Microsoft.UI.Reactor.Core.Component
{
    public override string Render()
    {
        var (items, setItems) = UseState(ValueSeed);
        items.Add(""x"");
        setItems(items);
        return """";
    }
}");
    }

    // Negative: a defensive copy before the mutation means the setter receives a NEW reference.
    [Fact]
    public async Task Defensive_Copy_Before_Mutation_DoesNotFlag()
    {
        await VerifyAnalyzer(@"
class C : Microsoft.UI.Reactor.Core.Component
{
    public override string Render()
    {
        var (items, setItems) = UseState(Seed);
        items = new System.Collections.Generic.List<string>(items);
        items.Add(""x"");
        setItems(items);
        return """";
    }
}");
    }

    // Near-miss: setX(x) where x is NOT a state local (setX and x are ordinary locals).
    [Fact]
    public async Task Setter_Not_From_UseState_DoesNotFlag()
    {
        await VerifyAnalyzer(@"
class C : Microsoft.UI.Reactor.Core.Component
{
    public override string Render()
    {
        var items = new System.Collections.Generic.List<string>();
        System.Action<System.Collections.Generic.List<string>> setItems = _ => { };
        items.Add(""x"");
        setItems(items);
        return """";
    }
}");
    }

    [Fact]
    public async Task CodeFix_Rewrites_Add_To_New_Collection_Value()
    {
        var before = Stubs + @"
class C : Microsoft.UI.Reactor.Core.Component
{
    public override string Render()
    {
        var (items, setItems) = UseState(Seed);
        items.Add(""x"");
        {|REACTOR_HOOKS_010:setItems(items)|};
        return """";
    }
}";

        var after = Stubs + @"
class C : Microsoft.UI.Reactor.Core.Component
{
    public override string Render()
    {
        var (items, setItems) = UseState(Seed);
        setItems([.. items, ""x""]);
        return """";
    }
}";

        await new CSharpCodeFixTest<HookRulesAnalyzer, MutateThenSetCodeFix, DefaultVerifier>
        {
            TestCode = before,
            FixedCode = after,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
            CodeActionEquivalenceKey = HookRulesAnalyzer.MutateThenSetId,
        }.RunAsync(TestContext.Current.CancellationToken);
    }
}

using Microsoft.UI.Reactor.Analyzers;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.AnalyzerTests;

/// <summary>
/// Tests for <see cref="StaticRegisterLambdaAnalyzer"/> (<c>REACTOR_DESC_001</c>) and its
/// <see cref="StaticRegisterLambdaCodeFix"/>. Stubs a minimal <c>ControlRegistry</c> in the
/// real namespace so the analyzer's semantic confirmation fires without pulling the framework
/// in; a same-named <c>Register</c> on an unrelated type proves the near-miss guard.
/// </summary>
public class StaticRegisterLambdaAnalyzerTests
{
    // Minimal shape: ControlRegistry lives in Microsoft.UI.Reactor.Core.V1Protocol (the
    // semantic gate keys off type name + namespace), each entry point takes a single
    // Func<object> factory. NotTheRegistry mirrors the 'Register' name on a different type so
    // the near-miss (name matches, symbol does not) can be exercised. The `using` for the
    // registry namespace sits at the top so appended user code can name ControlRegistry
    // unqualified (a using must precede all type declarations).
    private const string Stubs = @"
using System;
using Microsoft.UI.Reactor.Core.V1Protocol;

namespace Microsoft.UI.Reactor.Core.V1Protocol
{
    public static class ControlRegistry
    {
        public static void Register<TElement, TControl>(Func<object> handlerFactory) {}
        public static void RegisterForDerivedTypes<TBase, TControl>(Func<object> handlerFactory) {}
        public static void RegisterDecorator<TElement>(Func<object> handlerFactory) {}
        public static void RegisterDecoratorForDerivedTypes<TBase>(Func<object> handlerFactory) {}
    }
}

public class MyElement {}
public class MyControl {}
public class MyHandler
{
    public MyHandler() {}
    public MyHandler(int captured) {}
}

public static class NotTheRegistry
{
    public static void Register<TElement, TControl>(Func<object> handlerFactory) {}
}
";

    // ── Positive ────────────────────────────────────────────────────────

    [Fact]
    public async Task Fires_For_NonStatic_Register_Lambda()
    {
        var source = Stubs + @"
class C
{
    void M()
    {
        ControlRegistry.Register<MyElement, MyControl>({|REACTOR_DESC_001:() => new MyHandler()|});
    }
}";

        await new CSharpAnalyzerTest<StaticRegisterLambdaAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_For_RegisterForDerivedTypes()
    {
        var source = Stubs + @"
class C
{
    void M()
    {
        ControlRegistry.RegisterForDerivedTypes<MyElement, MyControl>({|REACTOR_DESC_001:() => new MyHandler()|});
    }
}";

        await new CSharpAnalyzerTest<StaticRegisterLambdaAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_For_RegisterDecorator()
    {
        var source = Stubs + @"
class C
{
    void M()
    {
        ControlRegistry.RegisterDecorator<MyElement>({|REACTOR_DESC_001:() => new MyHandler()|});
    }
}";

        await new CSharpAnalyzerTest<StaticRegisterLambdaAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_For_RegisterDecoratorForDerivedTypes()
    {
        var source = Stubs + @"
class C
{
    void M()
    {
        ControlRegistry.RegisterDecoratorForDerivedTypes<MyElement>({|REACTOR_DESC_001:() => new MyHandler()|});
    }
}";

        await new CSharpAnalyzerTest<StaticRegisterLambdaAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Negative ────────────────────────────────────────────────────────

    [Fact]
    public async Task No_Diagnostic_When_Already_Static()
    {
        var source = Stubs + @"
class C
{
    void M()
    {
        ControlRegistry.Register<MyElement, MyControl>(static () => new MyHandler());
    }
}";

        await new CSharpAnalyzerTest<StaticRegisterLambdaAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task No_Diagnostic_For_Method_Group_Argument()
    {
        // A method group has no lambda modifiers to make static — nothing to flag.
        var source = Stubs + @"
class C
{
    static object CreateHandler() => new MyHandler();
    void M()
    {
        ControlRegistry.Register<MyElement, MyControl>(CreateHandler);
    }
}";

        await new CSharpAnalyzerTest<StaticRegisterLambdaAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Near-miss (syntactic name match, different symbol) ───────────────

    [Fact]
    public async Task No_Diagnostic_For_Unrelated_Register_Method()
    {
        // Same member name ('Register') and shape, but the symbol is NOT ControlRegistry —
        // the semantic gate must keep this quiet.
        var source = Stubs + @"
class C
{
    void M()
    {
        NotTheRegistry.Register<MyElement, MyControl>(() => new MyHandler());
    }
}";

        await new CSharpAnalyzerTest<StaticRegisterLambdaAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Code fix ────────────────────────────────────────────────────────

    [Fact]
    public async Task CodeFix_Inserts_Static()
    {
        var before = Stubs + @"
class C
{
    void M()
    {
        ControlRegistry.Register<MyElement, MyControl>({|REACTOR_DESC_001:() => new MyHandler()|});
    }
}";

        var after = Stubs + @"
class C
{
    void M()
    {
        ControlRegistry.Register<MyElement, MyControl>(static () => new MyHandler());
    }
}";

        await new CSharpCodeFixTest<StaticRegisterLambdaAnalyzer, StaticRegisterLambdaCodeFix, DefaultVerifier>
        {
            TestCode = before,
            FixedCode = after,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CodeFix_Not_Offered_For_Capturing_Lambda()
    {
        // A capturing lambda cannot compile with 'static', so the analyzer still reports the
        // nudge but no code fix is offered: TestCode == FixedCode (diagnostic persists, no
        // rewrite). This is the "emit the diagnostic but NO auto-fix" contract.
        var code = Stubs + @"
class C
{
    void M()
    {
        int captured = 5;
        ControlRegistry.Register<MyElement, MyControl>({|REACTOR_DESC_001:() => new MyHandler(captured)|});
    }
}";

        await new CSharpCodeFixTest<StaticRegisterLambdaAnalyzer, StaticRegisterLambdaCodeFix, DefaultVerifier>
        {
            TestCode = code,
            FixedCode = code,
        }.RunAsync(TestContext.Current.CancellationToken);
    }
}

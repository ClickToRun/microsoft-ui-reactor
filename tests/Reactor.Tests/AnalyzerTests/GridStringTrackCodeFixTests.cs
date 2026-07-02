using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.UI.Reactor.Analyzers;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.AnalyzerTests;

/// <summary>
/// Tests for <see cref="GridStringTrackCodeFix"/> — the id-less spec-060 §4.5
/// code fix registered on the compiler diagnostic <c>CS0618</c> for the obsolete
/// <c>Grid(string[], string[], …)</c> overload. No analyzer produces the
/// diagnostic (the compiler does), so the fix test pairs
/// <see cref="EmptyDiagnosticAnalyzer"/> with the code fix and turns on compiler
/// <b>warning</b> verification so <c>CS0618</c> is surfaced and fixable.
/// </summary>
public class GridStringTrackCodeFixTests
{
    // Minimal Reactor-shaped surface: the two overloaded Grid factories (the
    // string one flagged [Obsolete(error:false)] so CS0618 fires) plus a GridSize
    // whose shape matches the real type — Auto is a PROPERTY, Star/Px are methods.
    private const string Stubs = @"
namespace Microsoft.UI.Reactor
{
    public abstract class Element { }

    public sealed class TextBlockElement : Element
    {
        public TextBlockElement(string text) { Text = text; }
        public string Text { get; }
    }

    public struct GridSize
    {
        public static GridSize Auto { get { return default; } }
        public static GridSize Star(double weight = 1) { return default; }
        public static GridSize Px(double pixels) { return default; }
    }

    public static class Factories
    {
        public static TextBlockElement TextBlock(string text) { return new TextBlockElement(text); }

        public static Element Grid(GridSize[] columns, GridSize[] rows, params Element[] children) { return null; }

        [System.Obsolete(""Use Grid(GridSize[], GridSize[], ...) — GridSize.Star/.Auto/.Px helpers."", error: false)]
        public static Element Grid(string[] columns, string[] rows, params Element[] children) { return null; }
    }

    [System.Obsolete(""Legacy helper."", error: false)]
    public static class Legacy
    {
        public static Element Build() { return null; }
    }
}
";

    private static CSharpCodeFixTest<EmptyDiagnosticAnalyzer, GridStringTrackCodeFix, DefaultVerifier> MakeTest(
        string testCode, string fixedCode)
    {
        var test = new CSharpCodeFixTest<EmptyDiagnosticAnalyzer, GridStringTrackCodeFix, DefaultVerifier>
        {
            TestCode = testCode,
            FixedCode = fixedCode,
            // The fix keys off the compiler's own obsolete WARNING, so warnings
            // must be part of the verified diagnostic set.
            CompilerDiagnostics = CompilerDiagnostics.Warnings,
        };

        // The stub deliberately omits XML docs on its public shims — ignore the
        // resulting doc-comment noise so only the CS0618 we care about is asserted.
        test.DisabledDiagnostics.Add("CS1591");
        // Collection expressions ("*"-style tracks) need C# 12+.
        test.SolutionTransforms.Add(LatestLanguageVersion);

        return test;
    }

    private static Microsoft.CodeAnalysis.Solution LatestLanguageVersion(
        Microsoft.CodeAnalysis.Solution solution, Microsoft.CodeAnalysis.ProjectId projectId)
    {
        var project = solution.GetProject(projectId)!;
        var options = (CSharpParseOptions)project.ParseOptions!;
        return solution.WithProjectParseOptions(projectId, options.WithLanguageVersion(LanguageVersion.Latest));
    }

    // ── The stub actually produces CS0618 (guards the [Obsolete] wiring) ──

    [Fact]
    public async Task Obsolete_String_Overload_Emits_CS0618()
    {
        var source = Stubs + @"
namespace TestApp
{
    using Microsoft.UI.Reactor;
    using static Microsoft.UI.Reactor.Factories;

    public static class C
    {
        public static Element Build() =>
            {|CS0618:Grid(new string[] { ""*"" }, new string[] { ""*"" })|};
    }
}";

        var test = new CSharpAnalyzerTest<EmptyDiagnosticAnalyzer, DefaultVerifier>
        {
            TestCode = source,
            CompilerDiagnostics = CompilerDiagnostics.Warnings,
        };
        test.DisabledDiagnostics.Add("CS1591");
        test.SolutionTransforms.Add(LatestLanguageVersion);

        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Fix round-trip: inline collection-expression literal arrays ──────

    [Fact]
    public async Task Fix_Rewrites_CollectionExpression_Tracks()
    {
        var before = Stubs + @"
namespace TestApp
{
    using Microsoft.UI.Reactor;
    using static Microsoft.UI.Reactor.Factories;

    public static class C
    {
        public static Element Build() =>
            {|CS0618:Grid([""*"", ""Auto"", ""200""], [""*""], TextBlock(""x""))|};
    }
}";

        var after = Stubs + @"
namespace TestApp
{
    using Microsoft.UI.Reactor;
    using static Microsoft.UI.Reactor.Factories;

    public static class C
    {
        public static Element Build() =>
            Grid([GridSize.Star(), GridSize.Auto, GridSize.Px(200)], [GridSize.Star()], TextBlock(""x""));
    }
}";

        await MakeTest(before, after).RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Fix round-trip: star weights + pixels ───────────────────────────

    [Fact]
    public async Task Fix_Rewrites_Star_Weights_And_Pixels()
    {
        var before = Stubs + @"
namespace TestApp
{
    using Microsoft.UI.Reactor;
    using static Microsoft.UI.Reactor.Factories;

    public static class C
    {
        public static Element Build() =>
            {|CS0618:Grid([""2*"", ""1.5*"", ""120""], [""auto""])|};
    }
}";

        var after = Stubs + @"
namespace TestApp
{
    using Microsoft.UI.Reactor;
    using static Microsoft.UI.Reactor.Factories;

    public static class C
    {
        public static Element Build() =>
            Grid([GridSize.Star(2), GridSize.Star(1.5), GridSize.Px(120)], [GridSize.Auto]);
    }
}";

        await MakeTest(before, after).RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Fix round-trip: new[] { ... } implicit array ────────────────────

    [Fact]
    public async Task Fix_Rewrites_Implicit_Array_Tracks()
    {
        var before = Stubs + @"
namespace TestApp
{
    using Microsoft.UI.Reactor;
    using static Microsoft.UI.Reactor.Factories;

    public static class C
    {
        public static Element Build() =>
            {|CS0618:Grid(new[] { ""*"", ""Auto"" }, new[] { ""*"" })|};
    }
}";

        var after = Stubs + @"
namespace TestApp
{
    using Microsoft.UI.Reactor;
    using static Microsoft.UI.Reactor.Factories;

    public static class C
    {
        public static Element Build() =>
            Grid(new[] { GridSize.Star(), GridSize.Auto }, new[] { GridSize.Star() });
    }
}";

        await MakeTest(before, after).RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Fix round-trip: new string[] { ... } explicit array ─────────────

    [Fact]
    public async Task Fix_Rewrites_Explicit_String_Array_Tracks()
    {
        var before = Stubs + @"
namespace TestApp
{
    using Microsoft.UI.Reactor;
    using static Microsoft.UI.Reactor.Factories;

    public static class C
    {
        public static Element Build() =>
            {|CS0618:Grid(new string[] { ""*"", ""200"" }, new string[] { ""Auto"" })|};
    }
}";

        var after = Stubs + @"
namespace TestApp
{
    using Microsoft.UI.Reactor;
    using static Microsoft.UI.Reactor.Factories;

    public static class C
    {
        public static Element Build() =>
            Grid(new GridSize[] { GridSize.Star(), GridSize.Px(200) }, new GridSize[] { GridSize.Auto });
    }
}";

        await MakeTest(before, after).RunAsync(TestContext.Current.CancellationToken);
    }

    // ── No fix: a variable string[] (track values not visible) ──────────

    [Fact]
    public async Task No_Fix_When_Tracks_Are_A_Variable()
    {
        // CS0618 still fires, but the concrete tracks aren't inline literals, so
        // the fix declines and the warning is left to stand.
        var source = Stubs + @"
namespace TestApp
{
    using Microsoft.UI.Reactor;
    using static Microsoft.UI.Reactor.Factories;

    public static class C
    {
        public static Element Build()
        {
            string[] cols = new[] { ""*"", ""Auto"" };
            string[] rows = new[] { ""*"" };
            return {|CS0618:Grid(cols, rows)|};
        }
    }
}";

        // FixedCode == TestCode: nothing changes.
        await MakeTest(source, source).RunAsync(TestContext.Current.CancellationToken);
    }

    // ── No fix: an element is a non-literal expression ──────────────────

    [Fact]
    public async Task No_Fix_When_An_Element_Is_Not_A_Literal()
    {
        var source = Stubs + @"
namespace TestApp
{
    using Microsoft.UI.Reactor;
    using static Microsoft.UI.Reactor.Factories;

    public static class C
    {
        public static Element Build(string dynamicTrack) =>
            {|CS0618:Grid([""*"", dynamicTrack], [""*""])|};
    }
}";

        await MakeTest(source, source).RunAsync(TestContext.Current.CancellationToken);
    }

    // ── No fix: an unparseable literal track ────────────────────────────

    [Fact]
    public async Task No_Fix_When_A_Literal_Track_Is_Unparseable()
    {
        var source = Stubs + @"
namespace TestApp
{
    using Microsoft.UI.Reactor;
    using static Microsoft.UI.Reactor.Factories;

    public static class C
    {
        public static Element Build() =>
            {|CS0618:Grid([""*"", ""nonsense""], [""*""])|};
    }
}";

        await MakeTest(source, source).RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Near-miss: a different obsolete member must not be touched ───────

    [Fact]
    public async Task No_Fix_For_Unrelated_Obsolete_Call()
    {
        var source = Stubs + @"
namespace TestApp
{
    using Microsoft.UI.Reactor;

    public static class C
    {
        public static Element Build() => {|CS0618:Legacy|}.Build();
    }
}";

        await MakeTest(source, source).RunAsync(TestContext.Current.CancellationToken);
    }
}

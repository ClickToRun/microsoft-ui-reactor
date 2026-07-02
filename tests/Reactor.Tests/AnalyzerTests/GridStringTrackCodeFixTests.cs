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

    // Same-named obsolete Grid(string[],string[],...) in a DIFFERENT type — the
    // symbol gate must reject it (ContainingType != Microsoft.UI.Reactor.Factories).
    public static class OtherFactories
    {
        [System.Obsolete(""A different Grid — not the DSL factory."", error: false)]
        public static Element Grid(string[] columns, string[] rows, params Element[] children) { return null; }
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

    // ── Near-miss: an obsolete Grid(string[],string[],...) in a DIFFERENT type ──

    [Fact]
    public async Task No_Fix_For_Same_Shape_Grid_In_Different_Type()
    {
        // Guards the ContainingType half of the symbol gate: only
        // Microsoft.UI.Reactor.Factories.Grid is fixable.
        var source = Stubs + @"
namespace TestApp
{
    using Microsoft.UI.Reactor;

    public static class C
    {
        public static Element Build() =>
            {|CS0618:OtherFactories.Grid([""*""], [""*""])|};
    }
}";

        await MakeTest(source, source).RunAsync(TestContext.Current.CancellationToken);
    }

    // ── No fix: numeric bounds (weight must be > 0, pixels must be >= 0) ──

    [Fact]
    public async Task No_Fix_When_Star_Weight_Is_Zero()
    {
        var source = Stubs + @"
namespace TestApp
{
    using Microsoft.UI.Reactor;
    using static Microsoft.UI.Reactor.Factories;

    public static class C
    {
        public static Element Build() =>
            {|CS0618:Grid([""0*""], [""*""])|};
    }
}";

        await MakeTest(source, source).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task No_Fix_When_Pixels_Are_Negative()
    {
        var source = Stubs + @"
namespace TestApp
{
    using Microsoft.UI.Reactor;
    using static Microsoft.UI.Reactor.Factories;

    public static class C
    {
        public static Element Build() =>
            {|CS0618:Grid([""-5""], [""*""])|};
    }
}";

        await MakeTest(source, source).RunAsync(TestContext.Current.CancellationToken);
    }

    // ── No fix: non-finite tracks (parse to Infinity — no valid C# literal) ──

    [Theory]
    [InlineData(@"""Infinity""")]   // literal +Infinity
    [InlineData(@"""Infinity*""")]  // +Infinity star weight
    [InlineData(@"""1e400""")]      // overflows to +Infinity
    public async Task No_Fix_When_Track_Is_Non_Finite(string track)
    {
        var source = Stubs + $@"
namespace TestApp
{{
    using Microsoft.UI.Reactor;
    using static Microsoft.UI.Reactor.Factories;

    public static class C
    {{
        public static Element Build() =>
            {{|CS0618:Grid([{track}], [""*""])|}};
    }}
}}";

        await MakeTest(source, source).RunAsync(TestContext.Current.CancellationToken);
    }

    // ── No fix: literals that DIVERGE from the legacy parser, or don't map ──
    // ParseColumnDef/ParseRowDef match "*"/"Auto"/"auto" EXACTLY on the raw string
    // (no trim, no other casing) and fall back to Star(1) otherwise. Converting any of
    // these would silently change layout, so the fix must withhold and leave CS0618.
    [Theory]
    [InlineData("\"AUTO\"")]       // wrong casing → legacy Star(1), NOT Auto
    [InlineData("\"aUtO\"")]       // wrong casing
    [InlineData("\" Auto \"")]     // whitespace around Auto → legacy Star(1)
    [InlineData("\" 2* \"")]       // trailing space defeats legacy raw EndsWith('*') → Star(1)
    [InlineData("\"\"")]           // empty
    [InlineData("\"   \"")]        // whitespace only
    [InlineData("\"-1*\"")]        // negative star weight
    public async Task No_Fix_When_Track_Diverges_From_Legacy_Parser(string track)
    {
        var source = Stubs + $@"
namespace TestApp
{{
    using Microsoft.UI.Reactor;
    using static Microsoft.UI.Reactor.Factories;

    public static class C
    {{
        public static Element Build() =>
            {{|CS0618:Grid([{track}], [""*""])|}};
    }}
}}";

        await MakeTest(source, source).RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Faithful: whitespace-padded numerics still convert (Float allows the
    //    surrounding whitespace, matching the legacy parser's double.TryParse) ──
    [Fact]
    public async Task Fix_Converts_Whitespace_Padded_Pixels()
    {
        var before = Stubs + @"
namespace TestApp
{
    using Microsoft.UI.Reactor;
    using static Microsoft.UI.Reactor.Factories;

    public static class C
    {
        public static Element Build() =>
            {|CS0618:Grid(["" 200 ""], [""*""])|};
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
            Grid([GridSize.Px(200)], [GridSize.Star()]);
    }
}";

        await MakeTest(before, after).RunAsync(TestContext.Current.CancellationToken);
    }

    // ── No fix: interpolated-string and collection-spread elements ──
    [Fact]
    public async Task No_Fix_When_Element_Is_Interpolated_Or_Spread()
    {
        var source = Stubs + @"
namespace TestApp
{
    using Microsoft.UI.Reactor;
    using static Microsoft.UI.Reactor.Factories;

    public static class C
    {
        public static Element Interp(int w) =>
            {|CS0618:Grid([$""{w}""], [""*""])|};

        public static Element Spread(string[] tracks) =>
            {|CS0618:Grid([""*"", ..tracks], [""*""])|};
    }
}";

        await MakeTest(source, source).RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Fix emits compiling GridSize even with only the static factory import ──

    [Fact]
    public async Task Fix_Qualifies_GridSize_When_Namespace_Not_Imported()
    {
        // The call site imports ONLY `using static ...Factories;`, so a bare
        // `GridSize` would not resolve. ToMinimalDisplayString qualifies it.
        var before = Stubs + @"
namespace TestApp
{
    using static Microsoft.UI.Reactor.Factories;

    public static class C
    {
        public static object Build() =>
            {|CS0618:Grid([""*"", ""Auto""], [""200""])|};
    }
}";

        var after = Stubs + @"
namespace TestApp
{
    using static Microsoft.UI.Reactor.Factories;

    public static class C
    {
        public static object Build() =>
            Grid([Microsoft.UI.Reactor.GridSize.Star(), Microsoft.UI.Reactor.GridSize.Auto], [Microsoft.UI.Reactor.GridSize.Px(200)]);
    }
}";

        await MakeTest(before, after).RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Lenient-but-not-C#-literal numerics normalize to a compiling literal ──

    [Fact]
    public async Task Fix_Normalizes_Trailing_Dot_Numeric_Tracks()
    {
        // "5.*" / "5." parse as doubles at runtime but "5." is not a valid C#
        // literal; the fix must emit "5", not "5.".
        var before = Stubs + @"
namespace TestApp
{
    using Microsoft.UI.Reactor;
    using static Microsoft.UI.Reactor.Factories;

    public static class C
    {
        public static Element Build() =>
            {|CS0618:Grid([""5.*""], [""5.""])|};
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
            Grid([GridSize.Star(5)], [GridSize.Px(5)]);
    }
}";

        await MakeTest(before, after).RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Named / reordered args: only the columns & rows params are rewritten ──

    [Fact]
    public async Task Fix_Rewrites_Only_Track_Params_With_Reordered_Named_Args()
    {
        // `children` named first must NOT be treated as a track array; the fix
        // must bind columns/rows by parameter, not by syntactic position.
        var before = Stubs + @"
namespace TestApp
{
    using Microsoft.UI.Reactor;
    using static Microsoft.UI.Reactor.Factories;

    public static class C
    {
        public static Element Build() =>
            {|CS0618:Grid(children: new Element[] { }, columns: [""*""], rows: [""Auto""])|};
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
            Grid(children: new Element[] { }, columns: [GridSize.Star()], rows: [GridSize.Auto]);
    }
}";

        await MakeTest(before, after).RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Fix-all: every legacy Grid in the document is converted ──────────

    [Fact]
    public async Task Fix_Converts_Multiple_Call_Sites()
    {
        var before = Stubs + @"
namespace TestApp
{
    using Microsoft.UI.Reactor;
    using static Microsoft.UI.Reactor.Factories;

    public static class C
    {
        public static Element A() => {|CS0618:Grid([""*""], [""Auto""])|};
        public static Element B() => {|CS0618:Grid([""2*""], [""200""])|};
    }
}";

        var after = Stubs + @"
namespace TestApp
{
    using Microsoft.UI.Reactor;
    using static Microsoft.UI.Reactor.Factories;

    public static class C
    {
        public static Element A() => Grid([GridSize.Star()], [GridSize.Auto]);
        public static Element B() => Grid([GridSize.Star(2)], [GridSize.Px(200)]);
    }
}";

        await MakeTest(before, after).RunAsync(TestContext.Current.CancellationToken);
    }
}

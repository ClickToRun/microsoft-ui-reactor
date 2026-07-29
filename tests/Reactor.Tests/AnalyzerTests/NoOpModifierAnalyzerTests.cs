using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.UI.Reactor.Analyzers;
using Microsoft.UI.Reactor.Core;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.AnalyzerTests;

using AnalyzerVerifier = CSharpAnalyzerVerifier<NoOpModifierAnalyzer, DefaultVerifier>;

/// <summary>
/// Tests for <see cref="NoOpModifierAnalyzer"/> (<c>REACTOR_MOD_003</c>) and
/// <see cref="NoOpModifierCodeFix"/>: a generic common modifier applied to an element whose mounted
/// control is outside the set <c>Reconciler.ApplyModifiers</c> writes it to, so the value is
/// silently dropped.
/// <para>
/// The negatives are the point of the file. This rule fires on code that <b>compiles</b>, so a
/// false positive is a warning on correct code — worse than the bug. Each one pins a specific gate:
/// the control allow-list itself, the generic-vs-type-specific overload split, an unresolvable
/// receiver, a missing generator attribute, and the polymorphic XAML-interop host.
/// </para>
/// </summary>
public class NoOpModifierAnalyzerTests
{
    // A Reactor-shaped surface: the WinUI hierarchy the gate is expressed in, the wrapper/descriptor
    // attributes the analyzer reads the mounted control from, the generic modifiers on
    // `Microsoft.UI.Reactor.ElementExtensions`, and the shape modifiers the fix rewrites to.
    private const string Stubs = @"
namespace System.Runtime.CompilerServices { public static class IsExternalInit { } }

namespace Microsoft.UI.Xaml
{
    public class UIElement { }
    public class FrameworkElement : UIElement { }
    public struct Thickness { public Thickness(double u) { } }
    public struct CornerRadius { public CornerRadius(double u) { } }
}

namespace Microsoft.UI.Xaml.Media
{
    public class Brush { }
    public class SolidColorBrush : Brush { }
}

namespace Microsoft.UI.Xaml.Controls
{
    using Microsoft.UI.Xaml;
    using Microsoft.UI.Xaml.Media;

    public class Control : FrameworkElement { }
    public class Border : FrameworkElement { }
    public class Panel : FrameworkElement { }
    public class Grid : Panel { }
    public class StackPanel : Panel { }
    public class Button : Control { }
    public class TextBlock : FrameworkElement { }
    public class RichTextBlock : FrameworkElement { }
    public class Image : FrameworkElement { }
}

namespace Microsoft.UI.Xaml.Shapes
{
    using Microsoft.UI.Xaml;

    public class Shape : FrameworkElement { }
    public class Rectangle : Shape { }
    public class Ellipse : Shape { }
    public class Line : Shape { }
    public class Path : Shape { }
}

namespace Microsoft.UI.Reactor.Wrappers
{
    using System;

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class GenerateReactorWrapperAttribute : Attribute
    {
        public GenerateReactorWrapperAttribute(Type controlType) { ControlType = controlType; }
        public Type ControlType { get; }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class GenerateReactorDescriptorAttribute : Attribute
    {
        public GenerateReactorDescriptorAttribute(Type controlType) { ControlType = controlType; }
        public Type ControlType { get; }
    }
}

namespace Microsoft.UI.Reactor.Core
{
    using Microsoft.UI.Reactor.Wrappers;
    using WinShapes = Microsoft.UI.Xaml.Shapes;
    using WinUI = Microsoft.UI.Xaml.Controls;

    public abstract record Element { }

    public sealed record ThemeRef(string ResourceKey);

    [GenerateReactorWrapper(typeof(WinShapes.Rectangle))]
    public record RectangleElement : Element { }

    [GenerateReactorWrapper(typeof(WinShapes.Ellipse))]
    public record EllipseElement : Element { }

    [GenerateReactorDescriptor(typeof(WinShapes.Line))]
    public record LineElement : Element { }

    [GenerateReactorDescriptor(typeof(WinShapes.Path))]
    public record PathElement : Element { }

    // A user-defined element derived from a wrapped one: the inherited registration mounts the
    // base's control, so the base's attribute is the authority.
    public record RoundedRectangleElement : RectangleElement { }

    [GenerateReactorDescriptor(typeof(WinUI.Border))]
    public record BorderElement : Element { }

    [GenerateReactorDescriptor(typeof(WinUI.StackPanel))]
    public record StackPanelElement : Element { }

    [GenerateReactorDescriptor(typeof(WinUI.Grid))]
    public record GridElement : Element { }

    [GenerateReactorDescriptor(typeof(WinUI.Button))]
    public record ButtonElement : Element { }

    [GenerateReactorDescriptor(typeof(WinUI.TextBlock))]
    public record TextBlockElement : Element { }

    [GenerateReactorDescriptor(typeof(WinUI.RichTextBlock))]
    public record RichTextBlockElement : Element { }

    [GenerateReactorDescriptor(typeof(WinUI.Image))]
    public record ImageElement : Element { }

    // XamlInterop's host: declared as the base, mounted as whatever the caller supplied.
    [GenerateReactorDescriptor(typeof(Microsoft.UI.Xaml.FrameworkElement))]
    public record XamlHostElement : Element { }

    // Hand-written handler with no Set overload: the mounted control is unknown.
    public record CardElement : Element { }

    public static class Factories
    {
        public static RectangleElement Rectangle() => new();
        public static EllipseElement Ellipse() => new();
        public static LineElement Line() => new();
        public static PathElement Path() => new();
        public static RoundedRectangleElement RoundedRectangle() => new();
        public static BorderElement Border() => new();
        public static StackPanelElement VStack() => new();
        public static GridElement Grid() => new();
        public static ButtonElement Button() => new();
        public static TextBlockElement Text(string s) => new();
        public static RichTextBlockElement RichTextBlock() => new();
        public static ImageElement Image(string s) => new();
        public static XamlHostElement XamlHost() => new();
        public static CardElement Card() => new();
    }
}

namespace Microsoft.UI.Reactor
{
    using System;
    using Microsoft.UI.Reactor.Core;
    using Microsoft.UI.Xaml;
    using Microsoft.UI.Xaml.Media;
    using WinShapes = Microsoft.UI.Xaml.Shapes;
    using WinUI = Microsoft.UI.Xaml.Controls;

    public static class BrushHelper
    {
        public static SolidColorBrush Parse(string color) => new();
    }

    public static class ElementExtensions
    {
        // Reactor's `Set` escape hatch. Its Action<TControl> argument is where the analyzer reads
        // the mounted control from — the generator attributes are not visible to consumers.
        public static RectangleElement Set(this RectangleElement el, Action<WinShapes.Rectangle> configure) => el;
        public static EllipseElement Set(this EllipseElement el, Action<WinShapes.Ellipse> configure) => el;
        public static LineElement Set(this LineElement el, Action<WinShapes.Line> configure) => el;
        public static PathElement Set(this PathElement el, Action<WinShapes.Path> configure) => el;
        public static BorderElement Set(this BorderElement el, Action<WinUI.Border> configure) => el;
        public static StackPanelElement Set(this StackPanelElement el, Action<WinUI.StackPanel> configure) => el;
        public static GridElement Set(this GridElement el, Action<WinUI.Grid> configure) => el;
        public static ButtonElement Set(this ButtonElement el, Action<WinUI.Button> configure) => el;
        public static TextBlockElement Set(this TextBlockElement el, Action<WinUI.TextBlock> configure) => el;
        public static RichTextBlockElement Set(this RichTextBlockElement el, Action<WinUI.RichTextBlock> configure) => el;
        public static ImageElement Set(this ImageElement el, Action<WinUI.Image> configure) => el;
        public static XamlHostElement Set(this XamlHostElement el, Action<Microsoft.UI.Xaml.FrameworkElement> configure) => el;

        // Generic common modifiers — the ones ApplyModifiers gates on a control type.
        public static T Background<T>(this T el, string color) where T : Element => el;
        public static T Background<T>(this T el, Brush brush) where T : Element => el;
        public static T Background<T>(this T el, ThemeRef theme) where T : Element => el;
        public static T Foreground<T>(this T el, Brush brush) where T : Element => el;
        public static T BorderBrush<T>(this T el, Brush brush) where T : Element => el;
        public static T BorderThickness<T>(this T el, double thickness) where T : Element => el;
        public static T CornerRadius<T>(this T el, double radius) where T : Element => el;
        public static T Padding<T>(this T el, double uniform) where T : Element => el;
        public static T FontSize<T>(this T el, double size) where T : Element => el;

        // Generic, but ungated in ModifierTable (see GateOnlyInReconciler).
        public static T IsEnabled<T>(this T el, bool enabled = true) where T : Element => el;

        // Generic and not in ModifierTable at all.
        public static T Size<T>(this T el, double w, double h) where T : Element => el;

        // Type-specific overload: writes the record property directly, so it never goes through
        // ApplyModifiers' control gate.
        public static RichTextBlockElement FontSize(this RichTextBlockElement el, double size) => el;

        // Shape modifiers the did-you-mean fix rewrites to.
        public static RectangleElement Fill(this RectangleElement el, Brush brush) => el;
        public static EllipseElement Fill(this EllipseElement el, Brush brush) => el;
        public static PathElement Fill(this PathElement el, Brush brush) => el;
        public static LineElement Stroke(this LineElement el, Brush brush) => el;
        public static PathElement Stroke(this PathElement el, Brush brush) => el;
        public static LineElement StrokeThickness(this LineElement el, double thickness) => el;
        public static PathElement StrokeThickness(this PathElement el, double thickness) => el;
    }
}

namespace Other
{
    using Microsoft.UI.Reactor.Core;

    // A non-Reactor fluent `Background` on a Reactor element: same name, different declaring type.
    public static class ThirdPartyExtensions
    {
        public static T Background<T>(this T el, int argb) where T : Element => el;
    }
}
";

    private static CSharpAnalyzerTest<NoOpModifierAnalyzer, DefaultVerifier> MakeAnalyzerTest(string body)
    {
        var test = new CSharpAnalyzerTest<NoOpModifierAnalyzer, DefaultVerifier>
        {
            TestCode = Stubs + body,
            CompilerDiagnostics = CompilerDiagnostics.None,
        };
        test.DisabledDiagnostics.Add("CS1591");
        return test;
    }

    private static CSharpCodeFixTest<NoOpModifierAnalyzer, NoOpModifierCodeFix, DefaultVerifier> MakeFixTest(
        string body, string fixedBody)
    {
        var test = new CSharpCodeFixTest<NoOpModifierAnalyzer, NoOpModifierCodeFix, DefaultVerifier>
        {
            TestCode = Stubs + body,
            FixedCode = Stubs + fixedBody,
            CompilerDiagnostics = CompilerDiagnostics.None,
        };
        test.DisabledDiagnostics.Add("CS1591");
        return test;
    }

    private static string App(string members) => @"
namespace TestApp
{
    using Microsoft.UI.Reactor;
    using Microsoft.UI.Reactor.Core;
    using Microsoft.UI.Xaml.Media;
    using static Microsoft.UI.Reactor.Core.Factories;

    static class C
    {
" + members + @"
    }
}";

    // ── Positives ───────────────────────────────────────────────────

    [Fact]
    public async Task Fires_For_Background_On_A_Rectangle()
    {
        var body = App(@"
        internal static Element M() => Rectangle().{|REACTOR_MOD_003:Background|}(""#FF6B6B"");");

        await MakeAnalyzerTest(body).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_For_The_Gallery_Canvas_Chain()
    {
        // The exact shape from samples/ReactorGallery/ControlPages/Layout/CanvasPage.cs:25 — the
        // modifier is mid-chain, and `Size<T>` preserves the concrete RectangleElement receiver.
        var body = App(@"
        internal static Element M() => Rectangle().Size(80, 80).{|REACTOR_MOD_003:Background|}(""#FF6B6B"").Size(80, 80);");

        await MakeAnalyzerTest(body).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Reports_Modifier_Element_Gate_And_Shape_Suggestion_As_Message_Arguments()
    {
        var body = App(@"
        internal static Element M() => Rectangle().{|#0:Background|}(""#FF6B6B"");");

        var test = MakeAnalyzerTest(body);
        test.ExpectedDiagnostics.Add(
            AnalyzerVerifier.Diagnostic(NoOpModifierAnalyzer.DiagnosticId)
                .WithLocation(0)
                .WithArguments(
                    "Background",
                    "RectangleElement",
                    "Panel, Control, or Border",
                    ". Rectangle is a Shape, which is painted with 'Fill' — did you mean '.Fill(...)'?"));

        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_For_Background_On_An_Ellipse_Brush_Overload()
    {
        var body = App(@"
        internal static Element M(Brush b) => Ellipse().{|REACTOR_MOD_003:Background|}(b);");

        await MakeAnalyzerTest(body).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_For_Background_On_A_Line_And_Suggests_Stroke()
    {
        // LineElement has no Fill modifier, so the candidate list falls through to Stroke rather
        // than emitting a call that does not exist.
        var body = App(@"
        internal static Element M(Brush b) => Line().{|#0:Background|}(b);");

        var test = MakeAnalyzerTest(body);
        test.ExpectedDiagnostics.Add(
            AnalyzerVerifier.Diagnostic(NoOpModifierAnalyzer.DiagnosticId)
                .WithLocation(0)
                .WithArguments(
                    "Background",
                    "LineElement",
                    "Panel, Control, or Border",
                    ". Line is a Shape, which is painted with 'Stroke' — did you mean '.Stroke(...)'?"));

        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_For_BorderThickness_On_A_Path_And_Suggests_StrokeThickness()
    {
        var body = App(@"
        internal static Element M() => Path().{|#0:BorderThickness|}(2);");

        var test = MakeAnalyzerTest(body);
        test.ExpectedDiagnostics.Add(
            AnalyzerVerifier.Diagnostic(NoOpModifierAnalyzer.DiagnosticId)
                .WithLocation(0)
                .WithArguments(
                    "BorderThickness",
                    "PathElement",
                    "Control or Border",
                    ". Path is a Shape, which is painted with 'StrokeThickness' — did you mean '.StrokeThickness(...)'?"));

        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_For_A_Derived_Element_Through_The_Base_Set_Overload()
    {
        // RoundedRectangleElement declares no Set of its own; the base's Set(RectangleElement,
        // Action<Rectangle>) is applicable to it, which is also how the inherited registration
        // mounts it.
        var body = App(@"
        internal static Element M() => RoundedRectangle().{|REACTOR_MOD_003:Background|}(""#FF6B6B"");");

        await MakeAnalyzerTest(body).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_For_Background_On_A_TextBlock_With_The_Border_Hint()
    {
        // Not a shape: no rename can help, so the message points at the structural fix instead.
        var body = App(@"
        internal static Element M() => Text(""hi"").{|#0:Background|}(""#FF6B6B"");");

        var test = MakeAnalyzerTest(body);
        test.ExpectedDiagnostics.Add(
            AnalyzerVerifier.Diagnostic(NoOpModifierAnalyzer.DiagnosticId)
                .WithLocation(0)
                .WithArguments(
                    "Background",
                    "TextBlockElement",
                    "Panel, Control, or Border",
                    ". Wrap it in a Border(...) to paint a background behind this element"));

        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_For_CornerRadius_On_An_Image()
    {
        var body = App(@"
        internal static Element M() => Image(""a.png"").{|REACTOR_MOD_003:CornerRadius|}(4);");

        await MakeAnalyzerTest(body).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_For_Padding_On_A_Grid()
    {
        // Padding's gate is Control/Border/StackPanel. A Grid is a Panel but not a StackPanel, so
        // the write is dropped — the asymmetry REACTOR_MOD_002's table exists to record.
        var body = App(@"
        internal static Element M() => Grid().{|REACTOR_MOD_003:Padding|}(16);");

        await MakeAnalyzerTest(body).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_For_Foreground_On_A_Border()
    {
        var body = App(@"
        internal static Element M(Brush b) => Border().{|REACTOR_MOD_003:Foreground|}(b);");

        await MakeAnalyzerTest(body).RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Negatives (false-positive gating) ───────────────────────────

    [Fact]
    public async Task Does_Not_Fire_For_Background_On_A_Border()
    {
        var body = App(@"
        internal static Element M() => Border().Background(""#FF6B6B"");");

        await MakeAnalyzerTest(body).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Does_Not_Fire_For_Background_On_A_Panel()
    {
        var body = App(@"
        internal static Element V() => VStack().Background(""#FF6B6B"");
        internal static Element G() => Grid().Background(""#FF6B6B"");");

        await MakeAnalyzerTest(body).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Does_Not_Fire_For_Any_Gated_Modifier_On_A_Control()
    {
        var body = App(@"
        internal static Element M(Brush b) =>
            Button().Background(b).Foreground(b).BorderBrush(b).BorderThickness(1).CornerRadius(4).Padding(8).FontSize(14);");

        await MakeAnalyzerTest(body).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Does_Not_Fire_For_Border_Box_Modifiers_On_A_Border()
    {
        var body = App(@"
        internal static Element M(Brush b) => Border().BorderBrush(b).BorderThickness(1).CornerRadius(4).Padding(8);");

        await MakeAnalyzerTest(body).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Does_Not_Fire_For_Padding_On_A_StackPanel()
    {
        var body = App(@"
        internal static Element M() => VStack().Padding(16);");

        await MakeAnalyzerTest(body).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Does_Not_Fire_For_Foreground_Or_FontSize_On_A_TextBlock()
    {
        var body = App(@"
        internal static Element M(Brush b) => Text(""hi"").Foreground(b).FontSize(14);");

        await MakeAnalyzerTest(body).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Does_Not_Fire_For_FontSize_On_A_RichTextBlock_Type_Specific_Overload()
    {
        // RichTextBlock is neither Control nor TextBlock, so the GENERIC FontSize<T> would be
        // dropped — but `.FontSize(14)` binds the type-specific RichTextBlockElement overload,
        // which writes the record directly. This also guards that overload's continued existence:
        // delete it and the call rebinds to the generic modifier and this test fails.
        var body = App(@"
        internal static Element M() => RichTextBlock().FontSize(14);");

        await MakeAnalyzerTest(body).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Does_Not_Fire_For_An_Ungated_Modifier()
    {
        // IsEnabled is Control-gated in ApplyModifiers but carries a null ControlGate (see
        // ModifierTable.GateOnlyInReconciler); a null gate is never read as "reaches everything",
        // it means "not classified for this direction" and is skipped.
        var body = App(@"
        internal static Element M() => Rectangle().IsEnabled(false);");

        await MakeAnalyzerTest(body).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Does_Not_Fire_For_A_Modifier_Outside_The_Table()
    {
        var body = App(@"
        internal static Element M() => Rectangle().Size(80, 80);");

        await MakeAnalyzerTest(body).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Does_Not_Fire_For_A_Generic_Receiver()
    {
        var body = App(@"
        internal static T Style<T>(T el) where T : Element => el.Background(""#FF6B6B"");");

        await MakeAnalyzerTest(body).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Does_Not_Fire_For_A_Receiver_Typed_As_Element()
    {
        var body = App(@"
        internal static Element M(Element el) => el.Background(""#FF6B6B"");");

        await MakeAnalyzerTest(body).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Does_Not_Fire_For_An_Element_With_No_Set_Overload()
    {
        // CardElement is a hand-written composite: nothing declares its mounted control, so the
        // analysis has no ground truth and must stay silent.
        var body = App(@"
        internal static Element M() => Card().Background(""#FF6B6B"");");

        await MakeAnalyzerTest(body).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Does_Not_Fire_For_The_Polymorphic_XamlInterop_Host()
    {
        // XamlHostElement declares FrameworkElement, but hosts whatever the caller supplied — which
        // may well be a Panel or Control at runtime.
        var body = App(@"
        internal static Element M() => XamlHost().Background(""#FF6B6B"");");

        await MakeAnalyzerTest(body).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Does_Not_Fire_For_A_Non_Reactor_Background_Extension()
    {
        var body = @"
namespace TestApp
{
    using Other;
    using Microsoft.UI.Reactor.Core;
    using static Microsoft.UI.Reactor.Core.Factories;

    static class C2
    {
        internal static Element M() => Rectangle().Background(0x00FF6B6B);
    }
}";

        await MakeAnalyzerTest(body).RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Code fix ────────────────────────────────────────────────────

    [Fact]
    public async Task CodeFix_Rewrites_The_Brush_Overload_As_A_Rename()
    {
        var body = App(@"
        internal static Element M(Brush b) => Rectangle().{|REACTOR_MOD_003:Background|}(b);");
        var fixedBody = App(@"
        internal static Element M(Brush b) => Rectangle().Fill(b);");

        await MakeFixTest(body, fixedBody).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CodeFix_Wraps_The_Color_String_In_BrushHelper_Parse()
    {
        // The shape modifiers take a Brush while the common modifier has a string overload, so a
        // bare rename would not compile. BrushHelper.Parse is exactly what Background(string) does
        // internally, which keeps the rewrite behaviour-preserving.
        var body = App(@"
        internal static Element M() => Rectangle().{|REACTOR_MOD_003:Background|}(""#FF6B6B"");");
        var fixedBody = App(@"
        internal static Element M() => Rectangle().Fill(BrushHelper.Parse(""#FF6B6B""));");

        await MakeFixTest(body, fixedBody).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CodeFix_Rewrites_A_Line_To_Stroke()
    {
        var body = App(@"
        internal static Element M() => Line().{|REACTOR_MOD_003:Background|}(""#FF6B6B"");");
        var fixedBody = App(@"
        internal static Element M() => Line().Stroke(BrushHelper.Parse(""#FF6B6B""));");

        await MakeFixTest(body, fixedBody).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CodeFix_Is_Not_Offered_For_The_ThemeRef_Overload()
    {
        // No Fill(ThemeRef) counterpart exists. The diagnostic still reports; FixedCode equal to
        // TestCode asserts no fix was registered (a registered fix would change the source).
        var body = App(@"
        internal static Element M(ThemeRef t) => Rectangle().{|REACTOR_MOD_003:Background|}(t);");

        await MakeFixTest(body, body).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CodeFix_Is_Not_Offered_For_A_Non_Shape_Receiver()
    {
        var body = App(@"
        internal static Element M() => Text(""hi"").{|REACTOR_MOD_003:Background|}(""#FF6B6B"");");

        await MakeFixTest(body, body).RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Drift guard: the suggested shape modifiers must really exist ──

    /// <summary>
    /// Every shape element in the live Reactor assembly must expose at least one of the
    /// <see cref="NoOpModifierAnalyzer.ShapeReplacements"/> candidates as a real
    /// <c>ElementExtensions</c> method, otherwise the analyzer would offer — and the code fix would
    /// emit — a call that does not compile.
    /// </summary>
    /// <remarks>
    /// Reflection only reads metadata; no WinUI object is constructed, so this is safe in the
    /// headless test host. Deleting <c>Fill(this RectangleElement, Brush)</c> fails this test.
    /// </remarks>
    [Fact]
    [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming", "IL2026",
        Justification = "Test-only contract guard: enumerates the Reactor assembly's element types and the ElementExtensions surface by design. This host is never trimmed; behaviour-neutral.")]
    [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming", "IL2075",
        Justification = "Test-only contract guard: reflects the public static methods of ElementExtensions, resolved by name from the Reactor assembly. Intentional and JIT-only; behaviour-neutral.")]
    public void Every_Shape_Element_Has_A_Resolvable_Shape_Replacement()
    {
        var elementExtensions = typeof(Element).Assembly.GetType("Microsoft.UI.Reactor.ElementExtensions");
        Assert.NotNull(elementExtensions);

        var modifiers = elementExtensions!
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .ToLookup(m => m.Name, StringComparer.Ordinal);

        var candidates = NoOpModifierAnalyzer.ShapeReplacements.Values
            .SelectMany(names => names)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var shapeElements = 0;
        var missing = new global::System.Collections.Generic.List<string>();

        foreach (var element in typeof(Element).Assembly.GetTypes()
                     .Where(t => typeof(Element).IsAssignableFrom(t) && !t.IsAbstract && !t.IsGenericTypeDefinition)
                     .OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            if (MountedControl(element) is not { } control
                || !typeof(global::Microsoft.UI.Xaml.Shapes.Shape).IsAssignableFrom(control))
                continue;

            shapeElements++;

            var hasReplacement = candidates.Any(name => modifiers[name].Any(m =>
            {
                var parameters = m.GetParameters();
                return parameters.Length >= 1 && parameters[0].ParameterType.IsAssignableFrom(element);
            }));

            if (!hasReplacement)
            {
                missing.Add(
                    $"{element.Name} mounts {control.Name} (a Shape) but ElementExtensions declares none of " +
                    $"[{string.Join("|", candidates)}] for it — REACTOR_MOD_003 would suggest a modifier that " +
                    "does not exist, or silently stop suggesting one.");
            }
        }

        Assert.True(missing.Count == 0, string.Join("\n  ", missing));

        // Self-validation: Rectangle/Ellipse/Line/Path. If the attribute walk ever stops resolving,
        // the loop would no-op and the guard would pass vacuously.
        Assert.True(
            shapeElements >= 4,
            $"Expected at least 4 shape elements but found {shapeElements} — the shape-replacement guard " +
            "may be running vacuously.");
    }

    [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming", "IL2075",
        Justification = "Test-only contract guard: reads the generator attribute off a type enumerated by the surrounding Assembly.GetTypes scan. Behaviour-neutral.")]
    private static Type? MountedControl(Type element)
    {
        for (var current = element; current is not null; current = current.BaseType)
        {
            foreach (var attribute in current.GetCustomAttributesData())
            {
                var name = attribute.AttributeType.Name;
                if (name is not ("GenerateReactorWrapperAttribute" or "GenerateReactorDescriptorAttribute")
                    || attribute.ConstructorArguments.Count < 1)
                    continue;

                if (attribute.ConstructorArguments[0].Value is Type control)
                    return control;
            }
        }

        return null;
    }
}

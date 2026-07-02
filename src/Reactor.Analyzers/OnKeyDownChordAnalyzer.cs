using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.UI.Reactor.Analyzers;

/// <summary>
/// <c>REACTOR_INPUT_001</c> — flags a <c>.OnKeyDown((s, e) =&gt; …)</c> lambda that tests a
/// <c>VirtualKeyModifiers.Control</c> / <c>.Menu</c> (Ctrl/Alt) chord.
/// </summary>
/// <remarks>
/// <para>
/// <c>.OnKeyDown</c> is a <b>focus-scoped</b> routed-input modifier: the handler only fires while
/// that specific element has keyboard focus. Hand-rolling an app-wide accelerator such as
/// <c>Ctrl+S</c> inside a <c>TextBox(…).OnKeyDown(…)</c> lambda therefore fires nowhere else and
/// never reaches WinUI's <c>AccessKeyManager</c> — the shortcut silently does nothing whenever the
/// field is not focused.
/// </para>
/// <para>
/// The idiomatic fix is a <c>Command</c> whose <c>Accelerator = Accelerator(VirtualKey.S,
/// VirtualKeyModifiers.Control)</c> (see <c>Command.cs</c> / <c>Dsl.cs</c>), which registers the
/// chord with the window's accelerator infrastructure and routes regardless of focus. The rule ships
/// a template code fix (<see cref="OnKeyDownChordCodeFix"/>) because the rewrite is intent-heavy —
/// the app author decides where the command lives and what it does.
/// </para>
/// <para>
/// Detection (spec 060 §12): a <c>.OnKeyDown</c> invocation whose single argument is a lambda whose
/// body references <c>VirtualKeyModifiers.Control</c> or <c>VirtualKeyModifiers.Menu</c>. A cheap
/// syntactic gate (method name + lambda + the <c>Control</c>/<c>Menu</c> member name qualified by an
/// identifier spelled <c>VirtualKeyModifiers</c>) runs before a single semantic check confirms the
/// member really binds to <c>Windows.System.VirtualKeyModifiers</c>, so a same-named local enum does
/// not trip it. A <c>Shift</c>-only chord or a modifier-free handler is deliberately left alone.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class OnKeyDownChordAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "REACTOR_INPUT_001";

    private const string ModifiersEnumName = "VirtualKeyModifiers";
    private const string ModifiersEnumNamespace = "Windows.System";
    private const string ReactorNamespacePrefix = "Microsoft.UI.Reactor";

    private static readonly LocalizableString Title =
        "Ctrl/Alt chord on .OnKeyDown should be a Command accelerator";

    private static readonly LocalizableString MessageFormat =
        "This .OnKeyDown lambda tests a '{0}' chord, but .OnKeyDown is focus-scoped and only fires while the element is focused. Register the shortcut as a Command whose Accelerator = Accelerator(VirtualKey.<key>, {0}) so it routes app-wide through AccessKeyManager.";

    private static readonly LocalizableString Description =
        "The .OnKeyDown modifier subscribes to the element's focus-scoped KeyDown routed event, so a " +
        "hand-rolled Ctrl+S / Alt+key shortcut only fires while that element has focus and never reaches " +
        "WinUI's AccessKeyManager. App-wide accelerators belong on a Command: " +
        "new Command { …, Accelerator = Accelerator(VirtualKey.S, VirtualKeyModifiers.Control) } " +
        "registers the chord with the window's accelerator infrastructure and routes regardless of focus.";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        MessageFormat,
        "Reactor.Input",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: Description);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        // Syntactic gate 1: a fluent `.OnKeyDown(...)` call.
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return;
        if (memberAccess.Name.Identifier.Text != "OnKeyDown")
            return;

        // Syntactic gate 2: exactly one argument, and it is a lambda (simple `s => …` or
        // parenthesized `(s, e) => …`, both `LambdaExpressionSyntax`). A method-group handler
        // (`.OnKeyDown(HandleKeyDown)`) is out of scope — the analyzer can't see the chord test,
        // and mirrors the code fix, which needs the lambda body.
        var args = invocation.ArgumentList.Arguments;
        if (args.Count != 1)
            return;
        if (args[0].Expression is not LambdaExpressionSyntax lambda)
            return;

        var body = lambda.Body;
        if (body is null)
            return;

        // Syntactic gate 3: the lambda body references `VirtualKeyModifiers.Control` / `.Menu`.
        // Collect matches syntactically first (cheap), then confirm the enum semantically.
        var chord = FindChordModifier(body, context.SemanticModel, context.CancellationToken);
        if (chord is null)
            return;

        // Ground to Reactor's `.OnKeyDown` modifier: if the call resolves to a method that is NOT in
        // a Reactor namespace, it is an unrelated same-named API (e.g. a third-party fluent helper) —
        // don't warn. When the symbol can't be resolved (incomplete code mid-edit), fall back to the
        // syntactic match and still warn, so the footgun stays visible while the author is typing.
        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is IMethodSymbol method
            && !IsReactorNamespace(method.ContainingNamespace?.ToDisplayString()))
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            Rule,
            invocation.GetLocation(),
            $"{ModifiersEnumName}.{chord}"));
    }

    /// <summary>
    /// Scans <paramref name="body"/> for the first <c>VirtualKeyModifiers.Control</c> /
    /// <c>VirtualKeyModifiers.Menu</c> member access that semantically binds to
    /// <c>Windows.System.VirtualKeyModifiers</c>. Returns the member name (<c>Control</c> /
    /// <c>Menu</c>) or <c>null</c> when none is found. A <c>Shift</c>/<c>Windows</c>/<c>None</c>
    /// modifier is intentionally not a match — only the Ctrl/Alt app-accelerator footgun fires.
    /// </summary>
    private static string? FindChordModifier(SyntaxNode body, SemanticModel model, System.Threading.CancellationToken ct)
    {
        foreach (var access in body.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>())
        {
            var member = access.Name.Identifier.Text;
            if (member != "Control" && member != "Menu")
                continue;

            // Only the OnKeyDown handler's own logic counts. A modifier test inside a *nested*
            // closure (a lambda/anonymous method/local function declared within this handler) belongs
            // to some other callback, not this key handler, so skip it to avoid a false positive.
            if (IsInsideNestedFunction(access, body))
                continue;

            // Cheap syntactic pre-check: the receiver must be spelled `VirtualKeyModifiers`
            // (bare `VirtualKeyModifiers.Control` or qualified `Windows.System.VirtualKeyModifiers.Control`)
            // before we spend a semantic query.
            if (ReceiverName(access.Expression) != ModifiersEnumName)
                continue;

            // Semantic confirmation: the accessed member is a field on the real
            // Windows.System.VirtualKeyModifiers enum (not a same-named user type).
            if (model.GetSymbolInfo(access, ct).Symbol is IFieldSymbol { ContainingType: { } enumType }
                && enumType.Name == ModifiersEnumName
                && enumType.ContainingNamespace?.ToDisplayString() == ModifiersEnumNamespace)
            {
                return member;
            }
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="node"/> sits inside a lambda / anonymous method / local function
    /// that is itself nested within <paramref name="body"/> (the OnKeyDown handler's body). Such a
    /// node belongs to an inner callback, not the key handler, so it must not count as the chord.
    /// </summary>
    private static bool IsInsideNestedFunction(SyntaxNode node, SyntaxNode body)
    {
        for (var current = node.Parent; current is not null && current != body; current = current.Parent)
        {
            if (current is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
                return true;
        }
        return false;
    }

    /// <summary>True when <paramref name="ns"/> is the Reactor root namespace or a descendant.</summary>
    private static bool IsReactorNamespace(string? ns) =>
        ns is not null
            && (ns == ReactorNamespacePrefix
                || ns.StartsWith(ReactorNamespacePrefix + ".", System.StringComparison.Ordinal));

    /// <summary>
    /// The trailing identifier of the receiver of a member access: the identifier itself for
    /// <c>VirtualKeyModifiers.Control</c>, or the <c>.Name</c> for a qualified
    /// <c>Windows.System.VirtualKeyModifiers.Control</c>.
    /// </summary>
    private static string? ReceiverName(ExpressionSyntax receiver) => receiver switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
        _ => null,
    };
}

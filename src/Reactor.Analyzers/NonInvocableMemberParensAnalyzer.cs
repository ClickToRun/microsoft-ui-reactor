using System.Linq;
using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.UI.Reactor.Analyzers;

/// <summary>
/// REACTOR_DYM_001 — a Reactor <em>property</em> or <em>field</em> invoked like a method
/// (e.g. <c>GridSize.Auto()</c> when <c>Auto</c> is a property). The C# compiler already rejects
/// this with <c>CS1955</c> ("Non-invocable member … cannot be used like a method"); this analyzer
/// adds the actionable <em>did-you-mean</em>: remove the parentheses. Paired with
/// <see cref="NonInvocableMemberParensCodeFix"/> for a one-click fix.
/// </summary>
/// <remarks>
/// <para>
/// This is the first in-build "did you mean" analyzer (design of record: spec 061). It is the
/// highest-frequency Reactor mistake in the eval corpus (<c>GridSize.Auto()</c>), and — unlike the
/// fuzzy name-resolution cases — it is <b>purely structural</b>, so it needs no similarity match.
/// </para>
/// <para>
/// <b>Precision.</b> It reports only when (a) the invocation did not bind
/// (<see cref="SymbolInfo.Symbol"/> is <see langword="null"/>), so it can never fire on a valid
/// call such as a delegate-typed property being invoked; (b) the receiver type resolves and lives
/// under <c>Microsoft.UI.Reactor</c>, so it never touches unrelated consumer code; and (c) the
/// named member is a property/field with no method overload. Because it only fires when the
/// invocation failed to bind, it always co-occurs with a compiler error — which is why a
/// <see cref="DiagnosticSeverity.Warning"/> here is safe under <c>TreatWarningsAsErrors</c> (the
/// build is already failing) while still surfacing in a plain <c>dotnet build</c>.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NonInvocableMemberParensAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "REACTOR_DYM_001";

    private static readonly LocalizableString Title =
        "Reactor member is not callable";
    private static readonly LocalizableString MessageFormat =
        "'{0}' is a {1}, not a method — remove the parentheses";
    private static readonly LocalizableString Description =
        "A Reactor property or field was invoked like a method (e.g. GridSize.Auto()). Reference the member without parentheses.";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        MessageFormat,
        "Reactor.DidYouMean",
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

        // Only the zero-argument case has an unambiguous "drop the parentheses" fix.
        if (invocation.ArgumentList.Arguments.Count != 0)
            return;
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return;

        // A generic member name (`GridSize.Auto<int>()`) can't be repaired by only dropping the
        // parentheses — `GridSize.Auto<int>` is still not a valid property/field reference — so leave
        // that shape to the raw compiler error rather than offering a fix that doesn't compile.
        if (memberAccess.Name is GenericNameSyntax)
            return;

        var model = context.SemanticModel;

        // Precise CS1955 alignment: only when the invocation itself did not bind. A property whose
        // type is a delegate (`prop()` invokes the delegate) binds fine and must never be flagged.
        if (model.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not null)
            return;

        // The receiver type survives the error state even though the invocation does not (see the
        // spec 061 spike). Static access (`GridSize.Auto`) binds to the type symbol; instance
        // access uses the expression's type.
        var receiverType = ResolveReceiverType(model, memberAccess.Expression, context.CancellationToken);
        if (receiverType is null || receiverType.TypeKind == TypeKind.Error)
            return;

        // Only touch Reactor types, so we never fire on unrelated consumer code.
        if (!IsReactorType(receiverType))
            return;

        var memberName = memberAccess.Name.Identifier.Text;
        var members = receiverType.GetMembers(memberName);
        if (members.IsEmpty)
            return; // truly unknown member — a different (name-resolution) shape, not this one.
        if (members.Any(m => m.Kind == SymbolKind.Method))
            return; // a real method overload exists — the call may be legitimate.

        var member = members.FirstOrDefault(m => m.Kind is SymbolKind.Property or SymbolKind.Field);
        if (member is null)
            return;

        var memberKind = member.Kind == SymbolKind.Property ? "property" : "field";
        context.ReportDiagnostic(Diagnostic.Create(
            Rule,
            invocation.GetLocation(),
            $"{receiverType.Name}.{memberName}",
            memberKind));
    }

    private static ITypeSymbol? ResolveReceiverType(SemanticModel model, ExpressionSyntax receiver, CancellationToken ct)
    {
        if (model.GetSymbolInfo(receiver, ct).Symbol is INamedTypeSymbol namedType)
            return namedType; // static member access: `GridSize.Auto()`
        return model.GetTypeInfo(receiver, ct).Type; // instance member access
    }

    private static bool IsReactorType(ITypeSymbol type)
    {
        var ns = type.ContainingNamespace?.ToDisplayString();
        return ns is not null
            && (ns == "Microsoft.UI.Reactor"
                || ns.StartsWith("Microsoft.UI.Reactor.", System.StringComparison.Ordinal));
    }
}

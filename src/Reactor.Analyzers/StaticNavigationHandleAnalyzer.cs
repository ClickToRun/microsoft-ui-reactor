using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.UI.Reactor.Analyzers;

/// <summary>
/// <c>REACTOR_NAV_001</c> — a <see cref="Navigation.NavigationHandle{TRoute}"/>
/// returned by <c>UseNavigation</c> must not be stashed in a <c>static</c> field.
/// </summary>
/// <remarks>
/// <para>
/// The pitfall (navigation.md §"Treating <c>UseNavigation</c> like a singleton"):
/// </para>
/// <code>
/// public static NavigationHandle&lt;Route&gt;? Nav;
/// // ...
/// var nav = UseNavigation(Route.Home);
/// Nav = nav; // capture for later use from anywhere
/// </code>
/// <para>
/// The handle is bound to the dispatcher of the component that created it. Stashed
/// in a <c>static</c>, it outlives the page and pins (leaks) that dispatcher; once
/// the dispatcher shuts down, its mutators throw. Prefer child-mode
/// <c>UseNavigation&lt;TRoute&gt;()</c> (no initial value) in a descendant to obtain
/// the same handle via context, or pass it through <c>Context</c> explicitly.
/// </para>
/// <para>
/// Detection is a pure <see cref="SymbolKind.Field"/> gate: any <c>static</c> field
/// typed <c>NavigationHandle&lt;&gt;</c>. The handle's constructor is
/// <c>internal</c>, so the only way consumer code can obtain one is <c>UseNavigation</c> —
/// which makes "static field typed <c>NavigationHandle&lt;&gt;</c>" equivalent to the
/// spec's "assigned from <c>UseNavigation</c>", and robustly covers the canonical form
/// above where the value flows through an intermediate local. No code-fix (the correct
/// rewrite depends on how the handle is consumed elsewhere).
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StaticNavigationHandleAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "REACTOR_NAV_001";

    private const string NavigationHandleTypeName = "NavigationHandle";
    private const string NavigationNamespace = "Microsoft.UI.Reactor.Navigation";

    private static readonly LocalizableString Title =
        "UseNavigation handle stored in a static field";
    private static readonly LocalizableString MessageFormat =
        "Static field '{0}' holds a UseNavigation handle, which outlives the page and pins its dispatcher";
    private static readonly LocalizableString Description =
        "A NavigationHandle<TRoute> is bound to the dispatcher of the component that created it. " +
        "Stashing it in a static field keeps it — and its dispatcher — alive past the page's " +
        "lifetime (a leak); after that dispatcher shuts down the handle's mutators throw. " +
        "Access the shared handle from a descendant with child-mode UseNavigation<TRoute>() " +
        "(no initial value), or pass it through Context, instead of a static field.";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        MessageFormat,
        "Reactor.Navigation",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: Description);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeField, SymbolKind.Field);
    }

    private static void AnalyzeField(SymbolAnalysisContext context)
    {
        var field = (IFieldSymbol)context.Symbol;

        // Only static storage leaks the handle across page lifetimes.
        if (!field.IsStatic)
            return;

        // Skip compiler-generated fields (auto-property backing fields, closures,
        // enum value fields, etc.) — the author can't act on those declarations.
        if (field.IsImplicitlyDeclared)
            return;

        if (!IsNavigationHandle(field.Type))
            return;

        var location = field.Locations.FirstOrDefault() ?? Location.None;
        context.ReportDiagnostic(Diagnostic.Create(Rule, location, field.Name));
    }

    private static bool IsNavigationHandle(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named)
            return false;

        var definition = named.OriginalDefinition;
        return definition.Arity == 1
            && definition.Name == NavigationHandleTypeName
            && definition.ContainingNamespace?.ToDisplayString() == NavigationNamespace;
    }
}

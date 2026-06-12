using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.UI.Reactor.Wrappers.Generator;

/// <summary>
/// REACTORGEN002 — errors when a name in the <c>Include</c> or <c>Exclude</c>
/// list of a <c>[GenerateReactorWrapper(...)]</c> attribute does not correspond
/// to a public property on the wrapped control. Catches typos and stale names
/// (e.g. after a control library rename) at author time instead of silently
/// surfacing/​dropping nothing.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class WrapperIncludeExcludeAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "REACTORGEN002";

    private const string AttributeFqn = "Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapperAttribute";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Unknown control property in Include/Exclude",
        messageFormat: "Property '{0}' in {1} does not exist on control '{2}'",
        category: "Reactor.Wrappers",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Include/Exclude on [GenerateReactorWrapper] must name public properties of the wrapped control.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.Attribute);
    }

    private static void Analyze(SyntaxNodeAnalysisContext ctx)
    {
        var attr = (AttributeSyntax)ctx.Node;
        var sm = ctx.SemanticModel;

        if (sm.GetSymbolInfo(attr, ctx.CancellationToken).Symbol is not IMethodSymbol ctor) return;
        if (ctor.ContainingType.ToDisplayString() != AttributeFqn) return;

        var argList = attr.ArgumentList;
        if (argList is null) return;

        // First positional typeof(...) argument → the control type.
        INamedTypeSymbol? control = null;
        foreach (var a in argList.Arguments)
        {
            if (a.NameEquals is null && a.NameColon is null && a.Expression is TypeOfExpressionSyntax toe)
            {
                control = sm.GetTypeInfo(toe.Type, ctx.CancellationToken).Type as INamedTypeSymbol;
                break;
            }
        }
        if (control is null) return;

        HashSet<string>? propNames = null;
        foreach (var a in argList.Arguments)
        {
            var argName = a.NameEquals?.Name.Identifier.ValueText;
            if (argName != "Include" && argName != "Exclude") continue;

            foreach (var element in EnumerateElements(a.Expression))
            {
                var cv = sm.GetConstantValue(element, ctx.CancellationToken);
                if (!cv.HasValue || cv.Value is not string name) continue;

                propNames ??= CollectPropertyNames(control);
                if (!propNames.Contains(name))
                    ctx.ReportDiagnostic(Diagnostic.Create(Rule, element.GetLocation(), name, argName, control.Name));
            }
        }
    }

    private static IEnumerable<ExpressionSyntax> EnumerateElements(ExpressionSyntax expr) => expr switch
    {
        ImplicitArrayCreationExpressionSyntax iac => iac.Initializer.Expressions,
        ArrayCreationExpressionSyntax { Initializer: { } init } => init.Expressions,
        InitializerExpressionSyntax init => init.Expressions,
        CollectionExpressionSyntax col => col.Elements.OfType<ExpressionElementSyntax>().Select(e => e.Expression),
        _ => Enumerable.Empty<ExpressionSyntax>(),
    };

    private static HashSet<string> CollectPropertyNames(INamedTypeSymbol control)
    {
        var names = new HashSet<string>();
        for (ITypeSymbol? t = control; t is not null; t = t.BaseType)
            foreach (var p in t.GetMembers().OfType<IPropertySymbol>())
                if (p.DeclaredAccessibility == Accessibility.Public && !p.IsStatic && !p.IsIndexer)
                    names.Add(p.Name);
        return names;
    }
}

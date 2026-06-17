using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.UI.Reactor.Wrappers.Generator;

/// <summary>
/// REACTORGEN006 — errors when a <c>[WrapOneWay("Property")]</c> names a property
/// that is not a public settable property of the wrapped control.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class WrapOneWayAnalyzer : DiagnosticAnalyzer
{
    private const string GenAttrFqn = "Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapperAttribute";
    private const string OneWayAttrFqn = "Microsoft.UI.Reactor.Wrappers.WrapOneWayAttribute";

    private static readonly DiagnosticDescriptor UnknownProperty = new(
        id: "REACTORGEN006",
        title: "Unknown WrapOneWay property",
        messageFormat: "WrapOneWay property '{0}' is not a public settable property of control '{1}'",
        category: "Reactor.Wrappers",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(UnknownProperty);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSymbolAction(Analyze, SymbolKind.NamedType);
    }

    private static void Analyze(SymbolAnalysisContext ctx)
    {
        var type = (INamedTypeSymbol)ctx.Symbol;
        var attrs = type.GetAttributes();

        INamedTypeSymbol? control = null;
        var oneWays = new List<AttributeData>();
        foreach (var a in attrs)
        {
            var name = a.AttributeClass?.ToDisplayString();
            if (name == GenAttrFqn && a.ConstructorArguments.Length == 1 &&
                a.ConstructorArguments[0].Value is INamedTypeSymbol c)
                control = c;
            else if (name == OneWayAttrFqn)
                oneWays.Add(a);
        }
        if (control is null || oneWays.Count == 0) return;

        var settable = SettablePropertyNames(control);

        foreach (var a in oneWays)
        {
            if (a.ConstructorArguments.Length < 1 || a.ConstructorArguments[0].Value is not string prop) continue;
            if (settable.Contains(prop)) continue;
            var loc = a.ApplicationSyntaxReference?.GetSyntax(ctx.CancellationToken).GetLocation() ?? Location.None;
            ctx.ReportDiagnostic(Diagnostic.Create(UnknownProperty, loc, prop, control.Name));
        }
    }

    private static HashSet<string> SettablePropertyNames(INamedTypeSymbol control)
    {
        var names = new HashSet<string>();
        for (ITypeSymbol? t = control; t is not null; t = t.BaseType)
            foreach (var p in t.GetMembers().OfType<IPropertySymbol>())
                if (p.DeclaredAccessibility == Accessibility.Public && !p.IsStatic && !p.IsIndexer &&
                    p.SetMethod is { DeclaredAccessibility: Accessibility.Public })
                    names.Add(p.Name);
        return names;
    }
}

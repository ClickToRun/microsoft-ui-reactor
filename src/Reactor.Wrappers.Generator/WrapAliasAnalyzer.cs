using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.UI.Reactor.Wrappers.Generator;

/// <summary>
/// REACTORGEN005 — errors when a <c>[WrapAlias("Name", "ControlProperty")]</c>
/// names a <c>ControlProperty</c> that is not a public settable property of the
/// wrapped control (catches typos / renamed control properties at author time).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class WrapAliasAnalyzer : DiagnosticAnalyzer
{
    private const string GenAttrFqn = "Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapperAttribute";
    private const string AliasAttrFqn = "Microsoft.UI.Reactor.Wrappers.WrapAliasAttribute";

    private static readonly DiagnosticDescriptor UnknownControlProperty = new(
        id: "REACTORGEN005",
        title: "Unknown WrapAlias control property",
        messageFormat: "WrapAlias control property '{0}' is not a public settable property of control '{1}'",
        category: "Reactor.Wrappers",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(UnknownControlProperty);

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
        var aliases = new List<AttributeData>();
        foreach (var a in attrs)
        {
            var name = a.AttributeClass?.ToDisplayString();
            if (name == GenAttrFqn && a.ConstructorArguments.Length == 1 &&
                a.ConstructorArguments[0].Value is INamedTypeSymbol c)
                control = c;
            else if (name == AliasAttrFqn)
                aliases.Add(a);
        }
        if (control is null || aliases.Count == 0) return;

        var settable = SettablePropertyNames(control);

        foreach (var a in aliases)
        {
            if (a.ConstructorArguments.Length < 2 || a.ConstructorArguments[1].Value is not string controlProp) continue;
            if (settable.Contains(controlProp)) continue;
            var loc = a.ApplicationSyntaxReference?.GetSyntax(ctx.CancellationToken).GetLocation() ?? Location.None;
            ctx.ReportDiagnostic(Diagnostic.Create(UnknownControlProperty, loc, controlProp, control.Name));
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

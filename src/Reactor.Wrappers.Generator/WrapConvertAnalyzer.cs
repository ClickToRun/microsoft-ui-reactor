using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.UI.Reactor.Wrappers.Generator;

/// <summary>
/// REACTORGEN008 — errors when a <c>[WrapConvert("Property")]</c> names a property
/// that is not a public settable property of the wrapped control, or whose type
/// has no unambiguous public single-argument constructor (so the ergonomic scalar
/// element value cannot be converted into it via <c>new Struct(v)</c>). Recognized
/// for both <c>[GenerateReactorWrapper]</c> and <c>[GenerateReactorDescriptor]</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class WrapConvertAnalyzer : DiagnosticAnalyzer
{
    private const string GenAttrFqn = "Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapperAttribute";
    private const string DescAttrFqn = "Microsoft.UI.Reactor.Wrappers.GenerateReactorDescriptorAttribute";
    private const string ConvertAttrFqn = "Microsoft.UI.Reactor.Wrappers.WrapConvertAttribute";

    private static readonly DiagnosticDescriptor InvalidConvert = new(
        id: "REACTORGEN008",
        title: "Invalid WrapConvert property",
        messageFormat: "WrapConvert property '{0}' must be a public settable property of control '{1}' whose type has a public single-argument constructor",
        category: "Reactor.Wrappers",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(InvalidConvert);

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
        var converts = new List<AttributeData>();
        foreach (var a in attrs)
        {
            var name = a.AttributeClass?.ToDisplayString();
            if ((name == GenAttrFqn || name == DescAttrFqn) && a.ConstructorArguments.Length == 1 &&
                a.ConstructorArguments[0].Value is INamedTypeSymbol c)
                control = c;
            else if (name == ConvertAttrFqn)
                converts.Add(a);
        }
        if (control is null || converts.Count == 0) return;

        var convertible = ConvertiblePropertyNames(control);

        foreach (var a in converts)
        {
            if (a.ConstructorArguments.Length < 1 || a.ConstructorArguments[0].Value is not string prop) continue;
            if (convertible.Contains(prop)) continue;
            var loc = a.ApplicationSyntaxReference?.GetSyntax(ctx.CancellationToken).GetLocation() ?? Location.None;
            ctx.ReportDiagnostic(Diagnostic.Create(InvalidConvert, loc, prop, control.Name));
        }
    }

    // Public settable properties whose type has exactly one public one-argument
    // constructor (the conversion target for [WrapConvert]).
    private static HashSet<string> ConvertiblePropertyNames(INamedTypeSymbol control)
    {
        var names = new HashSet<string>();
        for (ITypeSymbol? t = control; t is not null; t = t.BaseType)
            foreach (var p in t.GetMembers().OfType<IPropertySymbol>())
                if (p.DeclaredAccessibility == Accessibility.Public && !p.IsStatic && !p.IsIndexer &&
                    p.SetMethod is { DeclaredAccessibility: Accessibility.Public } &&
                    HasSingleArgCtor(p.Type))
                    names.Add(p.Name);
        return names;
    }

    private static bool HasSingleArgCtor(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named) return false;
        var count = 0;
        foreach (var ctor in named.InstanceConstructors)
            if (ctor.DeclaredAccessibility == Accessibility.Public && ctor.Parameters.Length == 1)
                count++;
        return count == 1;
    }
}

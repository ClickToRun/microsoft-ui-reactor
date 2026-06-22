using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.UI.Reactor.Wrappers.Generator;

/// <summary>
/// Validates <c>[WrapElementSlot("Prop", ControlProperty="...")]</c>:
/// <list type="bullet">
/// <item>REACTORGEN013 — the target control property must be a public settable property.</item>
/// <item>REACTORGEN014 — that property's type must be assignable from a mounted
/// <c>UIElement</c> (i.e. <c>object</c> or a <c>UIElement</c>-derived type); otherwise the
/// generated assignment can never succeed.</item>
/// <item>REACTORGEN015 — the element-facing slot property name (the attribute's first
/// argument) must be a valid C# identifier, since it becomes a generated member name.</item>
/// </list>
/// Applies under both <c>[GenerateReactorWrapper]</c> and <c>[GenerateReactorDescriptor]</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class WrapElementSlotAnalyzer : DiagnosticAnalyzer
{
    private const string WrapperAttrFqn = "Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapperAttribute";
    private const string DescriptorAttrFqn = "Microsoft.UI.Reactor.Wrappers.GenerateReactorDescriptorAttribute";
    private const string SlotAttrFqn = "Microsoft.UI.Reactor.Wrappers.WrapElementSlotAttribute";

    private static readonly DiagnosticDescriptor UnknownProperty = new(
        id: "REACTORGEN013",
        title: "Unknown WrapElementSlot control property",
        messageFormat: "WrapElementSlot target property '{0}' is not a public settable property of control '{1}' (set ControlProperty= if the control property name differs from the slot name)",
        category: "Reactor.Wrappers",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NotElementTyped = new(
        id: "REACTORGEN014",
        title: "WrapElementSlot control property is not element-typed",
        messageFormat: "WrapElementSlot target property '{0}' on control '{1}' is type '{2}', which is not assignable from a mounted UIElement (use object or a UIElement-derived type)",
        category: "Reactor.Wrappers",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidSlotName = new(
        id: "REACTORGEN015",
        title: "Invalid WrapElementSlot property name",
        messageFormat: "WrapElementSlot property name '{0}' is not a valid C# identifier; it is emitted as a generated member name",
        category: "Reactor.Wrappers",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(UnknownProperty, NotElementTyped, InvalidSlotName);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSymbolAction(Analyze, SymbolKind.NamedType);
    }

    private static void Analyze(SymbolAnalysisContext ctx)
    {
        var type = (INamedTypeSymbol)ctx.Symbol;

        INamedTypeSymbol? control = null;
        var slots = new List<AttributeData>();
        foreach (var a in type.GetAttributes())
        {
            var name = a.AttributeClass?.ToDisplayString();
            if ((name == WrapperAttrFqn || name == DescriptorAttrFqn) &&
                a.ConstructorArguments.Length >= 1 &&
                a.ConstructorArguments[0].Value is INamedTypeSymbol c)
                control = c;
            else if (name == SlotAttrFqn)
                slots.Add(a);
        }
        if (control is null || slots.Count == 0) return;

        foreach (var a in slots.Where(a => a.ConstructorArguments.Length >= 1 && a.ConstructorArguments[0].Value is string))
        {
            var slotName = (string)a.ConstructorArguments[0].Value!;
            var controlProp = slotName;
            foreach (var na in a.NamedArguments.Where(na => na.Key == "ControlProperty"))
                if (na.Value.Value is string cp) controlProp = cp;

            var loc = a.ApplicationSyntaxReference?.GetSyntax(ctx.CancellationToken).GetLocation() ?? Location.None;

            if (!IsValidIdentifier(slotName))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(InvalidSlotName, loc, slotName));
                continue;
            }

            var prop = FindSettableProperty(control, controlProp);
            if (prop is null)
            {
                ctx.ReportDiagnostic(Diagnostic.Create(UnknownProperty, loc, controlProp, control.Name));
                continue;
            }
            if (!IsElementAssignable(prop.Type))
                ctx.ReportDiagnostic(Diagnostic.Create(
                    NotElementTyped, loc, controlProp, control.Name, prop.Type.ToDisplayString()));
        }
    }

    // The slot name becomes a generated init-property / member identifier; reject
    // anything that isn't a valid C# identifier or that collides with a reserved keyword.
    // Contextual keywords are allowed (they are legal identifiers).
    private static bool IsValidIdentifier(string name) =>
        SyntaxFacts.IsValidIdentifier(name) &&
        SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None;

    private static IPropertySymbol? FindSettableProperty(INamedTypeSymbol control, string name)
    {
        for (ITypeSymbol? t = control; t is not null; t = t.BaseType)
            foreach (var p in t.GetMembers(name).OfType<IPropertySymbol>()
                .Where(p => p.DeclaredAccessibility == Accessibility.Public && !p.IsStatic && !p.IsIndexer &&
                    p.SetMethod is { DeclaredAccessibility: Accessibility.Public }))
                return p;
        return null;
    }

    // Assignable from a mounted UIElement: `object`, or a UIElement-derived type.
    private static bool IsElementAssignable(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_Object) return true;
        for (ITypeSymbol? t = type; t is not null; t = t.BaseType)
            if (t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::Microsoft.UI.Xaml.UIElement")
                return true;
        return false;
    }
}

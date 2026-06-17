using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.UI.Reactor.Wrappers.Generator;

/// <summary>
/// Validates <c>[WrapControlled("Prop", ChangedEvent = "Event")]</c> /
/// <c>[WrapControlled("Prop", Events = new[]{"A","B"})]</c> overrides
/// against the wrapped control:
/// <list type="bullet">
///   <item><b>REACTORGEN003</b> — <c>Property</c> is not a public settable property of the control.</item>
///   <item><b>REACTORGEN004</b> — a change event (<c>ChangedEvent</c>, any entry of <c>Events</c>, or
///   <c>{Property}Changed</c>) is not a public event with a two-parameter (sender, args) delegate.</item>
/// </list>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class WrapControlledAnalyzer : DiagnosticAnalyzer
{
    private const string GenAttrFqn = "Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapperAttribute";
    private const string WcAttrFqn = "Microsoft.UI.Reactor.Wrappers.WrapControlledAttribute";

    private static readonly DiagnosticDescriptor UnknownProperty = new(
        id: "REACTORGEN003",
        title: "Unknown WrapControlled property",
        messageFormat: "WrapControlled property '{0}' is not a public settable property of control '{1}'",
        category: "Reactor.Wrappers",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidEvent = new(
        id: "REACTORGEN004",
        title: "Invalid WrapControlled change event",
        messageFormat: "Change event '{0}' for WrapControlled property '{1}' is not a public (sender, args) event on control '{2}'",
        category: "Reactor.Wrappers",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(UnknownProperty, InvalidEvent);

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
        var controlled = new List<AttributeData>();
        foreach (var a in attrs)
        {
            var name = a.AttributeClass?.ToDisplayString();
            if (name == GenAttrFqn && a.ConstructorArguments.Length == 1 &&
                a.ConstructorArguments[0].Value is INamedTypeSymbol c)
                control = c;
            else if (name == WcAttrFqn)
                controlled.Add(a);
        }
        if (control is null || controlled.Count == 0) return;

        var settable = SettablePropertyNames(control);
        var events = EventsByName(control);

        foreach (var a in controlled)
        {
            if (a.ConstructorArguments.Length < 1 || a.ConstructorArguments[0].Value is not string prop) continue;
            var loc = a.ApplicationSyntaxReference?.GetSyntax(ctx.CancellationToken).GetLocation() ?? Location.None;

            if (!settable.Contains(prop))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(UnknownProperty, loc, prop, control.Name));
                continue;
            }

            string? changedEvent = null;
            string[]? eventList = null;
            foreach (var na in a.NamedArguments)
            {
                if (na.Key == "ChangedEvent" && na.Value.Value is string ce) changedEvent = ce;
                else if (na.Key == "Events" && !na.Value.Values.IsDefaultOrEmpty)
                    eventList = na.Value.Values.Select(v => v.Value as string).Where(s => s is not null).Select(s => s!).ToArray();
            }
            // Events[] takes precedence; else single ChangedEvent; else convention.
            var evNames = eventList is { Length: > 0 } ? eventList
                : new[] { changedEvent ?? prop + "Changed" };

            foreach (var evName in evNames)
            {
                if (!events.TryGetValue(evName, out var evt) ||
                    evt.Type is not INamedTypeSymbol del ||
                    del.DelegateInvokeMethod is not { Parameters.Length: 2 })
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(InvalidEvent, loc, evName, prop, control.Name));
                }
            }
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

    private static Dictionary<string, IEventSymbol> EventsByName(INamedTypeSymbol control)
    {
        var map = new Dictionary<string, IEventSymbol>();
        for (ITypeSymbol? t = control; t is not null; t = t.BaseType)
            foreach (var e in t.GetMembers().OfType<IEventSymbol>())
                if (e.DeclaredAccessibility == Accessibility.Public && !e.IsStatic && !map.ContainsKey(e.Name))
                    map[e.Name] = e;
        return map;
    }
}

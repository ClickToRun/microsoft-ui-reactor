using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.UI.Reactor.Wrappers.Generator;

/// <summary>
/// Validates <c>[WrapEvent("EventName", Arg = "Property")]</c> /
/// <c>[WrapEvent("E", Args = new[]{ ... })]</c> at the attribute site so author
/// typos surface as a clear diagnostic instead of a cryptic generated-code error
/// (a bad <c>Arg</c> currently compiles to <c>args.Eror</c> → CS1061 in a
/// generated file; a bad <c>EventName</c> is silently ignored). Recognized for
/// both <c>[GenerateReactorWrapper]</c> and <c>[GenerateReactorDescriptor]</c>.
/// <list type="bullet">
///   <item><b>REACTORGEN009</b> — <c>EventName</c> is not a public event of the control.</item>
///   <item><b>REACTORGEN010</b> — an <c>Arg</c> / <c>Args</c> entry is not a public
///   property of the event's argument type.</item>
/// </list>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class WrapEventAnalyzer : DiagnosticAnalyzer
{
    private const string GenAttrFqn = "Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapperAttribute";
    private const string DescAttrFqn = "Microsoft.UI.Reactor.Wrappers.GenerateReactorDescriptorAttribute";
    private const string EventAttrFqn = "Microsoft.UI.Reactor.Wrappers.WrapEventAttribute";

    private static readonly DiagnosticDescriptor EventNotFound = new(
        id: "REACTORGEN009",
        title: "Invalid WrapEvent event",
        messageFormat: "WrapEvent event '{0}' is not a public event of control '{1}'",
        category: "Reactor.Wrappers",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ArgNotFound = new(
        id: "REACTORGEN010",
        title: "Invalid WrapEvent argument property",
        messageFormat: "WrapEvent argument '{0}' is not a public property of event '{1}'s argument type '{2}'",
        category: "Reactor.Wrappers",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(EventNotFound, ArgNotFound);

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
        var wrapEvents = new List<AttributeData>();
        foreach (var a in type.GetAttributes())
        {
            var name = a.AttributeClass?.ToDisplayString();
            if ((name == GenAttrFqn || name == DescAttrFqn) && a.ConstructorArguments.Length == 1 &&
                a.ConstructorArguments[0].Value is INamedTypeSymbol c)
                control = c;
            else if (name == EventAttrFqn)
                wrapEvents.Add(a);
        }
        if (control is null || wrapEvents.Count == 0) return;

        foreach (var a in wrapEvents)
        {
            if (a.ConstructorArguments.Length < 1 || a.ConstructorArguments[0].Value is not string eventName) continue;
            var loc = a.ApplicationSyntaxReference?.GetSyntax(ctx.CancellationToken).GetLocation() ?? Location.None;

            var evt = FindPublicEvent(control, eventName);
            if (evt is null)
            {
                ctx.ReportDiagnostic(Diagnostic.Create(EventNotFound, loc, eventName, control.Name));
                continue;
            }

            // The event-args type is the 2nd parameter of the delegate's Invoke
            // (sender, args). If the delegate doesn't follow that shape we can't
            // validate the projected properties — leave it to the generator.
            var argsType = (evt.Type as INamedTypeSymbol)?.DelegateInvokeMethod?.Parameters is { Length: 2 } ps
                ? ps[1].Type
                : null;
            if (argsType is null) continue;

            foreach (var argProp in ArgPropertyNames(a))
            {
                if (HasPublicProperty(argsType, argProp)) continue;
                ctx.ReportDiagnostic(Diagnostic.Create(
                    ArgNotFound, loc, argProp, eventName, argsType.Name));
            }
        }
    }

    private static IEventSymbol? FindPublicEvent(INamedTypeSymbol control, string name)
    {
        for (ITypeSymbol? t = control; t is not null; t = t.BaseType)
            foreach (var e in t.GetMembers(name).OfType<IEventSymbol>())
                if (e.DeclaredAccessibility == Accessibility.Public && !e.IsStatic)
                    return e;
        return null;
    }

    private static IEnumerable<string> ArgPropertyNames(AttributeData a)
    {
        foreach (var na in a.NamedArguments)
        {
            if (na.Key == "Args" && !na.Value.Values.IsDefaultOrEmpty)
            {
                foreach (var v in na.Value.Values)
                    if (v.Value is string s)
                        yield return s;
            }
            else if (na.Key == "Arg" && na.Value.Value is string single)
            {
                yield return single;
            }
        }
    }

    private static bool HasPublicProperty(ITypeSymbol argsType, string name)
    {
        for (ITypeSymbol? t = argsType; t is not null; t = t.BaseType)
            foreach (var p in t.GetMembers(name).OfType<IPropertySymbol>())
                if (p.DeclaredAccessibility == Accessibility.Public && !p.IsStatic && !p.IsIndexer)
                    return true;
        return false;
    }
}

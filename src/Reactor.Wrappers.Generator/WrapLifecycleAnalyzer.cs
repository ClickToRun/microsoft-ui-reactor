using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.UI.Reactor.Wrappers.Generator;

/// <summary>
/// REACTORGEN011 — validates that each method named by
/// <c>[WrapLifecycle(onMounted, OnUnmounted = ...)]</c> is a <c>static</c> method on
/// the element record taking a single parameter to which the wrapped control type is
/// assignable (<c>static void M(TControl)</c>). Catches a typo'd / mis-signatured
/// lifecycle method at the attribute site instead of as a generated-code error.
/// Recognized for both <c>[GenerateReactorWrapper]</c> and <c>[GenerateReactorDescriptor]</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class WrapLifecycleAnalyzer : DiagnosticAnalyzer
{
    private const string GenAttrFqn = "Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapperAttribute";
    private const string DescAttrFqn = "Microsoft.UI.Reactor.Wrappers.GenerateReactorDescriptorAttribute";
    private const string LifecycleAttrFqn = "Microsoft.UI.Reactor.Wrappers.WrapLifecycleAttribute";

    private static readonly DiagnosticDescriptor InvalidMethod = new(
        id: "REACTORGEN011",
        title: "Invalid WrapLifecycle method",
        messageFormat: "WrapLifecycle method '{0}' must be a static method on '{1}' taking a single '{2}' parameter",
        category: "Reactor.Wrappers",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(InvalidMethod);

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
        AttributeData? lifecycle = null;
        foreach (var a in type.GetAttributes())
        {
            var name = a.AttributeClass?.ToDisplayString();
            if ((name == GenAttrFqn || name == DescAttrFqn) && a.ConstructorArguments.Length == 1 &&
                a.ConstructorArguments[0].Value is INamedTypeSymbol c)
                control = c;
            else if (name == LifecycleAttrFqn)
                lifecycle = a;
        }
        if (control is null || lifecycle is null) return;

        var loc = lifecycle.ApplicationSyntaxReference?.GetSyntax(ctx.CancellationToken).GetLocation() ?? Location.None;

        // Constructor arg = OnMounted; named "OnUnmounted" = optional teardown.
        var methodNames = new List<string>();
        if (lifecycle.ConstructorArguments.Length >= 1 && lifecycle.ConstructorArguments[0].Value is string onMounted)
            methodNames.Add(onMounted);
        foreach (var na in lifecycle.NamedArguments)
            if (na.Key == "OnUnmounted" && na.Value.Value is string onUnmounted)
                methodNames.Add(onUnmounted);

        foreach (var methodName in methodNames)
        {
            if (!HasValidLifecycleMethod(type, methodName, control))
                ctx.ReportDiagnostic(Diagnostic.Create(InvalidMethod, loc, methodName, type.Name, control.Name));
        }
    }

    private static bool HasValidLifecycleMethod(INamedTypeSymbol element, string name, INamedTypeSymbol control)
    {
        foreach (var m in element.GetMembers(name).OfType<IMethodSymbol>())
            if (m.IsStatic && m.Parameters.Length == 1 && IsAssignableTo(control, m.Parameters[0].Type))
                return true;
        return false;
    }

    // True when an instance of `control` can be passed where `target` is expected
    // (target is the control type or any of its base types).
    private static bool IsAssignableTo(INamedTypeSymbol control, ITypeSymbol target)
    {
        for (ITypeSymbol? t = control; t is not null; t = t.BaseType)
            if (SymbolEqualityComparer.Default.Equals(t, target))
                return true;
        return false;
    }
}

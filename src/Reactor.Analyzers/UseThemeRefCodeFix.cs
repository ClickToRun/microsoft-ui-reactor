using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.UI.Reactor.Analyzers;

/// <summary>
/// Code fix for REACTOR_THEME_001 / REACTOR_THEME_004: replaces a hard-coded color string or an
/// inline <c>new SolidColorBrush(Colors.X)</c> with the matching <c>Theme.X</c> token, but only
/// when the color has a known mapping <em>and</em> the token actually resolves on the real
/// <c>Theme</c> — otherwise the diagnostic stands with no auto-fix.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UseThemeRefCodeFix))]
[Shared]
public sealed class UseThemeRefCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(UseThemeRefAnalyzer.DiagnosticId, UseThemeRefAnalyzer.BrushDiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        SemanticModel? semanticModel = null;

        foreach (var diagnostic in context.Diagnostics)
        {
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);

            // Resolve which node to replace and which token to replace it with, depending on the rule.
            string? token = null;
            SyntaxNode? target = null;

            if (node.FirstAncestorOrSelf<LiteralExpressionSyntax>() is { } literal &&
                literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                // REACTOR_THEME_001 — hard-coded color string.
                if (UseThemeRefAnalyzer.ColorToThemeToken.TryGetValue(literal.Token.ValueText, out var t))
                {
                    token = t;
                    target = literal;
                }
            }
            else if (node.FirstAncestorOrSelf<ObjectCreationExpressionSyntax>() is { } creation)
            {
                // REACTOR_THEME_004 — inline new SolidColorBrush(Colors.X).
                var colorName = UseThemeRefAnalyzer.TryGetColorName(creation);
                if (colorName is not null &&
                    UseThemeRefAnalyzer.ColorToThemeToken.TryGetValue(colorName, out var t))
                {
                    token = t;
                    target = creation;
                }
            }

            if (token is null || target is null)
                continue; // Unmapped color — no key to invent, so the diagnostic stands without a fix.

            semanticModel ??= await context.Document
                .GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);

            var themeAccess = TryBuildThemeReference(semanticModel, target.SpanStart, token, target);
            if (themeAccess is null)
                continue; // Theme.<token> can't be resolved here — never emit non-compiling code.

            var nodeToReplace = target;
            var replacement = themeAccess;

            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Replace with Theme.{token}",
                    _ => Task.FromResult(context.Document.WithSyntaxRoot(
                        root.ReplaceNode(nodeToReplace, replacement))),
                    equivalenceKey: $"{diagnostic.Id}_{token}"),
                diagnostic);
        }
    }

    /// <summary>
    /// Builds a <c>Theme.&lt;token&gt;</c> expression guaranteed to compile at
    /// <paramref name="position"/>: it confirms the member exists on the real
    /// <c>Microsoft.UI.Reactor.Core.Theme</c> and renders the shortest unambiguous type name via
    /// <see cref="SymbolDisplayExtensions.ToMinimalDisplayString"/> (falling back to a fully
    /// <c>global::</c>-qualified name when no semantic model is available). Returns null when the
    /// token can't be resolved, so the caller withholds the fix rather than emit broken code.
    /// </summary>
    private static ExpressionSyntax? TryBuildThemeReference(
        SemanticModel? semanticModel, int position, string token, SyntaxNode triviaSource)
    {
        if (semanticModel is not null)
        {
            var themeType = semanticModel.Compilation.GetTypeByMetadataName("Microsoft.UI.Reactor.Core.Theme");
            if (themeType is null)
                return null;
            if (!themeType.GetMembers(token).Any(static m => m is IPropertySymbol or IFieldSymbol))
                return null;

            var themeName = themeType.ToMinimalDisplayString(semanticModel, position);
            return SyntaxFactory.ParseExpression($"{themeName}.{token}").WithTriviaFrom(triviaSource);
        }

        return SyntaxFactory.ParseExpression($"global::Microsoft.UI.Reactor.Core.Theme.{token}")
            .WithTriviaFrom(triviaSource);
    }
}

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.Tooling;

/// <summary>
/// Source lint for the ReactorGallery sample pages. The gallery is the reference app, so a
/// card that silently renders nothing teaches the wrong pattern — and the three mistakes
/// guarded here are all statically detectable and all shipped at least once:
///
/// <list type="bullet">
/// <item>an <c>ItemsView</c> view builder returning a non-<c>ItemContainer</c> root, which
/// makes the whole page throw (<c>ItemsViewElement.GuardedViewBuilder</c>);</item>
/// <item><c>.Background(...)</c> on a shape, which the reconciler only applies to
/// Panel / Control / Border and therefore drops, rendering an invisible shape;</item>
/// <item>an <c>ms-appx:///</c> asset that either does not exist or is never copied to the
/// output folder, which renders a blank image with no error of any kind.</item>
/// </list>
///
/// Roslyn parses the page sources directly — no gallery build, no WinUI objects, so this
/// stays in the headless unit tier. Each fact also asserts the scan actually found the
/// construct it polices, so deleting the sample would fail the test rather than pass it
/// vacuously.
/// </summary>
public sealed class GallerySampleLintTests
{
    static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Join(dir, "Reactor.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("Could not locate repo root (Reactor.slnx) from " + AppContext.BaseDirectory);
    }

    static string GalleryDir() => Path.Join(RepoRoot(), "samples", "ReactorGallery");

    static IReadOnlyList<(string Path, SyntaxNode Root)> Pages()
    {
        var pagesDir = Path.Join(GalleryDir(), "ControlPages");
        Assert.True(Directory.Exists(pagesDir), $"gallery ControlPages directory not found at {pagesDir}");

        var pages = Directory.EnumerateFiles(pagesDir, "*.cs", SearchOption.AllDirectories)
            .OrderBy(p => p, global::System.StringComparer.Ordinal)
            .Select(p => (Path: p, Root: CSharpSyntaxTree.ParseText(File.ReadAllText(p)).GetRoot()))
            .ToList();

        Assert.NotEmpty(pages);
        return pages;
    }

    static string Rel(string absolute) =>
        Path.GetRelativePath(RepoRoot(), absolute).Replace('\\', '/');

    static string Where(string path, SyntaxNode node) =>
        $"{Rel(path)}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";

    /// <summary>Simple name of an invocation: <c>Foo(...)</c> and <c>x.Foo(...)</c> both yield "Foo".</summary>
    static string? InvokedName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        GenericNameSyntax generic => generic.Identifier.Text,
        MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
        _ => null,
    };

    /// <summary>
    /// Name of the call that *starts* a fluent chain: for <c>ItemContainer(x).Margin(4)</c>
    /// and <c>Rectangle().Size(8).Background("#fff")</c> this yields "ItemContainer" and
    /// "Rectangle". Returns null when the head is not a plain invocation.
    /// </summary>
    static string? ChainHeadName(ExpressionSyntax expression)
    {
        var current = expression;
        while (true)
        {
            switch (current)
            {
                case ParenthesizedExpressionSyntax paren:
                    current = paren.Expression;
                    continue;
                case WithExpressionSyntax with:
                    current = with.Expression;
                    continue;
                case PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } suppress:
                    current = suppress.Operand;
                    continue;
                case InvocationExpressionSyntax invocation:
                    if (invocation.Expression is MemberAccessExpressionSyntax member)
                    {
                        current = member.Expression;
                        continue;
                    }
                    return InvokedName(invocation);
                default:
                    return null;
            }
        }
    }

    /// <summary>Every expression a lambda can hand back, flattening conditional branches.</summary>
    static IEnumerable<ExpressionSyntax> ReturnedExpressions(AnonymousFunctionExpressionSyntax lambda)
    {
        var roots = new List<ExpressionSyntax>();
        if (lambda.ExpressionBody is { } body)
            roots.Add(body);
        if (lambda.Block is { } block)
            roots.AddRange(block.DescendantNodes()
                .OfType<ReturnStatementSyntax>()
                .Select(r => r.Expression)
                .OfType<ExpressionSyntax>());

        foreach (var root in roots)
            foreach (var leaf in Flatten(root))
                yield return leaf;

        static IEnumerable<ExpressionSyntax> Flatten(ExpressionSyntax expression)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax paren:
                    foreach (var e in Flatten(paren.Expression)) yield return e;
                    break;
                case ConditionalExpressionSyntax conditional:
                    foreach (var e in Flatten(conditional.WhenTrue)) yield return e;
                    foreach (var e in Flatten(conditional.WhenFalse)) yield return e;
                    break;
                case CastExpressionSyntax cast:
                    foreach (var e in Flatten(cast.Expression)) yield return e;
                    break;
                default:
                    yield return expression;
                    break;
            }
        }
    }

    // ── ItemsView view builders must return an ItemContainer root ────────────

    [Fact]
    public void ItemsView_ViewBuilders_ReturnItemContainerRoots()
    {
        var offenders = new List<string>();
        var checkedBuilders = 0;

        foreach (var (path, root) in Pages())
        {
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (InvokedName(invocation) != "ItemsView") continue;

                var builder = invocation.ArgumentList.Arguments
                    .Select(a => a.Expression)
                    .OfType<AnonymousFunctionExpressionSyntax>()
                    // The view builder is the (item, index) => Element lambda — two parameters.
                    .LastOrDefault();
                if (builder is null) continue;

                foreach (var returned in ReturnedExpressions(builder))
                {
                    checkedBuilders++;
                    var head = ChainHeadName(returned);
                    // Unresolvable heads (a local, a helper call chain) are skipped rather than
                    // guessed at; only a statically-known non-container root is reported.
                    if (head is null || head == "ItemContainer") continue;
                    offenders.Add($"{Where(path, returned)}: ItemsView view builder returns {head}(...) — " +
                                  "ItemsView requires an ItemContainer root, so the page throws at render.");
                }
            }
        }

        Assert.True(checkedBuilders > 0,
            "no ItemsView view-builder roots were inspected — the lint would pass vacuously.");
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    // ── Shapes are painted with Fill, not Background ─────────────────────────

    static readonly string[] ShapeFactories = ["Rectangle", "Ellipse", "Line", "Path2D"];

    [Fact]
    public void Shapes_DoNotUseBackground()
    {
        var offenders = new List<string>();
        var inspectedShapeChains = 0;

        foreach (var (path, root) in Pages())
        {
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax member) continue;

                var head = ChainHeadName(member.Expression);
                if (head is null || !ShapeFactories.Contains(head)) continue;
                inspectedShapeChains++;

                var modifier = member.Name.Identifier.Text;
                if (modifier is "Background" or "Foreground")
                {
                    offenders.Add($"{Where(path, invocation)}: .{modifier}(...) on {head}(...) has no effect — " +
                                  "the reconciler only applies it to Panel / Control / Border. Shapes paint with .Fill(...) / .Stroke(...).");
                }
            }
        }

        Assert.True(inspectedShapeChains > 0,
            "no shape fluent chains were inspected — the lint would pass vacuously.");
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    // ── ms-appx assets must exist AND be copied to the output folder ─────────

    static readonly Regex MsAppxLiteral = new(@"ms-appx:///(?<path>[^""'\s\\]+)", RegexOptions.Compiled);

    /// <summary>
    /// Directory prefixes / exact files the gallery csproj copies next to the executable.
    /// <c>ms-appx:///</c> resolves against that folder for an unpackaged app, so an asset
    /// that is not a Content item is invisible at runtime no matter where it sits in the repo.
    /// </summary>
    static IReadOnlyList<string> CopiedContentEntries()
    {
        var csproj = Path.Join(GalleryDir(), "ReactorGallery.csproj");
        var text = File.ReadAllText(csproj);
        var entries = Regex.Matches(text, @"<Content\s+Include=""(?<inc>[^""]+)""")
            .Select(m => m.Groups["inc"].Value.Replace('\\', '/'))
            .Select(inc => inc.EndsWith("/**", global::System.StringComparison.Ordinal)
                ? inc[..^3] + "/"
                : inc)
            .ToList();

        Assert.True(entries.Count > 0, $"no <Content Include=...> items found in {Rel(csproj)}");
        return entries;
    }

    [Fact]
    public void MsAppxAssets_ExistAndAreCopiedToOutput()
    {
        var content = CopiedContentEntries();
        var offenders = new List<string>();
        var inspectedAssets = 0;

        foreach (var (path, root) in Pages())
        {
            foreach (Match match in MsAppxLiteral.Matches(root.ToFullString()))
            {
                var assetPath = match.Groups["path"].Value;
                // Skip interpolated / composed URIs — only literal paths are checkable.
                if (assetPath.Contains('{') || assetPath.Contains(')')) continue;
                inspectedAssets++;

                if (!File.Exists(Path.Join(GalleryDir(), assetPath.Replace('/', Path.DirectorySeparatorChar))))
                {
                    offenders.Add($"{Rel(path)}: ms-appx:///{assetPath} does not exist under samples/ReactorGallery — " +
                                  "the Image renders blank with no error.");
                    continue;
                }

                var covered = content.Any(entry => entry.EndsWith("/", global::System.StringComparison.Ordinal)
                    ? assetPath.StartsWith(entry, global::System.StringComparison.OrdinalIgnoreCase)
                    : string.Equals(assetPath, entry, global::System.StringComparison.OrdinalIgnoreCase));

                if (!covered)
                {
                    offenders.Add($"{Rel(path)}: ms-appx:///{assetPath} exists in the repo but no " +
                                  "<Content Include=...> item copies it to the output folder, so it is missing at runtime.");
                }
            }
        }

        Assert.True(inspectedAssets > 0,
            "no ms-appx:/// asset literals were inspected — the lint would pass vacuously.");
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }
}

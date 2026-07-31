using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.Tooling;

/// <summary>
/// Source lint for the ReactorGallery sample pages. The gallery is the reference app, so a
/// card that silently renders nothing teaches the wrong pattern — and the four mistakes
/// guarded here are all statically detectable and all shipped at least once:
///
/// <list type="bullet">
/// <item>an <c>ItemsView</c> view builder returning a non-<c>ItemContainer</c> root, which
/// makes the whole page throw (<c>ItemsViewElement.GuardedViewBuilder</c>);</item>
/// <item><c>.Background(...)</c> on a shape, which the reconciler only applies to
/// Panel / Control / Border and therefore drops, rendering an invisible shape;</item>
/// <item>an <c>ms-appx:///</c> asset that either does not exist, is never copied to the
/// output folder, or is composed at runtime so it cannot be checked — all of which render a
/// blank image with no error of any kind;</item>
/// <item><c>new Uri(x)</c> on a runtime value, which throws <c>UriFormatException</c> out of
/// <c>Render()</c> the moment the value is half-typed and replaces the whole page with the
/// error boundary.</item>
/// </list>
///
/// Roslyn parses the page sources directly — no gallery build, no WinUI objects, so this
/// stays in the headless unit tier. Each fact also asserts the scan actually found the
/// construct it polices, so deleting the sample would fail the test rather than pass it
/// vacuously.
/// </summary>
public sealed class GallerySampleLintTests
{
    // Source loading lives in GallerySources so the snippet-agreement lint next door reads the
    // same pages through the same code path.
    static string GalleryDir() => GallerySources.GalleryDir();

    static IReadOnlyList<(string Path, SyntaxNode Root)> Pages() => GallerySources.Pages();

    static string Rel(string absolute) => GallerySources.Rel(absolute);

    static string Where(string path, SyntaxNode node) => GallerySources.Where(path, node);

    static string? InvokedName(InvocationExpressionSyntax invocation) => GallerySources.InvokedName(invocation);

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

    /// <summary>Parameter count of a lambda, for either the simple or parenthesized form.</summary>
    static int ParameterCount(AnonymousFunctionExpressionSyntax lambda) => lambda switch
    {
        SimpleLambdaExpressionSyntax => 1,
        ParenthesizedLambdaExpressionSyntax p => p.ParameterList.Parameters.Count,
        AnonymousMethodExpressionSyntax a => a.ParameterList?.Parameters.Count ?? 0,
        _ => -1,
    };

    /// <summary>
    /// Strip parentheses and casts from an argument so <c>(Func&lt;T,int,Element&gt;)((i, n) =&gt; ...)</c>
    /// is still recognised as the lambda it is. Without this the lint fails closed on legal code.
    /// </summary>
    static ExpressionSyntax UnwrapArgument(ExpressionSyntax expression) => expression switch
    {
        ParenthesizedExpressionSyntax paren => UnwrapArgument(paren.Expression),
        CastExpressionSyntax cast => UnwrapArgument(cast.Expression),
        _ => expression,
    };

    /// <summary>
    /// The <c>(item, index) =&gt; Element</c> view-builder lambdas of an <c>ItemsView(...)</c> call.
    /// Selected by parameter count rather than position: <c>ItemsViewElement&lt;T&gt;</c> has exactly one
    /// two-parameter delegate (<c>ViewBuilder</c>) — <c>KeySelector</c>, <c>OnItemInvoked</c> and
    /// <c>OnSelectionChanged</c> all take one — so the count is unambiguous, while picking "the last
    /// lambda" would grab the key selector whenever the builder is passed as a method group.
    /// </summary>
    static IReadOnlyList<AnonymousFunctionExpressionSyntax> ViewBuilders(InvocationExpressionSyntax invocation) =>
        invocation.ArgumentList.Arguments
            .Select(a => UnwrapArgument(a.Expression))
            .OfType<AnonymousFunctionExpressionSyntax>()
            .Where(l => ParameterCount(l) == 2)
            .ToList();

    /// <summary>
    /// The lambda or local function a statement actually belongs to. <c>DescendantNodes</c> walks the
    /// whole subtree, so without this a <c>return</c> inside a nested lambda or local helper declared
    /// in the builder body would be attributed to the builder itself and checked for an
    /// <c>ItemContainer</c> root it was never required to have.
    /// </summary>
    static SyntaxNode? OwningScope(SyntaxNode node) =>
        node.Ancestors().FirstOrDefault(a =>
            a is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);

    /// <summary>Every expression a lambda can hand back, flattening conditional branches.</summary>
    static IEnumerable<ExpressionSyntax> ReturnedExpressions(AnonymousFunctionExpressionSyntax lambda)
    {
        var roots = new List<ExpressionSyntax>();
        if (lambda.ExpressionBody is { } body)
            roots.Add(body);
        if (lambda.Block is { } block)
            roots.AddRange(block.DescendantNodes()
                .OfType<ReturnStatementSyntax>()
                .Where(r => ReferenceEquals(OwningScope(r), lambda))
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
            foreach (var invocation in root.DescendantNodes()
                         .OfType<InvocationExpressionSyntax>()
                         .Where(i => InvokedName(i) == "ItemsView"))
            {
                // The view builder is the (item, index) => Element lambda.
                var lambdas = ViewBuilders(invocation);

                if (lambdas.Count == 0)
                {
                    offenders.Add($"{Where(path, invocation)}: the ItemsView view builder is not a two-parameter " +
                                  "lambda, so this lint cannot verify its root. Pass it inline as " +
                                  "(item, index) => ItemContainer(...) rather than as a method group or a " +
                                  "parameterless delegate { } form.");
                    continue;
                }

                foreach (var builder in lambdas)
                foreach (var returned in ReturnedExpressions(builder))
                {
                    checkedBuilders++;
                    var head = ChainHeadName(returned);
                    if (head == "ItemContainer") continue;
                    offenders.Add(head is null
                        ? $"{Where(path, returned)}: could not statically verify the ItemsView view-builder root — " +
                          "return ItemContainer(...) directly from the builder so the requirement stays checkable."
                        : $"{Where(path, returned)}: ItemsView view builder returns {head}(...) — " +
                          "ItemsView requires an ItemContainer root, so the page throws at render.");
                }
            }
        }

        Assert.True(checkedBuilders > 0,
            "no ItemsView view-builder roots were inspected — the lint would pass vacuously.");
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    static InvocationExpressionSyntax ParseItemsViewCall(string call) =>
        CSharpSyntaxTree
            .ParseText($"class C {{ void M() {{ var x = {call}; }} }}",
                cancellationToken: TestContext.Current.CancellationToken)
            .GetRoot(TestContext.Current.CancellationToken)
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(i => InvokedName(i) == "ItemsView");

    /// <summary>
    /// The builder selection above decides whether the lint checks a call or fails it closed, so its
    /// own behaviour is pinned here against synthetic source. A cast- or parenthesis-wrapped lambda is
    /// legal C# that converts to <c>Func&lt;T,int,Element&gt;</c>; before <c>UnwrapArgument</c> existed
    /// those were seen as non-lambdas and the lint reported a bogus offender against correct code.
    /// </summary>
    [Theory]
    [InlineData("ItemsView(items, i => i.Key, (i, n) => ItemContainer(Text(i.Name)))", 1)]
    [InlineData("ItemsView(items, i => i.Key, ((i, n) => ItemContainer(Text(i.Name))))", 1)]
    [InlineData("ItemsView(items, i => i.Key, (Func<Item, int, Element>)((i, n) => ItemContainer(Text(i.Name))))", 1)]
    [InlineData("ItemsView(items, i => i.Key, BuildRow)", 0)]
    [InlineData("ItemsView(items, i => i.Key, i => ItemContainer(Text(i.Name)))", 0)]
    public void ViewBuilderSelection_FindsWrappedLambdas_AndIgnoresOneParameterArguments(string call, int expected) =>
        Assert.Equal(expected, ViewBuilders(ParseItemsViewCall(call)).Count);

    /// <summary>
    /// Returns are collected with <c>DescendantNodes</c>, which walks the entire subtree — so a
    /// <c>return</c> belonging to a nested lambda or a local helper declared inside the builder would
    /// otherwise be read as one of the builder's own return paths and demanded to be an
    /// <c>ItemContainer</c>. That is a false failure against legal code, so the scoping is pinned here.
    /// </summary>
    [Theory]
    [InlineData("ItemsView(items, i => i.Key, (i, n) => ItemContainer(Text(i.Name)))", "ItemContainer")]
    [InlineData("ItemsView(items, i => i.Key, (i, n) => { return ItemContainer(Text(i.Name)); })", "ItemContainer")]
    [InlineData("ItemsView(items, i => i.Key, (i, n) => { var f = () => { return Text(i.Name); }; return ItemContainer(f()); })", "ItemContainer")]
    [InlineData("ItemsView(items, i => i.Key, (i, n) => { Element H() { return Text(i.Name); } return ItemContainer(H()); })", "ItemContainer")]
    [InlineData("ItemsView(items, i => i.Key, (i, n) => n == 0 ? ItemContainer(Text(i.Name)) : ItemContainer(Icon()))", "ItemContainer,ItemContainer")]
    public void ReturnedExpressions_AreScopedToTheBuilder_NotNestedLambdasOrLocalFunctions(string call, string expectedHeads)
    {
        var builder = Assert.Single(ViewBuilders(ParseItemsViewCall(call)));
        var heads = ReturnedExpressions(builder).Select(ChainHeadName).ToArray();

        Assert.Equal(expectedHeads, string.Join(",", heads));
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

    /// <summary>
    /// An <c>ms-appx:///</c> reference and whatever literal text follows it. The alternation's
    /// second arm matches an <em>empty</em> path when the scheme is immediately followed by a
    /// quote — i.e. <c>"ms-appx:///" + relative</c>, where nothing after the scheme is literal.
    /// Without it that form matches nothing at all and escapes the lint entirely. Prose mentions
    /// in comments are followed by a space, so they still match neither arm.
    /// </summary>
    static readonly Regex MsAppxLiteral = new(@"ms-appx:///(?<path>[^""'\s\\]+|(?=[""']))", RegexOptions.Compiled);

    /// <summary>
    /// True when the captured text cannot be a whole asset path, so the lint must say so rather
    /// than go looking for a file. Each arm is a real composition form: an interpolation hole,
    /// a capture that ran past the closing quote, a trailing <c>/</c> left by
    /// <c>"ms-appx:///Assets/" + name</c>, and an empty capture from <c>"ms-appx:///" + path</c>.
    /// </summary>
    static bool IsComposedAssetPath(string assetPath) =>
        assetPath.Length == 0
        || assetPath.Contains('{')
        || assetPath.Contains(')')
        || assetPath.EndsWith('/');

    /// <summary>
    /// Directory prefixes / exact files the gallery csproj copies next to the executable.
    /// <c>ms-appx:///</c> resolves against that folder for an unpackaged app, so an asset
    /// that is not a Content item is invisible at runtime no matter where it sits in the repo.
    /// </summary>
    static IReadOnlyList<string> CopiedContentEntries()
    {
        var csproj = Path.Join(GalleryDir(), "ReactorGallery.csproj");
        var text = File.ReadAllText(csproj);
        // Match Include= anywhere in the tag, not just as the first attribute — otherwise a
        // perfectly ordinary `<Content Update="…" Include="…">` silently drops out and the
        // asset lint reports "not copied to output" for a file that is.
        var entries = Regex.Matches(text, @"<Content\b[^>]*?\bInclude\s*=\s*""(?<inc>[^""]+)""")
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
            foreach (var (assetPath, offset) in MsAppxLiteral.Matches(root.ToFullString())
                         .Select(m => (m.Groups["path"].Value, m.Index)))
            {
                inspectedAssets++;

                // A regex match has no syntax node, so map its offset back through the tree
                // to report file:line like the other two facts in this file do.
                var at = $"{Rel(path)}:{root.SyntaxTree.GetLineSpan(new TextSpan(offset, 1), TestContext.Current.CancellationToken).StartLinePosition.Line + 1}";

                // A composed / interpolated URI cannot be resolved statically, so it would
                // silently escape this lint — fail instead of quietly skipping it.
                if (IsComposedAssetPath(assetPath))
                {
                    offenders.Add($"{at}: ms-appx:///{assetPath} is composed at runtime, so this lint cannot " +
                                  "verify the asset ships. Use a literal ms-appx:/// path in gallery pages.");
                    continue;
                }

                if (!File.Exists(Path.Join(GalleryDir(), assetPath.Replace('/', Path.DirectorySeparatorChar))))
                {
                    offenders.Add($"{at}: ms-appx:///{assetPath} does not exist under samples/ReactorGallery — " +
                                  "the Image renders blank with no error.");
                    continue;
                }

                var covered = content.Any(entry => entry.EndsWith("/", global::System.StringComparison.Ordinal)
                    ? assetPath.StartsWith(entry, global::System.StringComparison.OrdinalIgnoreCase)
                    : string.Equals(assetPath, entry, global::System.StringComparison.OrdinalIgnoreCase));

                if (!covered)
                {
                    offenders.Add($"{at}: ms-appx:///{assetPath} exists in the repo but no " +
                                  "<Content Include=...> item copies it to the output folder, so it is missing at runtime.");
                }
            }
        }

        Assert.True(inspectedAssets > 0,
            "no ms-appx:/// asset literals were inspected — the lint would pass vacuously.");
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    /// <summary>
    /// The regex and the composed-path classifier together decide whether a reference is checked,
    /// reported as unverifiable, or skipped entirely — so each composition form is pinned against
    /// a source line. <c>expected</c> is <c>null</c> when nothing should match at all, which is the
    /// correct answer only for a prose mention of the scheme in a comment.
    /// </summary>
    [Theory]
    [InlineData(@"Image(""ms-appx:///Assets/SampleImages/Landscape.png"")", "Assets/SampleImages/Landscape.png", false)]
    [InlineData(@"Image(""""ms-appx:///Assets/SampleImages/Landscape.png"""")", "Assets/SampleImages/Landscape.png", false)]
    [InlineData(@"Image($""ms-appx:///Assets/{name}.png"")", "Assets/{name}.png", true)]
    [InlineData(@"Image(""ms-appx:///Assets/"" + fileName)", "Assets/", true)]
    [InlineData(@"Image(""ms-appx:///"" + relative)", "", true)]
    [InlineData("// what ms-appx:/// resolves against, next to the executable", null, false)]
    public void MsAppxDetection_ClassifiesEveryCompositionForm(string source, string? expected, bool composed)
    {
        var match = MsAppxLiteral.Match(source);

        if (expected is null)
        {
            Assert.False(match.Success, $"prose mention should not match: {source}");
            return;
        }

        Assert.True(match.Success, $"no ms-appx match in: {source}");
        Assert.Equal(expected, match.Groups["path"].Value);
        Assert.Equal(composed, IsComposedAssetPath(match.Groups["path"].Value));
    }

    // ── new Uri(...) takes compile-time constants, never a runtime value ─────

    /// <summary>
    /// <c>new Uri(text)</c> throws <c>UriFormatException</c> on anything malformed, and a gallery
    /// page evaluates its element tree inside <c>Render()</c> — so the throw lands in
    /// <c>ErrorFallback</c> and the reader's whole page becomes a
    /// <c>⚠ Render error: UriFormatException…</c> overlay. Half-typed text in a bound
    /// <c>TextBox</c> is enough to trigger it (issue #982). A string that cannot vary at runtime
    /// cannot do that, hence the rule below.
    /// </summary>
    static bool IsUriCreation(ObjectCreationExpressionSyntax creation) => creation.Type switch
    {
        IdentifierNameSyntax id => id.Identifier.Text == "Uri",
        // System.Uri and global::System.Uri both land here.
        QualifiedNameSyntax qualified => qualified.Right.Identifier.Text == "Uri",
        _ => false,
    };

    /// <summary>Names in <paramref name="root"/> that a <c>new Uri(...)</c> argument may safely reference.</summary>
    /// <remarks>
    /// Two tiers, and the second is why this is sound rather than assumed:
    /// <list type="bullet">
    /// <item><c>const</c> fields and locals initialized from a string literal — compile-time
    /// constants, so a malformed one fails on first render and never gets committed;</item>
    /// <item><c>static readonly</c> fields whose own initializer is a string literal or a
    /// <c>new Uri(...)</c> over accepted arguments. That covers <c>new Uri(BaseUri, "page.html")</c>
    /// without hand-waving: the referenced field's construction is checked by this same rule, so
    /// accepting the reference adds no unchecked value. A <c>static readonly</c> initialized from a
    /// runtime call is <em>not</em> accepted.</item>
    /// </list>
    /// </remarks>
    static HashSet<string> ConstantNames(SyntaxNode root)
    {
        var names = new HashSet<string>(global::System.StringComparer.Ordinal);

        bool Accepted(ExpressionSyntax expression) =>
            (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            || (expression is IdentifierNameSyntax identifier && names.Contains(identifier.Identifier.Text));

        foreach (var declarator in root.DescendantNodes()
                     .OfType<VariableDeclaratorSyntax>()
                     .Where(d => d.Parent?.Parent is FieldDeclarationSyntax { Modifiers: var m } && m.Any(SyntaxKind.ConstKeyword)
                                 || d.Parent?.Parent is LocalDeclarationStatementSyntax { Modifiers: var l } && l.Any(SyntaxKind.ConstKeyword)))
        {
            if (declarator.Initializer?.Value is { } value && Accepted(value))
                names.Add(declarator.Identifier.Text);
        }

        foreach (var declarator in root.DescendantNodes()
                     .OfType<VariableDeclaratorSyntax>()
                     .Where(d => d.Parent?.Parent is FieldDeclarationSyntax { Modifiers: var m }
                                 && m.Any(SyntaxKind.StaticKeyword) && m.Any(SyntaxKind.ReadOnlyKeyword)))
        {
            if (declarator.Initializer?.Value is not { } value) continue;

            var ok = Accepted(value)
                     || (value is ObjectCreationExpressionSyntax creation
                         && IsUriCreation(creation)
                         && (creation.ArgumentList?.Arguments ?? default).All(a => Accepted(a.Expression)));

            if (ok) names.Add(declarator.Identifier.Text);
        }

        return names;
    }

    /// <summary>
    /// The <c>(string, UriKind)</c> and <c>(string, bool)</c> overloads pass a mode, not a URL, so
    /// those arguments carry no risk and are not held to the constant rule.
    /// </summary>
    static bool IsNotAUrlArgument(ExpressionSyntax expression) =>
        expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "UriKind" } }
        || expression.IsKind(SyntaxKind.TrueLiteralExpression)
        || expression.IsKind(SyntaxKind.FalseLiteralExpression);

    /// <summary>
    /// Every <c>new Uri(...)</c> argument in <paramref name="root"/> that is not a compile-time
    /// constant, as <c>"line: expression"</c>. Separate from the tree-wide fact below so the rule
    /// can be driven over synthetic source — a scan of a clean tree cannot tell a working detector
    /// from one that reports nothing.
    /// </summary>
    static IReadOnlyList<(ObjectCreationExpressionSyntax Site, ExpressionSyntax Argument)> RuntimeUriArguments(SyntaxNode root)
    {
        var names = ConstantNames(root);
        var offenders = new List<(ObjectCreationExpressionSyntax, ExpressionSyntax)>();

        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Where(IsUriCreation))
        {
            foreach (var argument in creation.ArgumentList?.Arguments ?? default)
            {
                var expression = argument.Expression;
                if (IsNotAUrlArgument(expression)) continue;
                if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)) continue;
                if (expression is IdentifierNameSyntax identifier && names.Contains(identifier.Identifier.Text)) continue;

                offenders.Add((creation, expression));
                break;
            }
        }

        return offenders;
    }

    static int UriCreationCount(SyntaxNode root) =>
        root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Count(IsUriCreation);

    [Fact]
    public void NewUri_TakesOnlyCompileTimeConstants()
    {
        var offenders = new List<string>();
        var inspectedConstructions = 0;

        foreach (var (path, root) in Pages())
        {
            inspectedConstructions += UriCreationCount(root);

            foreach (var (site, argument) in RuntimeUriArguments(root))
            {
                offenders.Add($"{Where(path, site)}: new Uri({argument}) is built from a runtime value — " +
                              "it throws UriFormatException on malformed input, and a throw out of Render() " +
                              "replaces the whole page with the error boundary. Parse with Uri.TryCreate and " +
                              "render the last value that parsed.");
            }
        }

        // Floor rather than > 0: the gallery has four `new Uri(...)` sites, so a loader that
        // silently found one page — or a type check that stopped recognising them — fails here
        // instead of reporting a clean tree it never looked at.
        Assert.True(inspectedConstructions >= 3,
            $"only {inspectedConstructions} `new Uri(...)` sites were inspected across the gallery — the lint would pass near-vacuously.");
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    /// <summary>
    /// The accept-list decides everything, so each of its arms is pinned against synthetic source:
    /// the tree-wide fact above is green for a detector that works and for one that returns nothing,
    /// and only these rows tell them apart. The reported rows are the shapes issue #982 shipped —
    /// state read straight out of <c>UseState</c>, a method call on it, and an interpolation.
    /// </summary>
    [Theory]
    // Accepted: nothing here can vary at runtime.
    [InlineData(@"class P { void M() { var u = new Uri(""https://example.com""); } }", 0)]
    [InlineData(@"class P { const string U = ""https://example.com""; void M() { var u = new Uri(U); } }", 0)]
    [InlineData(@"class P { void M() { const string U = ""https://example.com""; var u = new Uri(U); } }", 0)]
    [InlineData(@"class P { static readonly Uri B = new Uri(""https://example.com""); void M() { var u = new Uri(B, ""page.html""); } }", 0)]
    [InlineData(@"class P { void M() { var u = new Uri(""https://example.com"", UriKind.Absolute); } }", 0)]
    // Reported: every one of these is a value the reader can make malformed.
    [InlineData(@"class P { Element R() { var (t, s) = UseState(""""); return WebView2(new Uri(t)); } }", 1)]
    [InlineData(@"class P { Element R(string t) { return WebView2(new Uri(t.Trim())); } }", 1)]
    [InlineData(@"class P { Element R(string t) { return WebView2(new Uri($""https://{t}"")); } }", 1)]
    [InlineData(@"class P { Element R(string t) { return WebView2(new System.Uri(t)); } }", 1)]
    // A `static readonly` is only trusted when its own initializer is, and a property is not a field.
    [InlineData(@"class P { static readonly string U = Fetch(); void M() { var u = new Uri(U); } }", 1)]
    [InlineData(@"class P { static string U => Fetch(); void M() { var u = new Uri(U); } }", 1)]
    public void UriConstantRule_AcceptsCompileTimeValues_AndReportsRuntimeOnes(string source, int expected)
    {
        var root = CSharpSyntaxTree
            .ParseText(source, cancellationToken: TestContext.Current.CancellationToken)
            .GetRoot(TestContext.Current.CancellationToken);

        // Guards the rows themselves: a typo that stopped parsing as a Uri construction would
        // otherwise satisfy every `expected: 0` row for the wrong reason.
        Assert.True(UriCreationCount(root) > 0, $"no `new Uri(...)` in the row source: {source}");
        Assert.Equal(expected, RuntimeUriArguments(root).Count);
    }
}

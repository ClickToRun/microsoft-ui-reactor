using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.Tooling;

/// <summary>
/// Source lint for the agreement between a gallery <c>SampleCard</c>'s snippet and the live card
/// beside it. Both halves are arguments to one invocation —
/// <c>SampleCard(title, sample, sourceCode, options)</c> — so pairing them needs no heuristics.
///
/// <para>The rule is one sentence: <b>every lowerCamelCase name a snippet uses must exist in the
/// code beside it.</b> The direction is snippet → live and never the reverse. A snippet is allowed
/// to omit — that is what makes it readable — but a name it invents is a name the reader copies and
/// cannot compile.</para>
///
/// <para>Four things make that rule precise enough to gate on, where a plain text scan measured
/// ~5% precision:</para>
/// <list type="number">
/// <item>matching is case-sensitive and aware of member access, so a snippet's <c>price</c> does not
/// resolve against a live <c>p.Price</c>, and its <c>columns</c> does not resolve against
/// <c>Columns()</c>;</item>
/// <item>names the snippet itself binds are excluded, so the TabView snippets — whose <c>tabs</c> /
/// <c>idx</c> deliberately stand in for two differently-named cards — are not reported;</item>
/// <item>"beside it" is scoped per card: the page's shared context plus this card's own sample, so a
/// local bound inside a <em>sibling</em> card's lambda cannot legitimise a stale name here;</item>
/// <item>the scan is restricted to lowerCamelCase names, which is where this defect class lives.
/// Resolving PascalCase would need a type/API name universe that is either hand-maintained and goes
/// stale or broad enough to resolve anything.</item>
/// </list>
///
/// <para><b>What this deliberately cannot catch, and why that is the right answer.</b> Four of the
/// twelve instances #941 fixed are the snippet <em>omitting</em> something — a <c>.Padding(16)</c>,
/// a <c>UseMemo</c> seed, two of five columns. Catching those needs structural comparison of matched
/// sub-expressions, and the "Basic ItemsView" card proves that cannot coexist with a clean tree: its
/// snippet deliberately drops a whole <c>TextBlock</c>, a <c>.Foreground(...)</c>, <c>.Padding(12)</c>,
/// <c>.Background(...)</c>, <c>.CornerRadius(...)</c> and <c>.Margin(4)</c> from a chain it otherwise
/// reproduces exactly. Any rule strong enough to flag the missing <c>.Padding(16)</c> fires on that
/// card. The same argument rules out comparing named-argument values, which the DataGrid card would
/// trip by expanding <c>columns: Columns()</c> into a literal array. So this file checks invention,
/// not omission, and says so rather than pretending the gap is a bug.</para>
///
/// <para>Roslyn parses page sources as text — no gallery build, no WinUI objects — so this stays in
/// the headless unit tier. Snippets are fragments with deliberately missing semicolons and
/// <c>...</c> elisions, so parse diagnostics are ignored and names are harvested best-effort:
/// report, never fail closed.</para>
/// </summary>
public sealed class GallerySnippetAgreementTests
{
    // ── what a finding is ────────────────────────────────────────────────────

    internal enum FindingKind
    {
        /// <summary>The snippet uses a name that is neither declared in the snippet nor present in the page.</summary>
        Invented,

        /// <summary>A snippet deconstruction names some live identifiers and some that do not exist.</summary>
        HalfSynced,
    }

    internal readonly record struct Finding(string Card, string Name, string SnippetLine, int PageLine, FindingKind Kind)
    {
        public string Describe()
        {
            var nl = global::System.Environment.NewLine;

            return Kind == FindingKind.Invented
                ? $"the \"{Card}\" snippet uses `{Name}`, which does not exist in the code beside it and is not " +
                  $"declared in the snippet:{nl}      {SnippetLine}" +
                  $"{nl}    A snippet may omit; it may not invent. Point it at the name the card " +
                  "actually uses, or declare it in the snippet."
                : $"the \"{Card}\" snippet destructures `{Name}`, which the card does not have, alongside names that it " +
                  $"does:{nl}      {SnippetLine}" +
                  $"{nl}    A snippet may rename state wholesale for readability, but a half-renamed " +
                  "deconstruction means the card changed and the snippet did not.";
        }
    }

    internal sealed record PageScan(
        IReadOnlyList<Finding> Findings,
        int Cards,
        int NamesChecked,
        int SnippetsNotLiteral);

    // ── locating the two halves of a card ────────────────────────────────────

    /// <summary>
    /// The argument carrying the <c>name:</c> label, wherever in the list it sits. Both callers below
    /// prefer the label over position, because a gallery author may pass any of these by name.
    /// </summary>
    static ExpressionSyntax? NamedArgument(SeparatedSyntaxList<ArgumentSyntax> args, string name) =>
        args.Where(argument => argument.NameColon?.Name.Identifier.Text == name)
            .Select(argument => argument.Expression)
            .FirstOrDefault();

    /// <summary>
    /// The snippet argument: named <c>sourceCode:</c> wherever it sits, otherwise the third
    /// positional argument of <c>SampleCard(title, sample, sourceCode, options)</c>.
    /// </summary>
    static ExpressionSyntax? SnippetArgument(InvocationExpressionSyntax invocation)
    {
        var args = invocation.ArgumentList.Arguments;

        return NamedArgument(args, "sourceCode")
               ?? (args.Count >= 3 && args[2].NameColon is null ? args[2].Expression : null);
    }

    /// <summary>Verbatim and raw string literals both land here; <c>ValueText</c> already un-escapes and de-indents.</summary>
    static string? StringLiteralValue(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)
            ? literal.Token.ValueText
            : null;

    static string CardTitle(InvocationExpressionSyntax invocation)
    {
        var args = invocation.ArgumentList.Arguments;

        // A `title:` label that is not a string literal stays "(untitled)" rather than falling through
        // to position — the author named the argument, so position says nothing about it.
        if (NamedArgument(args, "title") is { } named)
            return StringLiteralValue(named) ?? "(untitled)";

        return (args.Count > 0 && args[0].NameColon is null ? StringLiteralValue(args[0].Expression) : null)
               ?? "(untitled)";
    }

    // ── parsing a snippet fragment ───────────────────────────────────────────

    // A snippet is a fragment, so it is wrapped in a method body: that makes a top-level
    // `static int Helper(...) => ...` a local function and a bare `Foo(...)` an expression
    // statement. The prologue is one line, which is what maps a node back to a snippet line.
    const string SnippetPrologue = "class __Snippet { void __Card() {\n";
    const int SnippetPrologueLines = 1;

    static SyntaxTree ParseSnippet(string snippet) =>
        CSharpSyntaxTree.ParseText(SnippetPrologue + snippet + "\n} }");

    // ── which identifier occurrences are references ──────────────────────────

    /// <summary>
    /// The naming convention this lint is scoped to: locals, state and setters. Types, factories,
    /// modifiers and initializer keys are PascalCase and are out of scope by design.
    /// </summary>
    internal static bool IsLocalStyleName(string name) =>
        name.Length > 0
        && (name[0] == '_' || (name[0] >= 'a' && name[0] <= 'z'))
        && name.Any(c => c != '_');

    /// <summary>
    /// The one identifier that parses as <see cref="IdentifierNameSyntax"/> but can never be a name
    /// in scope. Every other contextual keyword is excluded structurally instead — <c>var</c> and
    /// <c>dynamic</c> by <see cref="IsTypeSlot"/>, and <c>value</c> not at all, because a snippet is
    /// a statement fragment where <c>value</c> is an ordinary local. A blanket name list would
    /// silence a snippet's invented <c>value</c>, which is exactly the defect this rule exists for.
    /// </summary>
    const string NameOf = "nameof";

    /// <summary>
    /// True when the identifier names a type rather than a value. Walks out through any enclosing
    /// type syntax first so <c>List&lt;foo&gt;</c>, <c>foo[]</c> and <c>foo?</c> are classified by
    /// the slot the whole type sits in, not by the leaf.
    /// </summary>
    static bool IsTypeSlot(IdentifierNameSyntax identifier)
    {
        SyntaxNode node = identifier;
        while (node.Parent is TypeSyntax
               or TypeArgumentListSyntax
               or TupleElementSyntax
               or ArrayRankSpecifierSyntax)
        {
            node = node.Parent;
        }

        return node.Parent switch
        {
            VariableDeclarationSyntax v => v.Type == node,
            ForEachStatementSyntax f => f.Type == node,
            DeclarationExpressionSyntax d => d.Type == node,
            DeclarationPatternSyntax d => d.Type == node,
            RecursivePatternSyntax r => r.Type == node,
            TypePatternSyntax => true,
            ObjectCreationExpressionSyntax o => o.Type == node,
            ArrayCreationExpressionSyntax => true,
            ParameterSyntax p => p.Type == node,
            CastExpressionSyntax c => c.Type == node,
            TypeOfExpressionSyntax t => t.Type == node,
            DefaultExpressionSyntax d => d.Type == node,
            CatchDeclarationSyntax c => c.Type == node,
            MethodDeclarationSyntax m => m.ReturnType == node,
            LocalFunctionStatementSyntax l => l.ReturnType == node,
            PropertyDeclarationSyntax p => p.Type == node,
            BaseTypeSyntax => true,
            TypeConstraintSyntax => true,
            UsingDirectiveSyntax => true,
            AttributeSyntax => true,
            BinaryExpressionSyntax b => b.IsKind(SyntaxKind.AsExpression) && b.Right == node,
            _ => false,
        };
    }

    /// <summary>
    /// Every identifier that reads a value. Excludes the right-hand side of member access and member
    /// binding — <c>p.Price</c> references <c>p</c>, not <c>Price</c> — named-argument labels, object
    /// and <c>with</c> initializer keys, and type positions.
    /// </summary>
    internal static IEnumerable<IdentifierNameSyntax> ReferenceNames(SyntaxNode root) =>
        root.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Where(id => !id.Identifier.IsMissing && !IsTypeSlot(id) && IsValueReference(id));

    static bool IsValueReference(IdentifierNameSyntax identifier) => identifier.Parent switch
    {
        MemberAccessExpressionSyntax member => member.Name != identifier,
        MemberBindingExpressionSyntax binding => binding.Name != identifier,
        QualifiedNameSyntax qualified => qualified.Right != identifier,
        AliasQualifiedNameSyntax alias => alias.Name != identifier,

        // `Foo(width: 60)` — the label names a parameter, not a value in scope. Not crediting these
        // on the live side is what lets a snippet's `columns: columns` be caught beside a live
        // `columns: Columns()`.
        NameColonSyntax => false,
        NameEqualsSyntax => false,

        // `X(...) with { SelectedIndex = idx }` — the key names a property, the value is checked.
        AssignmentExpressionSyntax assignment =>
            assignment.Left != identifier
            || assignment.Parent is not InitializerExpressionSyntax initializer
            || !(initializer.IsKind(SyntaxKind.ObjectInitializerExpression)
                 || initializer.IsKind(SyntaxKind.WithInitializerExpression)),

        // `nameof(X)` is an operator, not a call, so it is excluded by where it sits rather than by
        // its spelling — a lowercase local function call is still checked.
        InvocationExpressionSyntax call =>
            call.Expression != identifier || identifier.Identifier.Text != NameOf,

        _ => true,
    };

    /// <summary>
    /// Every name a tree binds. Collected tree-wide and applied scope-insensitively: a snippet that
    /// declares a name anywhere may use it anywhere, which is deliberately permissive so the lint
    /// errs towards silence.
    /// </summary>
    internal static IEnumerable<string> DeclaredNames(SyntaxNode root) =>
        root.DescendantNodesAndSelf().SelectMany(DeclaredBy);

    /// <summary>The names a single node binds, ignoring its descendants.</summary>
    static IEnumerable<string> DeclaredBy(SyntaxNode node) =>
        (node switch
        {
            VariableDeclaratorSyntax v => [v.Identifier],
            SingleVariableDesignationSyntax s => [s.Identifier],
            ParameterSyntax p => [p.Identifier],
            LocalFunctionStatementSyntax l => [l.Identifier],
            MethodDeclarationSyntax m => [m.Identifier],
            PropertyDeclarationSyntax p => [p.Identifier],
            EventDeclarationSyntax e => [e.Identifier],
            ForEachStatementSyntax f => [f.Identifier],
            CatchDeclarationSyntax c => [c.Identifier],
            FromClauseSyntax f => [f.Identifier],
            LetClauseSyntax l => [l.Identifier],
            JoinClauseSyntax j => [j.Identifier],
            JoinIntoClauseSyntax j => [j.Identifier],
            QueryContinuationSyntax q => [q.Identifier],
            TupleElementSyntax t => [t.Identifier],
            TypeDeclarationSyntax t => [t.Identifier],
            EnumMemberDeclarationSyntax e => [e.Identifier],
            LabeledStatementSyntax l => [l.Identifier],
            _ => global::System.Array.Empty<SyntaxToken>(),
        })
        .Select(token => token.Text)
        .Where(text => text.Length > 0);

    /// <summary>
    /// The names one card's snippet may resolve against: everything the page declares or references
    /// <em>outside</em> any card, plus everything inside this card's own invocation. That is what
    /// "the code beside it" means — the page's shared context and the card's own sample.
    /// </summary>
    /// <remarks>
    /// Scoping this per card rather than page-wide matters: a local bound inside a sibling card's
    /// lambda is not reachable from here, and crediting it would let one card's name legitimise
    /// another card's stale snippet. It is still deliberately coarse within those two regions — a
    /// name bound anywhere in them counts — so the rule errs towards silence.
    /// </remarks>
    static HashSet<string> LiveNamesFor(SyntaxNode pageRoot, InvocationExpressionSyntax card, IReadOnlyList<InvocationExpressionSyntax> allCards)
    {
        var names = new HashSet<string>(global::System.StringComparer.Ordinal);

        bool Reachable(SyntaxNode node) =>
            !allCards.Any(other => other != card && other.Span.Contains(node.Span));

        foreach (var node in pageRoot.DescendantNodesAndSelf().Where(Reachable))
        {
            foreach (var declared in DeclaredBy(node))
                names.Add(declared);

            if (node is IdentifierNameSyntax id && !id.Identifier.IsMissing && !IsTypeSlot(id) && IsValueReference(id))
                names.Add(id.Identifier.Text);
        }

        return names;
    }

    /// <summary>Every name anywhere in the page. Retained for the tests that pin the scope rules.</summary>
    internal static HashSet<string> LiveNames(SyntaxNode pageRoot)
    {
        var names = new HashSet<string>(DeclaredNames(pageRoot), global::System.StringComparer.Ordinal);
        foreach (var reference in ReferenceNames(pageRoot))
            names.Add(reference.Identifier.Text);
        return names;
    }

    // ── the scan ─────────────────────────────────────────────────────────────

    internal static PageScan ScanSource(string pageSource) =>
        ScanPage(CSharpSyntaxTree.ParseText(pageSource).GetRoot());

    internal static PageScan ScanPage(SyntaxNode pageRoot)
    {
        var findings = new List<Finding>();
        var cards = 0;
        var namesChecked = 0;
        var notLiteral = 0;

        var allCards = pageRoot.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => GallerySources.InvokedName(i) == "SampleCard")
            .ToList();

        foreach (var invocation in allCards)
        {
            if (SnippetArgument(invocation) is not { } snippetArgument) continue;

            cards++;

            // A couple of guidance pages build their snippet with string.Join over the same data the
            // card renders. There is no literal to read, and the card cannot drift from itself, so
            // it is counted and skipped rather than reported.
            if (StringLiteralValue(snippetArgument) is not { } snippet)
            {
                notLiteral++;
                continue;
            }

            var title = CardTitle(invocation);
            var pageLine = snippetArgument.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var lines = snippet.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            var live = LiveNamesFor(pageRoot, invocation, allCards);
            var tree = ParseSnippet(snippet);
            var root = tree.GetRoot();
            var bound = new HashSet<string>(DeclaredNames(root), global::System.StringComparer.Ordinal);

            string LineOf(SyntaxNode node)
            {
                var index = tree.GetLineSpan(node.Span).StartLinePosition.Line - SnippetPrologueLines;
                return index >= 0 && index < lines.Length ? lines[index].Trim() : snippet.Trim();
            }

            var reported = new HashSet<string>(global::System.StringComparer.Ordinal);

            foreach (var reference in ReferenceNames(root))
            {
                var name = reference.Identifier.Text;
                if (!IsLocalStyleName(name)) continue;

                namesChecked++;

                if (bound.Contains(name) || live.Contains(name)) continue;
                if (!reported.Add(name)) continue;

                findings.Add(new Finding(title, name, LineOf(reference), pageLine, FindingKind.Invented));
            }

            foreach (var finding in HalfSyncedDeconstructions(root, live, title, pageLine, LineOf))
                if (reported.Add(finding.Name))
                    findings.Add(finding);
        }

        return new PageScan(findings, cards, namesChecked, notLiteral);
    }

    /// <summary>
    /// A snippet may rename a card's state wholesale — the TabView snippets call two differently
    /// named cards' state <c>tabs</c> / <c>idx</c> so one snippet can stand for both, and every name
    /// they bind is theirs. That permissiveness is necessary, and it opens exactly one hole: a
    /// deconstruction that tracks the card only <em>halfway</em>. When <c>var (path, setPath)</c>
    /// sits beside a card that has <c>path</c> and no <c>setPath</c>, nothing was renamed — the card
    /// changed and the snippet did not — but the binder credits both names and says nothing.
    /// </summary>
    /// <remarks>
    /// This rule is the one part of the file with no shipped defect behind it, and that is
    /// deliberate. <c>c5d4bbd2</c> is the near miss: it moved the Basic BreadcrumbBar card from
    /// <c>var (path, setPath) = UseState(...)</c> to <c>var path = UseMemo(...)</c>. It changed the
    /// snippet in the same commit, so the half-synced state never shipped — but had it changed only
    /// the live half, this is the shape it would have left, and nothing else here would see it.
    /// </remarks>
    static IEnumerable<Finding> HalfSyncedDeconstructions(
        SyntaxNode snippetRoot,
        HashSet<string> live,
        string title,
        int pageLine,
        global::System.Func<SyntaxNode, string> lineOf)
    {
        foreach (var designation in snippetRoot.DescendantNodes().OfType<ParenthesizedVariableDesignationSyntax>())
        {
            var names = designation.Variables
                .OfType<SingleVariableDesignationSyntax>()
                .Select(v => v.Identifier.Text)
                .Where(IsLocalStyleName)
                .ToList();

            if (names.Count < 2) continue;

            var present = names.Count(live.Contains);
            if (present == 0 || present == names.Count) continue;

            foreach (var missing in names.Where(n => !live.Contains(n)))
                yield return new Finding(title, missing, lineOf(designation), pageLine, FindingKind.HalfSynced);
        }
    }

    // ── the gate ─────────────────────────────────────────────────────────────

    [Fact]
    public void SnippetNames_ExistInTheCodeBesideThem()
    {
        var offenders = new List<string>();
        var cards = 0;
        var namesChecked = 0;
        var notLiteral = 0;

        foreach (var (path, root) in GallerySources.Pages())
        {
            var scan = ScanPage(root);
            cards += scan.Cards;
            namesChecked += scan.NamesChecked;
            notLiteral += scan.SnippetsNotLiteral;

            offenders.AddRange(scan.Findings.Select(f =>
                $"{GallerySources.Rel(path)}:{f.PageLine}: {f.Describe()}"));
        }

        Assert.True(cards > 0, "no SampleCard invocations were inspected — the lint would pass vacuously.");
        Assert.True(namesChecked > 0, "no snippet names were resolved — the lint would pass vacuously.");
        Assert.True(offenders.Count == 0,
            $"{offenders.Count} snippet name(s) disagree with the card beside them " +
            $"({cards} cards, {namesChecked} names checked, {notLiteral} snippet(s) not a literal and skipped):" +
            global::System.Environment.NewLine +
            string.Join(global::System.Environment.NewLine, offenders));
    }

    // ── the rule, pinned against real defects and against itself ─────────────

    /// <summary>
    /// Assembles a one-card page around real live code and a real snippet. The snippet is emitted
    /// as a verbatim literal so a fixture can be pasted from a gallery page unchanged.
    /// </summary>
    internal static string Page(string live, string sample, string snippet)
    {
        var literal = "@\"" + snippet.Replace("\"", "\"\"") + "\"";

        return $$"""
            namespace Gallery;

            class __Page
            {
                Element Render()
                {
            {{live}}
                    return SampleCard("Card",
            {{sample}},
                        sourceCode: {{literal}});
                }
            }
            """;
    }

    /// <summary>
    /// Every name the lint reports for a single synthetic card. Asserts the card was actually found,
    /// so "reported nothing" can never mean "never looked".
    /// </summary>
    static IReadOnlyList<Finding> Scan(string live, string sample, string snippet)
    {
        var scan = ScanSource(Page(live, sample, snippet));
        Assert.Equal(1, scan.Cards);
        return scan.Findings;
    }

    static string[] Reported(string live, string sample, string snippet) =>
        Scan(live, sample, snippet).Select(f => f.Name).OrderBy(n => n, global::System.StringComparer.Ordinal).ToArray();

    /// <summary>
    /// <see cref="Reported"/>, asserted as one labelled line. Rows in these theories carry walls of
    /// snippet text, so a bare name-list mismatch is hard to trace back to the case that broke;
    /// folding the row's label into the compared value makes the failure say which one it was.
    /// </summary>
    static void AssertReported(string label, string live, string sample, string snippet, string[] expected) =>
        Assert.Equal(
            $"{label}: [{string.Join(", ", expected)}]",
            $"{label}: [{string.Join(", ", Reported(live, sample, snippet))}]");

    /// <summary>
    /// A name no card declares, appended to a snippet so the rule always has something to say about
    /// it. Rows that expect silence would otherwise pass just as well against a rule that reports
    /// nothing at all; with the probe in the snippet, silence is a failure.
    /// </summary>
    const string Probe = "probeSentinel";

    /// <summary>
    /// <see cref="AssertReported"/> with the probe appended: the result must be exactly the case's
    /// expectation plus the probe. That pins "expected nothing" cases to a rule that is still alive
    /// <em>and</em> to a snippet the parser actually recovered far enough to read.
    /// </summary>
    static void AssertReportedWithProbe(string label, string live, string sample, string snippet, string[] expected)
    {
        var withProbe = expected.Concat([Probe]).OrderBy(n => n, global::System.StringComparer.Ordinal).ToArray();
        AssertReported($"{label} + probe", live, sample, snippet + ";\nTextBlock(" + Probe + ");", withProbe);
    }

    // Each case below is a card that actually shipped: the live half is the code that sat in the
    // gallery, the snippet half is the text beside it, and `expected` is the name whose absence the
    // reader would have discovered by pasting the snippet. Deleting the rule fails all of them.

    public static TheoryData<string, string, string, string, string[]> KnownBadCards() => new()
    {
        // 2d8ff37e — "Basic TabView". The snippet declares its own `tabs`, which is legal (it stands
        // in for `basicTabs`), but `idx` / `setIdx` are declared nowhere and named nothing live.
        {
            "TabView Basic (2d8ff37e)",
            """
                    var (basicTabs, setBasicTabs) = UseState(UseMemo(() => new[] { "Home", "Document", "Settings" }));
                    var (basicIdx, setBasicIdx) = UseState(0);
            """,
            """
                        (TabView(basicTabs
                            .Select(t => Tab(t, TextBlock($"{t} content").Padding(16)))
                            .ToArray()) with
                        {
                            SelectedIndex = basicIdx,
                            OnSelectedIndexChanged = i => setBasicIdx(i),
                            OnTabCloseRequested = i =>
                            {
                                var remaining = basicTabs.Where((_, n) => n != i).ToArray();
                                setBasicTabs(remaining);
                                setBasicIdx(SelectionAfterClose(basicIdx, i, remaining.Length));
                            },
                        }).Height(200)
            """,
            """
            var (tabs, setTabs) = UseState(new[] { "Home", "Document", "Settings" });

            TabView(tabs.Select(t => Tab(t, TextBlock($"{t} content"))).ToArray()) with
            {
                SelectedIndex = idx,
                OnSelectedIndexChanged = i => setIdx(i),
                // The per-tab ✕ only raises TabCloseRequested — the app removes the tab.
                OnTabCloseRequested = i => setTabs(tabs.Where((_, n) => n != i).ToArray()),
            }
            """,
            new[] { "idx", "setIdx" }
        },

        // 2d8ff37e — "Dynamic TabView". Same shape, plus a `nextId` the card calls `nextTabId`; the
        // snippet's Add-Tab button would have produced the same title forever.
        {
            "TabView Dynamic (2d8ff37e)",
            """
                    var (dynamicTabs, setDynamicTabs) = UseState(UseMemo(() => new[] { "Tab 1", "Tab 2", "Tab 3" }));
                    var (dynamicIdx, setDynamicIdx) = UseState(0);
                    var (nextTabId, setNextTabId) = UseState(4);
            """,
            """
                        VStack(8,
                            (TabView(dynamicTabs.Select(t => Tab(t, TextBlock($"Content of {t}"))).ToArray()) with
                            {
                                SelectedIndex = dynamicIdx,
                                OnSelectedIndexChanged = i => setDynamicIdx(i),
                                OnTabCloseRequested = i => setDynamicTabs(dynamicTabs.Where((_, n) => n != i).ToArray()),
                            }),
                            Button("Add Tab", () =>
                            {
                                setDynamicTabs(dynamicTabs.Append($"Tab {nextTabId}").ToArray());
                                setNextTabId(nextTabId + 1);
                            }))
            """,
            """
            var (tabs, setTabs) = UseState(new[] { "Tab 1", "Tab 2", "Tab 3" });

            TabView(tabs.Select(t => Tab(t, TextBlock($"Content of {t}"))).ToArray()) with
            {
                SelectedIndex = idx,
                OnTabCloseRequested = i => setTabs(tabs.Where((_, n) => n != i).ToArray()),
            }

            Button("Add Tab", () => setTabs(tabs.Append($"Tab {nextId}").ToArray()))
            """,
            new[] { "idx", "nextId" }
        },

        // 4fa54750 — "Compact ItemsView". The card reads the price off the row (`p.Price`); the
        // snippet reads a bare `price`. Member-access awareness is the whole difference here.
        {
            "ItemsView Compact (4fa54750)",
            """
                    var products = new Product[]
                    {
                        new("Laptop", "Electronics", 999.99),
                        new("Notebook", "Office", 4.99),
                    }.ToList().AsReadOnly();
            """,
            """
                        ItemsView(
                            products,
                            p => p.Name,
                            (p, i) => ItemContainer(
                                HStack(8,
                                    TextBlock($"{i + 1}.").Width(20).Foreground(Theme.SecondaryText),
                                    TextBlock(p.Name).Flex(grow: 1),
                                    TextBlock($"${p.Price:F2}").Foreground(Theme.AccentText)
                                ).Padding(8)
                            )
                        ).Height(250)
            """,
            """
            ItemsView(
                products, p => p.Name,
                (p, i) => ItemContainer(
                    HStack(8, TextBlock(p.Name).Flex(grow: 1), TextBlock(price)))
            )
            """,
            new[] { "price" }
        },

        // 4fa54750 — DataGrid row-edit card. `source` resolves (the page above it declares one), so
        // only `columns` is left — and it survives only because a live `columns:` *label* is not
        // credited as a name in scope. Crediting labels would silence this row.
        {
            "DataGrid row-edit (4fa54750)",
            """
                    var source = UseMemo(() => new ListDataSource<Product>(BuildProducts(60), p => (RowKey)p.Id));
                    var rowEditSource = UseMemo(() => new ListDataSource<Product>(BuildProducts(12), p => (RowKey)p.Id));

                    FieldDescriptor[] Columns() =>
                    [
                        Column<Product>("Id", p => p.Id, width: 60),
                        Column<Product>("Name", p => p.Name, editable: true, width: 200),
                    ];
            """,
            """
                        DataGrid(
                            source: rowEditSource,
                            columns: Columns(),
                            editable: true,
                            editMode: EditMode.Row,
                            rowHeight: 36
                        ).Height(340)
            """,
            """
            DataGrid(
                source: source,
                columns: columns,
                editable: true,
                editMode: EditMode.Row,   // whole row edits and commits together
                onRowChanged: (key, item) => { /* persist the row */ return Task.CompletedTask; },
                rowHeight: 36)
            """,
            new[] { "columns" }
        },

        // 4fa54750 — "Left-Pane NavigationView". `tag` resolves against the card's own lambda
        // parameter — the live scope is deliberately permissive within a card — but `setTag` is a
        // setter that never existed, and one unresolved name is all it takes to fail the card.
        {
            "NavigationView Left-Pane (4fa54750)",
            """
                    var (selectedTag, setSelectedTag) = UseState("page1");
                    var (paneMode, setPaneMode) = UseState(0);

                    var items = new[] { NavItem("Home", icon: "Home", tag: "page1") };
                    var modes = new[] { NavigationViewPaneDisplayMode.Auto, NavigationViewPaneDisplayMode.Top };
            """,
            """
                        (NavigationView(items,
                            content: TextBlock($"Selected: {selectedTag}").Padding(16))
                        with
                        {
                            SelectedTag = selectedTag,
                            OnSelectedTagChanged = tag => { if (tag != null) setSelectedTag(tag); },
                            PaneTitle = "Nav Demo",
                            PaneDisplayMode = modes[paneMode],
                            IsSettingsVisible = false,
                        }).Height(300)
            """,
            """
            NavigationView(items, content: TextBlock("Selected: ..."))
            with {
                SelectedTag = tag,
                OnSelectedTagChanged = t => setTag(t),
                PaneTitle = "Nav Demo",
                PaneDisplayMode = modes[paneMode],   // driven by the Options combo below
            }
            """,
            new[] { "setTag" }
        },
    };

    [Theory]
    [MemberData(nameof(KnownBadCards))]
    public void KnownBadCards_AreReported(string label, string live, string sample, string snippet, string[] expected)
    {
        AssertReported(label, live, sample, snippet, expected);

        // Differential: the identical snippet beside a card that *does* declare those names is
        // silent. That pins each row to the absent name rather than to anything else about the
        // card — and it is the shape each fix commit produced.
        var syncedLive = string.Join("\n", expected.Select(name => $"        var {name} = Live();")) + "\n" + live;
        AssertReported($"{label} + synced", syncedLive, sample, snippet, []);
    }

    /// <summary>
    /// The half-synced shape, built from the two real sides of <c>c5d4bbd2</c>: the live code that
    /// commit produced and the snippet it replaced. The pairing is a counterfactual — the commit
    /// changed both together — but it is the state the tree would have been left in had it changed
    /// only one, and it is invisible to every other rule here because the snippet binds
    /// <c>setPath</c> itself.
    /// </summary>
    [Fact]
    public void HalfSyncedDeconstruction_IsReportedAsItsOwnKind()
    {
        const string live = """
                    var path = UseMemo(() => new[] { "Home", "Documents", "Reports" });
                    var (clicked, setClicked) = UseState("(none)");
        """;
        const string sample = """
                        VStack(8,
                            BreadcrumbBar(
                                path.Select(p => Breadcrumb(p)).ToArray(),
                                item => setClicked(item.Label)),
                            TextBlock($"Last clicked: {clicked}"))
        """;
        const string snippet = """
            var (path, setPath) = UseState(UseMemo(() => new[] { "Home", "Documents", "Reports" }));

            BreadcrumbBar(
                path.Select(p => Breadcrumb(p)).ToArray(),
                item => setClicked(item.Label))
            """;

        var finding = Assert.Single(Scan(live, sample, snippet));
        Assert.Equal(FindingKind.HalfSynced, finding.Kind);
        Assert.Equal("setPath", finding.Name);
        Assert.Contains("half-renamed", finding.Describe(), global::System.StringComparison.Ordinal);

        // The shipped card — snippet and live both on the UseMemo side — is silent, so the finding
        // tracks the drift and not the card.
        Assert.Empty(Reported(live, sample, snippet.Replace(
            "var (path, setPath) = UseState(UseMemo(() => new[] { \"Home\", \"Documents\", \"Reports\" }));",
            "var path = UseMemo(() => new[] { \"Home\", \"Documents\", \"Reports\" });")));
    }

    /// <summary>
    /// A snippet may rename a card's state wholesale, so a deconstruction none of whose names are
    /// live is silence, not a finding — that is what lets one TabView snippet stand for two cards.
    /// </summary>
    [Fact]
    public void WhollyRenamedDeconstruction_IsNotReported()
    {
        const string live = "        var (basicTabs, setBasicTabs) = UseState(new[] { \"Home\" });";
        const string sample = "            TabView(basicTabs.Select(t => Tab(t)).ToArray())";
        const string snippet = "var (tabs, setTabs) = UseState(new[] { \"Home\" });\nTabView(tabs.Select(t => Tab(t)).ToArray())";

        AssertReported("wholly renamed", live, sample, snippet, []);
        AssertReportedWithProbe("wholly renamed", live, sample, snippet, []);
    }

    // Each row below decides report-vs-skip for one construct. `expected` is empty when the
    // construct must bind or be ignored, and names the identifier when it must survive to a finding.
    public static TheoryData<string, string, string, string, string[]> BinderCases() => new()
    {
        // Snippet-declared names bind, scope-insensitively and by every declaration form.
        { "var declaration", "", "TextBlock(\"x\")", "var total = 1;\nTextBlock($\"{total}\")", [] },
        { "tuple deconstruction", "", "TextBlock(\"x\")", "var (a, setA) = UseState(0);\nButton(\"+\", () => setA(a + 1))", [] },
        { "lambda parameter", "", "TextBlock(\"x\")", "Slider(0, 1, 10, v => Log(v))", [] },
        { "local function", "", "TextBlock(\"x\")", "Element row(string s) => TextBlock(s);\nVStack(row(\"a\"))", [] },
        { "foreach", "", "TextBlock(\"x\")", "foreach (var entry in Rows()) { Use(entry); }", [] },
        { "pattern designation", "", "TextBlock(\"x\")", "if (Value() is int n) { Use(n); }", [] },
        { "undeclared name survives", "", "TextBlock(\"x\")", "TextBlock($\"{total}\")", ["total"] },

        // `var` is an identifier in a type slot. Reporting it would fire on almost every snippet,
        // so the type-slot test is load-bearing, not decoration.
        { "var is a type slot", "", "TextBlock(\"x\")", "foreach (var item in Rows()) { }", [] },

        // Member access resolves the receiver, never the member.
        { "member access rhs", "", "TextBlock(\"x\")", "Rows().Select(p => TextBlock(p.price))", [] },
        { "receiver still checked", "", "TextBlock(\"x\")", "TextBlock(row.name)", ["row"] },

        // Named-argument labels: excluded on the snippet side, not credited on the live side.
        { "snippet label", "", "TextBlock(\"x\")", "Column(\"Id\", width: 60)", [] },
        { "live label is not a name", "", "DataGrid(columns: Columns())", "DataGrid(columns: columns)", ["columns"] },

        // Initializer keys name properties; their values are still checked.
        { "initializer key", "", "TextBlock(\"x\")", "Options() with { padding = 4 }", [] },
        { "initializer value", "", "TextBlock(\"x\")", "Options() with { padding = pad }", ["pad"] },

        // Matching is Ordinal, so a differently-cased live name does not resolve a snippet name…
        { "case sensitive", "        var Price = 1;", "TextBlock(\"x\")", "TextBlock(price)", ["price"] },
        // …and PascalCase snippet names are out of scope entirely.
        { "pascal case ignored", "", "TextBlock(\"x\")", "TextBlock(Price)", [] },

        // Contextual keywords are excluded by where they sit, not by their spelling, so an ordinary
        // local named `value` is still a name the reader has to be able to resolve.
        { "nameof is not a value", "", "TextBlock(\"x\")", "TextBlock(nameof(Price))", [] },
        { "value is checked", "", "TextBlock(\"x\")", "TextBox(value, onChange)", ["onChange", "value"] },
        { "value binds like any other local", "        var value = Seed();", "TextBlock(\"x\")", "TextBox(value)", [] },
        { "dynamic in a type slot", "", "TextBlock(\"x\")", "dynamic row = Rows(); Use(row)", [] },

        // Interpolation holes are code; comments and string bodies are not.
        { "interpolation hole", "", "TextBlock(\"x\")", "TextBlock($\"total: {total}\")", ["total"] },
        { "comment", "", "TextBlock(\"x\")", "// total is set above\nTextBlock(\"hi\")", [] },
        { "string body", "", "TextBlock(\"x\")", "TextBlock(\"total\")", [] },
    };

    [Theory]
    [MemberData(nameof(BinderCases))]
    public void Binder_DecidesWhatCounts(string label, string live, string sample, string snippet, string[] expected)
    {
        AssertReported(label, live, sample, snippet, expected);

        // Half these rows expect silence, which a rule that has stopped reporting also produces. The
        // probe makes every row — including those — fail against a dead rule.
        AssertReportedWithProbe(label, live, sample, snippet, expected);
    }

    /// <summary>
    /// Snippets are fragments, so the parser sees elisions, missing semicolons and prose. None of
    /// that may turn into a finding: this lint reports, it never fails closed. The probe proves the
    /// converse at the same time — Roslyn recovered far enough that the rule still read the snippet,
    /// so "nothing reported" here means "nothing wrong", not "nothing parsed".
    /// </summary>
    [Theory]
    [InlineData("elision", "TabView(tabs) with\n{\n    ...\n}")]
    [InlineData("missing semicolons", "Button(\"Save\")\nTextBlock(\"Saved\")")]
    [InlineData("comment only", "// Wrap the panel in a Border to get Padding.")]
    [InlineData("stray brace", "VStack(\n    TextBlock(\"a\"),")]
    public void UnparseableSnippets_AreSkipped_NotReported(string label, string snippet)
    {
        const string live = "        var tabs = Tabs();";
        const string sample = "            TabView(tabs)";

        AssertReported(label, live, sample, snippet, []);
        AssertReportedWithProbe(label, live, sample, snippet, []);
    }

    /// <summary>
    /// Two guidance pages build their snippet with <c>string.Join</c> over the same data the card
    /// renders. There is no literal to read and the card cannot drift from itself, so the card is
    /// counted and skipped — never silently dropped, which would hide it from the vacuity counters.
    /// </summary>
    [Fact]
    public void ComputedSnippetArgument_IsCountedAndSkipped()
    {
        var scan = ScanSource("""
            namespace Gallery;

            class __Page
            {
                Element Render()
                {
                    var lines = string.Join("\n", Entries().Select(e => e.Token));
                    return SampleCard("Card", VStack(Entries().Select(Row).ToArray()), lines);
                }
            }
            """);

        Assert.Equal(1, scan.Cards);
        Assert.Equal(1, scan.SnippetsNotLiteral);
        Assert.Equal(0, scan.NamesChecked);
        Assert.Empty(scan.Findings);
    }

    /// <summary>
    /// The snippet is the third positional argument or the <c>sourceCode:</c> label wherever it
    /// sits. Reading the wrong argument would make the lint scan the card title and pass vacuously.
    /// </summary>
    [Theory]
    [InlineData("positional", """SampleCard("Card", TextBlock("x"), @"TextBlock(missing)")""")]
    [InlineData("named", """SampleCard("Card", TextBlock("x"), sourceCode: @"TextBlock(missing)")""")]
    [InlineData("named out of order", """SampleCard(sourceCode: @"TextBlock(missing)", title: "Card", sample: TextBlock("x"))""")]
    [InlineData("with options", """SampleCard("Card", TextBlock("x"), @"TextBlock(missing)", options: OptionPanel())""")]
    public void SnippetArgument_IsFoundInEveryCallShape(string label, string call)
    {
        var scan = ScanSource($$"""
            namespace Gallery;

            class __Page
            {
                Element Render() => {{call}};
            }
            """);

        Assert.Equal(1, scan.Cards);
        var finding = Assert.Single(scan.Findings);
        Assert.Equal($"{label}: Card/missing", $"{label}: {finding.Card}/{finding.Name}");
    }

    /// <summary>
    /// A name is resolved against the page's shared context and the card's own sample — not against
    /// its siblings. A local bound inside another card's lambda is not reachable from here, and
    /// crediting it would let one card's name legitimise another card's stale snippet.
    /// </summary>
    [Fact]
    public void SiblingCardLocals_DoNotResolveAName()
    {
        var page = """
            namespace Gallery;

            class __Page
            {
                Element Render()
                {
                    var rows = Rows();
                    return VStack(
                        SampleCard("Sibling", VStack(rows.Select(row => TextBlock(row.Name)).ToArray()),
                            sourceCode: @"VStack(rows.Select(row => TextBlock(row.Name)).ToArray())"),
                        SampleCard("Card", TextBlock("x"),
                            sourceCode: @"TextBlock(row.Name)"));
                }
            }
            """;

        var scan = ScanSource(page);

        Assert.Equal(2, scan.Cards);
        var finding = Assert.Single(scan.Findings);
        Assert.Equal("Card", finding.Card);
        Assert.Equal("row", finding.Name);

        // The sibling's own snippet uses the same name and stays silent, because there `row` is in
        // the card that binds it. Same page, same name, opposite verdicts — that is the scope.
        Assert.DoesNotContain(scan.Findings, f => f.Card == "Sibling");
    }
}

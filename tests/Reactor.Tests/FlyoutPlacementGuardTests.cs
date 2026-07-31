using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.UI.Reactor.Cli.Pack;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Controls.Primitives;
using Xunit;

namespace Microsoft.UI.Reactor.Tests;

/// <summary>
/// Regression cover for the <c>Flyout(...)</c>-with-default-placement process kill.
///
/// WinUI's <c>FlyoutBase::ShowAtCore</c> validates the effective placement through
/// <c>FlyoutBase::ValidateAndSetParameters</c>, whose switch only accepts <c>0..12</c>
/// and fails everything else with <c>E_INVALIDARG</c>. <c>FlyoutPlacementMode.Auto</c>
/// is <c>13</c>, so a flyout left at <c>Auto</c> fail-fasts the process the moment it is
/// shown. Reactor's element records default to <c>Auto</c>, so the value must never reach
/// the WinUI DP.
///
/// These are headless tests: <see cref="FlyoutPlacementMode"/> is a WinRT enum (a value
/// type) so it is safe to touch, and the structural test only parses source text — no
/// <c>Microsoft.UI.Xaml</c> object is constructed.
/// </summary>
public class FlyoutPlacementGuardTests
{
    // ════════════════════════════════════════════════════════════════
    //  Decision function — the guard itself
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// The 13 placement values WinUI's validator switch accepts (0..12). Spelled out
    /// rather than derived from the enum so that a future WinUI enum addition shows up
    /// as a deliberate failure in <see cref="Accepted_Values_Cover_The_Whole_Enum_Except_Auto"/>
    /// instead of silently widening the guard.
    /// </summary>
    private static readonly FlyoutPlacementMode[] s_validatorAcceptedModes =
    [
        FlyoutPlacementMode.Top,
        FlyoutPlacementMode.Bottom,
        FlyoutPlacementMode.Left,
        FlyoutPlacementMode.Right,
        FlyoutPlacementMode.Full,
        FlyoutPlacementMode.TopEdgeAlignedLeft,
        FlyoutPlacementMode.TopEdgeAlignedRight,
        FlyoutPlacementMode.BottomEdgeAlignedLeft,
        FlyoutPlacementMode.BottomEdgeAlignedRight,
        FlyoutPlacementMode.LeftEdgeAlignedTop,
        FlyoutPlacementMode.LeftEdgeAlignedBottom,
        FlyoutPlacementMode.RightEdgeAlignedTop,
        FlyoutPlacementMode.RightEdgeAlignedBottom,
    ];

    public static TheoryData<FlyoutPlacementMode> ValidatorAcceptedModes()
        => new(s_validatorAcceptedModes);

    [Theory]
    [MemberData(nameof(ValidatorAcceptedModes))]
    public void ShouldApply_Is_True_For_Every_Placement_WinUI_Accepts(FlyoutPlacementMode mode)
    {
        Assert.True(
            FlyoutPlacement.ShouldApply(mode),
            $"{mode} is inside WinUI's accepted 0..12 range and must be written to FlyoutBase.Placement.");
    }

    [Fact]
    public void ShouldApply_Is_False_For_Auto()
    {
        // Auto == 13 falls off the end of ValidateAndSetParameters' switch → E_INVALIDARG
        // → stowed ArgumentException → process termination when the flyout is shown.
        Assert.False(FlyoutPlacement.ShouldApply(FlyoutPlacementMode.Auto));
    }

    [Fact]
    public void Auto_Is_The_Only_Value_Outside_The_Validator_Range()
    {
        // Pins the premise the guard rests on: Auto is 13, everything else is 0..12.
        Assert.Equal(13, (int)FlyoutPlacementMode.Auto);

        var outsideRange = Enum.GetValues<FlyoutPlacementMode>()
            .Where(mode => (int)mode is < 0 or > 12)
            .ToList();

        Assert.Equal([FlyoutPlacementMode.Auto], outsideRange);
    }

    [Fact]
    public void Accepted_Values_Cover_The_Whole_Enum_Except_Auto()
    {
        var accepted = s_validatorAcceptedModes.ToHashSet();

        var expected = Enum.GetValues<FlyoutPlacementMode>()
            .Where(mode => mode != FlyoutPlacementMode.Auto)
            .ToHashSet();

        Assert.Equal(expected, accepted);
    }

    // ════════════════════════════════════════════════════════════════
    //  Structural guard — every flyout placement write goes through the choke point
    // ════════════════════════════════════════════════════════════════

    private const string HelperFileName = "FlyoutPlacement.cs";

    /// <summary>
    /// The <c>CommandBarFlyout</c> reconciler methods, which used to carry their own
    /// placement helper (<c>Reconciler.ApplyFlyoutPlacement</c>) and their own exemption from
    /// the bypass test below. The two helpers silently diverged while that exemption stood, so
    /// these names are kept — now to assert the opposite: that they route through the one
    /// choke point like every other flyout site.
    /// </summary>
    private static readonly string[] CommandBarFlyoutPlacementMethods =
    [
        "MountCommandBarFlyout",
        "UpdateCommandBarFlyout",
    ];

    [Fact]
    public void No_Flyout_Placement_Write_Bypasses_FlyoutPlacement()
    {
        var bypasses = ScanCoreForFlyoutPlacementWrites()
            .Where(w => !string.Equals(Path.GetFileName(w.File), HelperFileName, StringComparison.Ordinal))
            .Select(w => $"{Path.GetFileName(w.File)}({w.Line}) in {w.Method}: {w.Text}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            bypasses.Count == 0,
            "These sites write FlyoutBase.Placement directly instead of routing through " +
            "FlyoutPlacement.Apply, which lets FlyoutPlacementMode.Auto reach WinUI's validator " +
            "and terminate the process when the flyout is shown: " +
            $"[{string.Join("; ", bypasses)}]. Route the write through FlyoutPlacement.Apply. " +
            "There is deliberately no exemption list: a second helper for this DP is exactly " +
            "what issue #953 retired, after the first pair diverged unnoticed behind one.");
    }

    [Fact]
    public void FlyoutPlacement_Is_The_Only_File_That_Writes_The_Dp()
    {
        // The class summary claims FlyoutPlacement is "the single choke point for pushing a
        // Reactor element's Placement onto a live WinUI FlyoutBase". Make that literally true
        // rather than aspirational. Equality (not "contains") so this fails in both
        // directions: a second writer fails, and so does a scan that silently found nothing.
        var files = ScanCoreForFlyoutPlacementWrites()
            .Select(w => Path.GetFileName(w.File))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Assert.Equal([HelperFileName], files);
    }

    [Fact]
    public void CommandBarFlyout_Sites_Route_Through_The_Choke_Point()
    {
        // The half the bypass test cannot see: a method that stopped applying placement
        // altogether writes nothing, so it would pass "no direct write" vacuously. Assert the
        // positive — these methods still call FlyoutPlacement.Apply — so dropping the call,
        // renaming the method, or reintroducing a private helper all fail here.
        //
        // Scope: method granularity, deliberately. A syntactic scan cannot tell which branch
        // of UpdateCommandBarFlyout a call sits in, so per-branch coverage is left to the
        // selftests that open a real flyout (CbfReset_AutoRestoresDefaultPlacement drives the
        // reuse-existing branch; CbfAuto_OpenedWithoutFailFast the mount branch).
        var routed = ScanCoreForChokePointCalls()
            .Select(c => c.Method)
            .ToHashSet(StringComparer.Ordinal);

        // Anti-vacuity: a call scanner that matched nothing would make the assertion below
        // fail rather than pass, but pin a known-routed non-CommandBarFlyout site too so a
        // scanner that only ever matches one shape is visible.
        Assert.Contains("MountFlyout", routed);

        Assert.All(
            CommandBarFlyoutPlacementMethods,
            method => Assert.True(
                routed.Contains(method),
                $"{method} no longer calls FlyoutPlacement.Apply. It is one of the two sites " +
                "that used to own a duplicate placement helper; placement there must go " +
                "through the shared choke point."));
    }

    [Fact]
    public void Scanner_Detects_The_Writes_Inside_FlyoutPlacement()
    {
        // Anti-vacuity: a detector that never matches anything would make the test above
        // pass unconditionally. Prove it finds both legitimate writes in the choke point —
        // the explicit assignment and the ClearValue that handles Auto.
        var inHelper = ScanCoreForFlyoutPlacementWrites()
            .Where(w => string.Equals(Path.GetFileName(w.File), HelperFileName, StringComparison.Ordinal))
            .Select(w => w.Text)
            .ToList();

        Assert.Equal(2, inHelper.Count);
        Assert.Contains(inHelper, t => t.Contains("flyout.Placement = placement", StringComparison.Ordinal));
        Assert.Contains(inHelper, t => t.Contains("ClearValue", StringComparison.Ordinal));
    }

    [Fact]
    public void Scanner_Reads_The_Whole_Core_Library()
    {
        // Anti-vacuity: if the file walk silently found nothing (wrong root, renamed
        // folder) the bypass test would also pass unconditionally.
        Assert.True(
            CoreSourceFiles().Count > 100,
            "Expected to scan the whole src/Reactor tree; the source walk found too few files.");
    }

    [Fact]
    public void Scanner_Flags_A_Synthetic_Bypass()
    {
        // Anti-vacuity, part 3: prove the matcher recognizes every write shape that can
        // reach FlyoutBase.Placement — a member assignment, a flyout object initializer,
        // and the SetValue/SetCurrentValue DP escape hatches — while ignoring the
        // look-alikes (element-record initializers, a differently-named placement DP).
        const string source = """
            class Sample
            {
                void M(Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase existing)
                {
                    existing.Placement = Placement;
                    var f = new WinUI.Flyout { Content = null, Placement = Placement };
                    existing.SetValue(WinPrim.FlyoutBase.PlacementProperty, Placement);
                    existing.SetCurrentValue(FlyoutBase.PlacementProperty, Placement);
                    existing.ClearValue(WinPrim.FlyoutBase.PlacementProperty);
                    var e = new SomeElement { Placement = Placement };
                    var t = new TeachingTip { PreferredPlacement = Placement };
                    existing.SetValue(TeachingTip.PreferredPlacementProperty, Placement);
                    existing.ClearValue(TeachingTip.PreferredPlacementProperty);
                    var read = existing.Placement;
                }
            }
            """;

        var hits = FindPlacementWrites("synthetic.cs", source)
            .Select(w => w.Text)
            .ToList();

        Assert.Equal(5, hits.Count);
        Assert.Contains(hits, h => h.StartsWith("existing.Placement", StringComparison.Ordinal));
        Assert.Contains(hits, h => h.Contains("new WinUI.Flyout", StringComparison.Ordinal));
        Assert.Contains(hits, h => h.Contains("SetValue(WinPrim.FlyoutBase.PlacementProperty", StringComparison.Ordinal));
        Assert.Contains(hits, h => h.Contains("SetCurrentValue(FlyoutBase.PlacementProperty", StringComparison.Ordinal));
        Assert.Contains(hits, h => h.Contains("ClearValue(WinPrim.FlyoutBase.PlacementProperty", StringComparison.Ordinal));
    }

    [Fact]
    public void Scanner_Attributes_Each_Write_To_Its_Enclosing_Method()
    {
        // Both the bypass failure message and
        // CommandBarFlyout_Sites_Route_Through_The_Choke_Point key off the enclosing method
        // name, so prove two identical writes in one file are attributed to the method they
        // actually sit in — not to the file, and not to "<none>".
        const string source = """
            class Sample
            {
                void MountCommandBarFlyout()
                {
                    var flyout = new WinUI.CommandBarFlyout { Placement = cbf.Placement };
                }

                void MountFlyout()
                {
                    var flyout = new WinUI.Flyout { Placement = flyEl.Placement };
                }
            }
            """;

        var byMethod = FindPlacementWrites("synthetic.cs", source)
            .ToDictionary(w => w.Method, w => w);

        Assert.Equal(2, byMethod.Count);
        Assert.Contains("new WinUI.CommandBarFlyout", byMethod["MountCommandBarFlyout"].Text, StringComparison.Ordinal);
        Assert.Contains("new WinUI.Flyout", byMethod["MountFlyout"].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Scanner_Finds_Choke_Point_Calls_Regardless_Of_Qualification()
    {
        // Anti-vacuity for CommandBarFlyout_Sites_Route_Through_The_Choke_Point: a call
        // matcher that missed the shape actually used in src/Reactor would turn that test
        // into a permanent failure rather than a silent pass, but a matcher that is too
        // loose is the real risk — prove it ignores same-named Apply calls on other owners.
        const string source = """
            class Sample
            {
                void MountFlyout()
                {
                    FlyoutPlacement.Apply(flyout, flyEl.Placement);
                    Core.FlyoutPlacement.Apply(flyout, flyEl.Placement);
                    SomethingElse.Apply(flyout, flyEl.Placement);
                    FlyoutPlacement.ShouldApply(flyEl.Placement);
                }
            }
            """;

        var calls = FindChokePointCalls("synthetic.cs", source).ToList();

        Assert.Equal(2, calls.Count);
        Assert.All(calls, c => Assert.Equal("MountFlyout", c.Method));
    }

    // ── scanning helpers ────────────────────────────────────────────

    private readonly record struct PlacementWrite(string File, int Line, string Method, string Text);

    private readonly record struct ChokePointCall(string File, int Line, string Method);

    // Five [Fact]s consume these; walking src/Reactor and Roslyn-parsing every file once per
    // test is pure overhead. Lazy also caches the assertion failure, so a broken repo-root
    // lookup reports identically in every test instead of racing. Mirrors the memoization in
    // AnalyzerTests/SetEventSubscriptionConsistencyTests.
    private static readonly Lazy<List<string>> s_coreSourceFiles = new(FindCoreSourceFiles);
    private static readonly Lazy<List<PlacementWrite>> s_placementWrites = new(ScanCore);
    private static readonly Lazy<List<ChokePointCall>> s_chokePointCalls = new(ScanCoreForCalls);

    private static List<string> CoreSourceFiles() => s_coreSourceFiles.Value;

    private static List<PlacementWrite> ScanCoreForFlyoutPlacementWrites() => s_placementWrites.Value;

    private static List<ChokePointCall> ScanCoreForChokePointCalls() => s_chokePointCalls.Value;

    private static List<string> FindCoreSourceFiles()
    {
        var root = RepoRootFinder.FindRepoRoot();
        Assert.NotNull(root);
        var coreDir = Path.Join(root!, "src", "Reactor");
        Assert.True(Directory.Exists(coreDir), $"src/Reactor not found at {coreDir}");

        return Directory
            .EnumerateFiles(coreDir, "*.cs", SearchOption.AllDirectories)
            // bin/obj hold generated + copied sources that are not part of the hand-written surface.
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
    }

    private static List<PlacementWrite> ScanCore()
    {
        var writes = new List<PlacementWrite>();
        foreach (var file in CoreSourceFiles())
            writes.AddRange(FindPlacementWrites(file, File.ReadAllText(file)));
        return writes;
    }

    private static List<ChokePointCall> ScanCoreForCalls()
    {
        var calls = new List<ChokePointCall>();
        foreach (var file in CoreSourceFiles())
            calls.AddRange(FindChokePointCalls(file, File.ReadAllText(file)));
        return calls;
    }

    /// <summary>
    /// Finds calls to the choke point itself — <c>FlyoutPlacement.Apply(...)</c>, however
    /// <c>FlyoutPlacement</c> is qualified — so a site can be asserted to route through it
    /// rather than merely to contain no direct write.
    /// </summary>
    private static IEnumerable<ChokePointCall> FindChokePointCalls(string file, string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);

        foreach (var invocation in tree.GetCompilationUnitRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax
                { Name.Identifier.Text: "Apply", Expression: var owner }) continue;
            if (LeafName(owner) != nameof(FlyoutPlacement)) continue;

            yield return new ChokePointCall(
                file,
                invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                EnclosingMethodName(invocation));
        }
    }

    /// <summary>
    /// Finds writes that land on a live WinUI <c>FlyoutBase.Placement</c> DP:
    /// <list type="bullet">
    /// <item><c>receiver.Placement = ...</c> — Reactor element records are <c>init</c>-only,
    /// so a member assignment can only target a mutable control.</item>
    /// <item><c>new ...Flyout { Placement = ... }</c> — object initializers on a WinUI
    /// flyout type. Element records are named <c>...Element</c> and the DSL builds them
    /// with target-typed <c>new(...)</c>, so neither is matched.</item>
    /// <item><c>SetValue(FlyoutBase.PlacementProperty, ...)</c> and
    /// <c>SetCurrentValue(...)</c> — the DP escape hatches that bypass the CLR property.</item>
    /// </list>
    /// The match is syntactic (no semantic model, so the scan needs no compilation), which is
    /// why it keys on the <c>FlyoutBase</c> qualifier for the DP forms and on a
    /// <c>...Flyout</c> type name for object initializers: a future non-flyout <c>Placement</c>
    /// setter (a <c>TeachingTip</c>, say) must not be mistaken for a bypass.
    /// </summary>
    private static IEnumerable<PlacementWrite> FindPlacementWrites(string file, string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetCompilationUnitRoot();

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case AssignmentExpressionSyntax assignment
                    when assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                      && IsPlacementAssignment(assignment):
                    yield return Write(file, assignment, Flatten(assignment));
                    break;

                case InvocationExpressionSyntax invocation when IsPlacementDpWrite(invocation):
                    yield return Write(file, invocation, Flatten(invocation.ToString()));
                    break;
            }
        }
    }

    private static bool IsPlacementAssignment(AssignmentExpressionSyntax assignment) =>
        assignment.Left switch
        {
            // receiver.Placement = ...
            MemberAccessExpressionSyntax { Name.Identifier.Text: "Placement" } => true,
            // { Placement = ... } inside new SomethingFlyout { ... }
            IdentifierNameSyntax { Identifier.Text: "Placement" } => IsFlyoutObjectInitializer(assignment),
            _ => false,
        };

    /// <summary>
    /// Matches <c>x.SetValue(&lt;anything&gt;FlyoutBase.PlacementProperty, v)</c>, the
    /// <c>SetCurrentValue</c> sibling, and <c>x.ClearValue(...PlacementProperty)</c>,
    /// regardless of how <c>FlyoutBase</c> is qualified.
    /// </summary>
    private static bool IsPlacementDpWrite(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax
            { Name.Identifier.Text: "SetValue" or "SetCurrentValue" or "ClearValue" }) return false;
        if (invocation.ArgumentList.Arguments.Count < 1) return false;

        var dp = invocation.ArgumentList.Arguments[0].Expression;
        return dp is MemberAccessExpressionSyntax
        {
            Name.Identifier.Text: "PlacementProperty",
            Expression: var owner,
        } && LeafName(owner) == "FlyoutBase";
    }

    private static string LeafName(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
        SimpleNameSyntax simple => simple.Identifier.Text,
        _ => expression.ToString(),
    };

    private static PlacementWrite Write(string file, SyntaxNode node, string text)
        => new(file,
               node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
               EnclosingMethodName(node),
               text);

    /// <summary>
    /// Nearest enclosing method/accessor/local-function name. Names the offending site in the
    /// bypass failure message, and is what
    /// <see cref="CommandBarFlyout_Sites_Route_Through_The_Choke_Point"/> matches against —
    /// a stable identifier rather than a file or a line number.
    /// </summary>
    private static string EnclosingMethodName(SyntaxNode node)
    {
        for (var n = node.Parent; n is not null; n = n.Parent)
        {
            switch (n)
            {
                case MethodDeclarationSyntax m: return m.Identifier.Text;
                case LocalFunctionStatementSyntax lf: return lf.Identifier.Text;
                case ConstructorDeclarationSyntax c: return c.Identifier.Text;
                case AccessorDeclarationSyntax a
                    when a.Parent?.Parent is PropertyDeclarationSyntax p:
                    return p.Identifier.Text;
                case PropertyDeclarationSyntax p2: return p2.Identifier.Text;
            }
        }
        return "<none>";
    }

    private static bool IsFlyoutObjectInitializer(AssignmentExpressionSyntax assignment)
    {
        if (assignment.Parent is not InitializerExpressionSyntax initializer) return false;
        if (initializer.Parent is not ObjectCreationExpressionSyntax creation) return false;

        var typeName = creation.Type switch
        {
            QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
            SimpleNameSyntax simple => simple.Identifier.Text,
            _ => creation.Type.ToString(),
        };
        return typeName.EndsWith("Flyout", StringComparison.Ordinal);
    }

    private static string Flatten(AssignmentExpressionSyntax assignment)
        => Flatten(assignment.Parent is InitializerExpressionSyntax { Parent: ObjectCreationExpressionSyntax creation }
            ? creation.ToString()
            : assignment.ToString());

    private static string Flatten(string text)
    {
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 120 ? text : text[..120] + "…";
    }
}

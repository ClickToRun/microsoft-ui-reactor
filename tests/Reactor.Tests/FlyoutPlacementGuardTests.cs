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
    /// The choke point's repo-relative path, anchored at the scan root rather than at the
    /// file name or a bare <c>Core/</c> segment. A second <c>FlyoutPlacement.cs</c> dropped
    /// anywhere else under <c>src/Reactor</c> — including under some other <c>Core/</c>
    /// folder — is therefore a bypass rather than a self-granted exemption.
    /// </summary>
    private static readonly string HelperPathSuffix =
        Path.DirectorySeparatorChar +
        string.Join(Path.DirectorySeparatorChar, "src", "Reactor", "Core", HelperFileName);

    private static bool IsChokePointFile(string file)
        => file.EndsWith(HelperPathSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every method in <c>src/Reactor</c> that applies a flyout placement. The bypass test
    /// below cannot see a site that stops applying placement <i>altogether</i> — no write is
    /// no bypass — so this list is the positive half of the invariant.
    /// </summary>
    /// <remarks>
    /// The two <c>CommandBarFlyout</c> entries are the reason this list exists at all: they
    /// used to carry their own placement helper (<c>Reconciler.ApplyFlyoutPlacement</c>) and
    /// their own exemption from the bypass test, and the two helpers silently diverged while
    /// that exemption stood. Matched by set equality rather than containment, so a new flyout
    /// site cannot join the choke point without someone deciding how its branches get covered.
    /// </remarks>
    private static readonly string[] PlacementApplyingMethods =
    [
        "CreateFlyoutFromElement",
        "MountCommandBarFlyout",
        "MountFlyout",
        "UpdateCommandBarFlyout",
        "UpdateFlyoutElement",
        "UpdateFlyoutInPlace",
    ];

    [Fact]
    public void No_Flyout_Placement_Write_Bypasses_FlyoutPlacement()
    {
        var writes = ScanCoreForFlyoutPlacementWrites();

        // Anti-vacuity: the choke point itself writes the DP, so an empty scan means the file
        // walk or the matcher broke — not that the invariant holds.
        Assert.NotEmpty(writes);

        var bypasses = writes
            .Where(w => !IsChokePointFile(w.File))
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
    public void Every_Flyout_Site_Routes_Through_The_Choke_Point()
    {
        // The half the bypass test cannot see: a site that stopped applying placement
        // altogether writes nothing, so it would pass "no direct write" vacuously. Assert the
        // positive — each of these methods still calls FlyoutPlacement.Apply — so dropping the
        // call, or reintroducing a private helper alongside it, fails here.
        //
        // Scope: method granularity, deliberately. A syntactic scan cannot tell which branch
        // of a multi-branch method a call sits in — deleting one of UpdateCommandBarFlyout's
        // two calls still leaves the method "routed" here. Per-branch coverage is therefore
        // the selftests' job, and each branch has an assertion that fails without its call:
        // CbfReset_AutoRestoresDefaultPlacement (reuse-existing arm),
        // CmdBarFlyout_FreshPlacementBottom (fresh-flyout arm), and
        // CbfReset_ExplicitPlacementApplied (mount) — that last one mounts an explicit
        // Bottom, so dropping the mount call strands WinUI's own default and fails it.
        // CbfAuto_OpenedWithoutFailFast is *not* the mount oracle: an Auto mount clears the
        // DP to that same default, which is exactly what deleting the call also produces.
        var routed = ScanCoreForChokePointCalls()
            .Select(c => c.Method)
            .ToHashSet(StringComparer.Ordinal);

        var missing = PlacementApplyingMethods.Where(m => !routed.Contains(m)).ToList();
        Assert.True(
            missing.Count == 0,
            $"[{string.Join(", ", missing)}] no longer call FlyoutPlacement.Apply. Either they " +
            "stopped applying placement — which the bypass test cannot detect, because a " +
            "missing write is not a bypass — or they were renamed, in which case update " +
            $"{nameof(PlacementApplyingMethods)}.");

        // Set equality, not containment: an unlisted site would otherwise be silently
        // unguarded, which is the failure mode this whole class exists to prevent.
        var unlisted = routed
            .Where(m => !PlacementApplyingMethods.Contains(m, StringComparer.Ordinal))
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToList();
        Assert.True(
            unlisted.Count == 0,
            $"[{string.Join(", ", unlisted)}] call FlyoutPlacement.Apply but are not listed in " +
            $"{nameof(PlacementApplyingMethods)}. Add them — and if a new site has more than one " +
            "branch, add a selftest per branch, because this test is method-granular and cannot " +
            "tell them apart.");
    }

    [Fact]
    public void Choke_Point_Exemption_Is_Pinned_To_The_One_Real_File()
    {
        var root = RepoRootFinder.FindRepoRoot();
        Assert.NotNull(root);

        // The exemption is what makes the bypass test tolerable at all, so it has to be
        // narrow: the real file is recognised...
        Assert.True(
            IsChokePointFile(Path.Join(root!, "src", "Reactor", "Core", HelperFileName)),
            "The real choke point is no longer recognised as one, which makes the bypass " +
            "test fail on the very file it is supposed to exempt.");

        // ...and nothing else is. A decoy under some other Core/ folder inside the scan root
        // would otherwise grant itself the exemption this class exists to deny — the same
        // fail-open shape as the allow-lists issue #953 removed.
        Assert.All(
            new[]
            {
                Path.Join(root!, "src", "Reactor", "Flex", "Core", HelperFileName),
                Path.Join(root!, "src", "Reactor", "Core", "Nested", HelperFileName),
                Path.Join(root!, "src", "Reactor", HelperFileName),
            },
            decoy => Assert.False(
                IsChokePointFile(decoy),
                $"'{decoy}' exempts itself from the bypass test despite not being the choke " +
                "point. Anchor HelperPathSuffix at the scan root, not at a file name or a " +
                "bare Core/ segment."));
    }

    [Fact]
    public void Scanner_Detects_The_Writes_Inside_FlyoutPlacement()
    {
        // Anti-vacuity: a detector that never matches anything would make the test above
        // pass unconditionally. Prove it finds both legitimate writes in the choke point —
        // the explicit assignment and the ClearValue that handles Auto.
        var inHelper = ScanCoreForFlyoutPlacementWrites()
            .Where(w => IsChokePointFile(w.File))
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
    public void Scanner_Flags_A_Target_Typed_Flyout_Initializer()
    {
        // `X x = new() { ... }` is an ImplicitObjectCreationExpression, which carries no type
        // of its own — a matcher that only understood `new X { ... }` would let this shape
        // write Auto straight to the DP. An element record built the same way must still be
        // ignored, so the declared type is what decides.
        const string source = """
            class Sample
            {
                void M()
                {
                    WinUI.CommandBarFlyout a = new() { Placement = n.Placement };
                    WinUI.Flyout b = new() { Content = null, Placement = n.Placement };
                    WinUI.MenuFlyout? c = new() { Target = null, Placement = n.Placement };
                    SomeElement d = new() { Placement = n.Placement };
                    TeachingTip e = new() { PreferredPlacement = n.Placement };
                }

                // The shape every DSL flyout factory uses: the target type is the method's
                // return type, and it is an immutable element record, not a live control.
                static ContentFlyoutElement ContentFlyout(Element content, FlyoutPlacementMode placement) =>
                    new(content) { Placement = placement };
            }
            """;

        var hits = FindPlacementWrites("synthetic.cs", source)
            .Select(w => w.Text)
            .ToList();

        Assert.Equal(3, hits.Count);
        // A target-typed `new()` carries no type text, so the hits are told apart by their
        // other initializer members. Mutually exclusive predicates, so matching all three
        // proves three distinct shapes were found rather than one shape found repeatedly.
        Assert.Contains(hits, h => !h.Contains("Content", StringComparison.Ordinal)
                                && !h.Contains("Target", StringComparison.Ordinal));
        Assert.Contains(hits, h => h.Contains("Content = null", StringComparison.Ordinal));
        Assert.Contains(hits, h => h.Contains("Target = null", StringComparison.Ordinal));
    }

    [Fact]
    public void Scanner_Flags_A_Target_Typed_Flyout_Inside_A_Collection()
    {
        // An array's or single-argument generic's leaf is still the element type. Left
        // unpeeled the declared name comes back non-null but wrong — "WinUI.Flyout[]",
        // "List" — which fails the Flyout suffix check *and* slips past the fail-closed arm,
        // so a real write to a real flyout goes unseen. Same shape as the nullable hole.
        //
        // The two non-flyout cases matter just as much: peeling, rather than giving up on
        // every collection, is what keeps the guard off Reactor's own element records when
        // they are held in an array or list — an ordinary DSL shape, not an exotic one.
        const string source = """
            class Sample
            {
                void M()
                {
                    WinUI.Flyout[] a = [new() { Content = null, Placement = n.Placement }];
                    List<WinUI.MenuFlyout> b = new() { new() { Target = null, Placement = n.Placement } };
                    System.Collections.Generic.List<WinUI.Flyout> c = new() { new() { IsOpen = false, Placement = n.Placement } };
                    SomeElement[] d = [new() { Placement = n.Placement }];
                    List<SomeElement> e = new() { new() { Placement = n.Placement } };
                }
            }
            """;

        var hits = FindPlacementWrites("synthetic.cs", source)
            .Select(w => w.Text)
            .ToList();

        // Exactly the three live-control shapes; the element-record array and list are not
        // flagged. Asserting the count both ways is what makes this non-vacuous: dropping
        // the array peel loses `a`, dropping the generic peel entirely loses `b` and `c`,
        // and replacing the peel with a blanket fail-closed wrongly gains `e`.
        Assert.Equal(3, hits.Count);
        Assert.Contains(hits, h => h.Contains("Content = null", StringComparison.Ordinal));
        Assert.Contains(hits, h => h.Contains("Target = null", StringComparison.Ordinal));
        Assert.Contains(hits, h => h.Contains("IsOpen = false", StringComparison.Ordinal));
    }

    [Fact]
    public void Scanner_Flags_A_Generic_Flyout_Type()
    {
        // A generic type is ambiguous: CustomFlyout<T> *is* a flyout, List<Flyout> merely
        // holds one. Peeling straight to the type argument answers the second correctly and
        // the first backwards — the write lands on a real flyout and goes unseen. The
        // generic's own name has to be checked before its argument.
        const string source = """
            class Sample
            {
                void M()
                {
                    var a = new CustomFlyout<SomeElement> { Placement = n.Placement };
                    CustomFlyout<SomeElement> b = new() { Target = null, Placement = n.Placement };
                    var c = new Holder<SomeElement> { Placement = n.Placement };
                }
            }
            """;

        var hits = FindPlacementWrites("synthetic.cs", source)
            .Select(w => w.Text)
            .ToList();

        // The two generic flyouts, explicit and target-typed; the generic non-flyout is not
        // flagged, which is what proves the argument is still peeled when the outer name
        // isn't a flyout rather than everything generic being flagged wholesale.
        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.Contains("new CustomFlyout<SomeElement>", StringComparison.Ordinal));
        Assert.Contains(hits, h => h.Contains("Target = null", StringComparison.Ordinal));
    }

    [Fact]
    public void Scanner_Flags_A_Placement_Write_In_A_Nested_Member_Initializer()
    {
        // new Holder { ContextFlyout = { Placement = ... } } writes the property on an object
        // that already exists, so the receiver's type appears nowhere in the expression — the
        // initializer's parent is the outer assignment, not a creation. Unresolvable, and a
        // real write to a live control, so it has to fail closed like every other such shape.
        const string source = """
            class Sample
            {
                void M()
                {
                    var a = new Holder { ContextFlyout = { Placement = n.Placement } };
                    var b = new Holder { Anything = { Placement = n.Placement } };
                }
            }
            """;

        var hits = FindPlacementWrites("synthetic.cs", source)
            .Select(w => w.Text)
            .ToList();

        // Both, including the one whose member name gives no hint: the guard cannot tell what
        // it is writing to, and a false positive costs a one-line exemption while a false
        // negative costs the crash.
        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.Contains("ContextFlyout", StringComparison.Ordinal));
        Assert.Contains(hits, h => h.Contains("Anything", StringComparison.Ordinal));
    }

    [Fact]
    public void Scanner_Flags_A_Target_Typed_Initializer_It_Cannot_Type()
    {
        // When the declared type is unreadable (var, or assignment to an existing local) the
        // matcher fails closed. A false positive here is a one-line fix; a false negative is
        // the process-terminating crash coming back unnoticed.
        const string source = """
            class Sample
            {
                void M(WinUI.Flyout existing)
                {
                    existing = new() { Placement = n.Placement };
                }
            }
            """;

        Assert.Single(FindPlacementWrites("synthetic.cs", source));
    }

    [Fact]
    public void Scanner_Attributes_Each_Write_To_Its_Enclosing_Method()
    {
        // Both the bypass failure message and the positive-routing test key off the enclosing
        // method name, so prove two identical writes in one file are attributed to the method
        // they actually sit in — not to the file, and not to "<none>".
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

        Assert.True(
            byMethod.Count == 2,
            "Each write must be attributed to its own enclosing method — the bypass failure " +
            $"message and {nameof(Every_Flyout_Site_Routes_Through_The_Choke_Point)} both key " +
            $"off that name. Got: [{string.Join(", ", byMethod.Keys.OrderBy(k => k, StringComparer.Ordinal))}].");
        Assert.Contains("new WinUI.CommandBarFlyout", byMethod["MountCommandBarFlyout"].Text, StringComparison.Ordinal);
        Assert.Contains("new WinUI.Flyout", byMethod["MountFlyout"].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Scanner_Finds_Choke_Point_Calls_Regardless_Of_Qualification()
    {
        // Anti-vacuity for the positive-routing test: a call matcher that missed the shape
        // actually used in src/Reactor would turn that test into a permanent failure rather
        // than a silent pass, but a matcher that is too loose is the real risk — prove it
        // ignores same-named Apply calls on other owners.
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

        Assert.True(
            calls.Count == 2,
            "The choke-point matcher must accept both qualification forms and reject " +
            $"same-named Apply calls on other owners, or {nameof(Every_Flyout_Site_Routes_Through_The_Choke_Point)} " +
            $"passes vacuously. Got: [{string.Join(", ", calls.Select(c => c.Method))}].");
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
        => CSharpSyntaxTree.ParseText(source)
            .GetCompilationUnitRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(IsChokePointCall)
            .Select(invocation => new ChokePointCall(
                file,
                invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                EnclosingMethodName(invocation)));

    /// <summary>
    /// A call to <c>FlyoutPlacement.Apply(...)</c>, bare or namespace-qualified — including
    /// through a <i>namespace</i> alias, since the leaf of <c>Alias.FlyoutPlacement.Apply</c>
    /// is still the type name.
    /// </summary>
    /// <remarks>
    /// A <i>type</i> alias (<c>using FP = ...FlyoutPlacement;</c> then <c>FP.Apply(...)</c>)
    /// does not match: the leaf is <c>FP</c>, and resolving it needs a semantic model this
    /// scan deliberately does without. For a site already in
    /// <see cref="PlacementApplyingMethods"/> that fails loudly — it drops out of the routed
    /// set and <see cref="Every_Flyout_Site_Routes_Through_The_Choke_Point"/> reports it as
    /// missing. A brand-new site introduced that way would be invisible to both halves of
    /// the invariant, which is one of the symbol-resolution gaps issue #964 tracks.
    /// </remarks>
    private static bool IsChokePointCall(InvocationExpressionSyntax invocation)
        => invocation.Expression is MemberAccessExpressionSyntax
           { Name.Identifier.Text: "Apply", Expression: var owner }
           && LeafName(owner) == nameof(FlyoutPlacement);

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
    /// <see cref="Every_Flyout_Site_Routes_Through_The_Choke_Point"/> matches against — a
    /// stable identifier rather than a file or a line number.
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

        return initializer.Parent switch
        {
            // new WinUI.Flyout { Placement = ... }
            ObjectCreationExpressionSyntax creation => DenotesFlyout(creation.Type) is not false,
            // WinUI.CommandBarFlyout f = new() { Placement = ... } — the type lives on the
            // declaration, not the creation. Unresolvable shapes (an assignment to an
            // existing local, say) are treated as writes: a false positive is a loud,
            // one-line fix, a false negative is the process-terminating crash coming back.
            ImplicitObjectCreationExpressionSyntax implicitCreation => DeclaredDenotesFlyout(implicitCreation) is not false,
            // new Holder { ContextFlyout = { Placement = ... } } — a nested member initializer
            // writes the property on an object that already exists, so the receiver's type is
            // nowhere in this expression. Nothing syntactic can resolve it, and it is a real
            // write to a live control, so it fails closed like every other unresolvable shape.
            AssignmentExpressionSyntax => true,
            _ => false,
        };
    }

    /// <summary>
    /// Whether the type a target-typed <c>new()</c> is being converted to denotes a flyout,
    /// read from the declaration, return type, or cast that supplies it;
    /// <see langword="null"/> when no syntactic answer exists (assignment to an existing
    /// local, a lambda body, <c>var</c>).
    /// </summary>
    private static bool? DeclaredDenotesFlyout(ImplicitObjectCreationExpressionSyntax creation)
    {
        for (SyntaxNode n = creation; n.Parent is not null; n = n.Parent)
        {
            switch (n.Parent)
            {
                case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax decl } }:
                    return decl.Type.IsVar ? null : DenotesFlyout(decl.Type);
                case EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax prop }:
                    return DenotesFlyout(prop.Type);
                case CastExpressionSyntax cast:
                    return DenotesFlyout(cast.Type);
                case ReturnStatementSyntax or ArrowExpressionClauseSyntax:
                    return EnclosingReturnDenotesFlyout(n.Parent);
            }

            if (n.Parent is StatementSyntax or MemberDeclarationSyntax) break;
        }

        return null;
    }

    private static bool? EnclosingReturnDenotesFlyout(SyntaxNode node)
    {
        for (SyntaxNode? n = node; n is not null; n = n.Parent)
        {
            switch (n)
            {
                // A lambda's target type is not syntactically knowable — give up rather
                // than pick up the enclosing method's return type by accident.
                case AnonymousFunctionExpressionSyntax: return null;
                case MethodDeclarationSyntax method: return DenotesFlyout(method.ReturnType);
                case LocalFunctionStatementSyntax local: return DenotesFlyout(local.ReturnType);
                case PropertyDeclarationSyntax property: return DenotesFlyout(property.Type);
                case ConversionOperatorDeclarationSyntax conversion: return DenotesFlyout(conversion.Type);
                case OperatorDeclarationSyntax op: return DenotesFlyout(op.ReturnType);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the type syntax denotes a flyout — either directly, or as the element type of
    /// a collection of them; <see langword="null"/> when no unambiguous syntactic answer
    /// exists, so every caller fails closed on it.
    /// </summary>
    /// <remarks>
    /// This answers the question the callers actually ask. An earlier revision returned the
    /// leaf name and let each caller apply the suffix test, which cannot work: a single type
    /// argument is ambiguous between the flyout itself (<c>CustomFlyout&lt;T&gt;</c>) and the
    /// collection holding one (<c>List&lt;Flyout&gt;</c>), and only a check that sees both
    /// names can tell them apart. Every wrapper peeled here is otherwise a shape that yields
    /// a name that is non-null but wrong — <c>"CommandBarFlyout?"</c>, <c>"WinUI.Flyout[]"</c>,
    /// <c>"List"</c> — and so fails the suffix test while still slipping past the fail-closed
    /// arm. A guard whose job is to fail closed must not have a shape that fails open.
    /// </remarks>
    private static bool? DenotesFlyout(TypeSyntax type) => type switch
    {
        NullableTypeSyntax nullable => DenotesFlyout(nullable.ElementType),
        ArrayTypeSyntax array => DenotesFlyout(array.ElementType),
        // Its own name first: a generic flyout is still a flyout. Only once that is ruled out
        // is the single type argument the interesting one, and only one argument is
        // unambiguous — two or more, and there is no guessing which the initializer targets.
        // Peeling at all, rather than always giving up, is what keeps the guard off Reactor's
        // own element records when they are held in a collection, an ordinary DSL shape.
        GenericNameSyntax generic => IsFlyoutName(generic.Identifier.Text) ? true
            : generic.TypeArgumentList.Arguments is [var only] ? DenotesFlyout(only)
            : null,
        // Recurse rather than test Right's identifier directly, so the right-hand side gets
        // the same treatment (Some.Namespace.List<WinUI.Flyout> denotes a flyout).
        QualifiedNameSyntax qualified => DenotesFlyout(qualified.Right),
        SimpleNameSyntax simple => IsFlyoutName(simple.Identifier.Text),
        _ => null,
    };

    private static bool IsFlyoutName(string name) => name.EndsWith("Flyout", StringComparison.Ordinal);

    private static string Flatten(AssignmentExpressionSyntax assignment)
        => Flatten(assignment.Parent switch
        {
            // new SomeFlyout { Placement = ... } — report the whole creation, so the failure
            // message names the type rather than just the property.
            InitializerExpressionSyntax { Parent: BaseObjectCreationExpressionSyntax creation } => creation.ToString(),
            // ContextFlyout = { Placement = ... } — report the outer assignment, so the
            // message names the member being written. On its own the inner assignment reads
            // "Placement = ..." and says nothing about what it lands on.
            InitializerExpressionSyntax { Parent: AssignmentExpressionSyntax outer } => outer.ToString(),
            _ => assignment.ToString(),
        });

    private static string Flatten(string text)
    {
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 120 ? text : text[..120] + "…";
    }
}

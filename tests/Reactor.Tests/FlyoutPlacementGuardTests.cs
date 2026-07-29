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

    [Fact]
    public void No_Flyout_Placement_Write_Bypasses_FlyoutPlacement()
    {
        var writes = ScanCoreForFlyoutPlacementWrites();

        var bypasses = writes
            .Where(w => !string.Equals(Path.GetFileName(w.File), HelperFileName, StringComparison.Ordinal))
            .Select(w => $"{Path.GetFileName(w.File)}({w.Line}): {w.Text}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            bypasses.Count == 0,
            "These sites write FlyoutBase.Placement directly instead of routing through " +
            $"FlyoutPlacement.Apply, which lets FlyoutPlacementMode.Auto reach WinUI's validator " +
            $"and terminate the process when the flyout is shown: [{string.Join("; ", bypasses)}]");
    }

    [Fact]
    public void Scanner_Detects_The_Write_Inside_FlyoutPlacement()
    {
        // Anti-vacuity: a detector that never matches anything would make the test above
        // pass unconditionally. Prove it finds the one legitimate write.
        var writes = ScanCoreForFlyoutPlacementWrites();

        var inHelper = writes
            .Where(w => string.Equals(Path.GetFileName(w.File), HelperFileName, StringComparison.Ordinal))
            .ToList();

        var single = Assert.Single(inHelper);
        Assert.Contains("Placement", single.Text, StringComparison.Ordinal);
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
        // Anti-vacuity, part 3: prove the matcher recognizes both write shapes the fix
        // removed — a member assignment and a flyout object initializer.
        const string source = """
            class Sample
            {
                void M(Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase existing)
                {
                    existing.Placement = Placement;
                    var f = new WinUI.Flyout { Content = null, Placement = Placement };
                    var e = new SomeElement { Placement = Placement };
                    var t = new TeachingTip { PreferredPlacement = Placement };
                }
            }
            """;

        var hits = FindPlacementWrites("synthetic.cs", source)
            .Select(w => w.Text)
            .ToList();

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.StartsWith("existing.Placement", StringComparison.Ordinal));
        Assert.Contains(hits, h => h.Contains("new WinUI.Flyout", StringComparison.Ordinal));
    }

    // ── scanning helpers ────────────────────────────────────────────

    private readonly record struct PlacementWrite(string File, int Line, string Text);

    private static List<string> CoreSourceFiles()
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

    private static List<PlacementWrite> ScanCoreForFlyoutPlacementWrites()
    {
        var writes = new List<PlacementWrite>();
        foreach (var file in CoreSourceFiles())
            writes.AddRange(FindPlacementWrites(file, File.ReadAllText(file)));
        return writes;
    }

    /// <summary>
    /// Finds writes that land on a live WinUI <c>FlyoutBase.Placement</c> DP:
    /// <list type="bullet">
    /// <item><c>receiver.Placement = ...</c> — Reactor element records are <c>init</c>-only,
    /// so a member assignment can only target a mutable control.</item>
    /// <item><c>new ...Flyout { Placement = ... }</c> — object initializers on a WinUI
    /// flyout type. Element records are named <c>...Element</c> and the DSL builds them
    /// with target-typed <c>new(...)</c>, so neither is matched.</item>
    /// </list>
    /// </summary>
    private static IEnumerable<PlacementWrite> FindPlacementWrites(string file, string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetCompilationUnitRoot();

        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)) continue;

            bool isPlacementWrite = assignment.Left switch
            {
                // receiver.Placement = ...
                MemberAccessExpressionSyntax { Name.Identifier.Text: "Placement" } => true,
                // { Placement = ... } inside new SomethingFlyout { ... }
                IdentifierNameSyntax { Identifier.Text: "Placement" } =>
                    IsFlyoutObjectInitializer(assignment),
                _ => false,
            };
            if (!isPlacementWrite) continue;

            var line = assignment.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            yield return new PlacementWrite(file, line, Flatten(assignment));
        }
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
    {
        var text = assignment.Parent is InitializerExpressionSyntax { Parent: ObjectCreationExpressionSyntax creation }
            ? creation.ToString()
            : assignment.ToString();
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 120 ? text : text[..120] + "…";
    }
}

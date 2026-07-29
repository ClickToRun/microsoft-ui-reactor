using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.UI.Reactor.Analyzers;
using Microsoft.UI.Reactor.Cli.Pack;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.AnalyzerTests;

/// <summary>
/// Keeps <see cref="ModifierTable"/> honest.
/// <para>
/// <see cref="PoolResetSetConsistencyTests"/> already guards the pool-reset half of the
/// table against <c>ElementPool.CleanElement</c> drift. These tests cover the other half —
/// the "a modifier exists, prefer it over <c>.Set</c>" entries — where the failure mode is
/// different: the table silently falls behind the DSL as modifiers are added, which is
/// exactly how the original 12-entry list went stale while ~144 <c>.Set</c> sites with
/// modifiers went undiagnosed.
/// </para>
/// <para>
/// Two classes of guarantee:
/// </para>
/// <list type="number">
/// <item><description><b>Integrity</b> — every entry names a modifier that really exists,
/// and every element type really declares it. A wrong entry makes
/// <c>PoolResetSetCodeFix</c> emit code that does not compile.</description></item>
/// <item><description><b>Staleness</b> — a newly added generic modifier whose name matches
/// a settable WinUI dependency property must be classified, either into the table or into
/// <see cref="ModifierTable.DeliberatelyExcluded"/> with a reason. Adding a modifier
/// therefore forces a decision instead of silently widening the gap.</description></item>
/// </list>
/// </summary>
public class ModifierTableIntegrityTests
{
    // ── Integrity ────────────────────────────────────────────────────────────

    [Fact]
    public void Every_Entry_Names_A_Modifier_That_Exists()
    {
        var generic = ReadGenericModifierNames();
        var typeSpecific = ReadTypeSpecificModifiers();

        var broken = new List<string>();
        foreach (var (prop, info) in ModifierTable.Properties)
        {
            var exists = generic.Contains(info.Modifier)
                || typeSpecific.ContainsKey(info.Modifier);
            if (!exists)
                broken.Add($"{prop} -> .{info.Modifier}()");
        }

        Assert.True(
            broken.Count == 0,
            "These ModifierTable entries name a modifier that does not exist in " +
            "ElementExtensions*.cs, so PoolResetSetCodeFix would rewrite '.Set(...)' into a " +
            $"call that does not compile: [{string.Join(", ", broken)}]. " +
            "Fix the modifier name, or drop the entry.");
    }

    [Fact]
    public void Every_TypeSpecific_Entry_Lists_Only_Element_Types_That_Declare_It()
    {
        // This is the assertion that keeps the code fix sound for the type-specific half.
        // `.TextWrapping(...)` compiles on TextBlockElement but not on, say, BorderElement —
        // so if the listed element types drift from what ElementExtensions actually declares,
        // the fix silently starts producing uncompilable rewrites on the extra types.
        var typeSpecific = ReadTypeSpecificModifiers();

        var wrong = new List<string>();
        foreach (var (prop, info) in ModifierTable.Properties)
        {
            if (info.ElementTypes is null)
                continue;

            if (!typeSpecific.TryGetValue(info.Modifier, out var declaredOn))
            {
                wrong.Add($"{prop}: '.{info.Modifier}()' has no type-specific overloads at all");
                continue;
            }

            foreach (var listed in info.ElementTypes)
            {
                if (!declaredOn.Contains(listed))
                    wrong.Add($"{prop}: '.{info.Modifier}()' is NOT declared on {listed}");
            }
        }

        Assert.True(
            wrong.Count == 0,
            "ModifierTable lists element types that do not declare the modifier: " +
            $"[{string.Join("; ", wrong)}]. The code fix would emit a call that does not " +
            "compile on those receivers.");
    }

    [Fact]
    public void TypeSpecific_Entries_Do_Not_Omit_Element_Types_That_Declare_The_Modifier()
    {
        // The inverse of the previous test. Omissions are not unsafe — a missing element
        // type only costs a diagnostic — but they are silent, and they accumulate. Anything
        // deliberately left out (inline RichText* run/paragraph types, which are not
        // Elements and have no '.Set') is filtered rather than exempted case by case.
        var typeSpecific = ReadTypeSpecificModifiers();

        var missing = new List<string>();
        foreach (var (prop, info) in ModifierTable.Properties)
        {
            if (info.ElementTypes is null)
                continue;
            if (!typeSpecific.TryGetValue(info.Modifier, out var declaredOn))
                continue;

            foreach (var declared in declaredOn)
            {
                // Only element records participate in the '.Set' DSL; the inline
                // RichTextParagraph / RichTextRun / RichTextHyperlink types do not.
                if (!declared.EndsWith("Element", StringComparison.Ordinal))
                    continue;
                if (!info.ElementTypes.Contains(declared))
                    missing.Add($"{prop}: {declared} declares '.{info.Modifier}()' but is not listed");
            }
        }

        Assert.True(
            missing.Count == 0,
            "ModifierTable omits element types that declare the modifier, so REACTOR_MOD_002 " +
            $"will not fire on those receivers: [{string.Join("; ", missing)}]. " +
            "Add them to the entry's elementTypes list.");
    }

    [Fact]
    public void No_Property_Is_Both_Mapped_And_Excluded()
    {
        var overlap = ModifierTable.Properties.Keys
            .Where(ModifierTable.DeliberatelyExcluded.ContainsKey)
            .ToList();

        Assert.True(
            overlap.Count == 0,
            "These properties appear in BOTH ModifierTable.Properties and " +
            $"DeliberatelyExcluded, which is contradictory: [{string.Join(", ", overlap)}].");
    }

    [Fact]
    public void Every_Exclusion_Carries_A_Reason()
    {
        var blank = ModifierTable.DeliberatelyExcluded
            .Where(kvp => string.IsNullOrWhiteSpace(kvp.Value))
            .Select(kvp => kvp.Key)
            .ToList();

        Assert.True(
            blank.Count == 0,
            $"These exclusions have no documented reason: [{string.Join(", ", blank)}]. " +
            "An unexplained exclusion is indistinguishable from an oversight.");
    }

    // ── Staleness ────────────────────────────────────────────────────────────

    [Fact]
    public void Every_Generic_Modifier_Matching_A_Settable_WinUI_Property_Is_Classified()
    {
        // The load-bearing test. When someone adds `public static T Foo<T>(this T el, ...)`
        // and WinUI has a settable `Foo` property, `.Set(x => x.Foo = v)` becomes
        // rewritable — and without this test nobody would notice that the analyzer does not
        // know about it. Forcing a choice between "map it" and "exclude it with a reason"
        // is what stops the table drifting behind the DSL.
        var candidates = ReadGenericModifierNames()
            .Where(IsSettableWinUiProperty)
            .Where(name => !ModifierTable.Properties.ContainsKey(name))
            .Where(name => !ModifierTable.DeliberatelyExcluded.ContainsKey(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            candidates.Count == 0,
            "These generic modifiers match a settable WinUI property but are neither mapped " +
            "in ModifierTable.Properties nor listed in ModifierTable.DeliberatelyExcluded: " +
            $"[{string.Join(", ", candidates)}]. " +
            "Either add a mapping (so REACTOR_MOD_002 suggests the modifier for " +
            "'.Set(x => x.PROP = ...)'), or add an exclusion explaining why the modifier is " +
            "not an equivalent replacement.");
    }

    [Fact]
    public void Every_Type_Specific_Modifier_Matching_A_Settable_WinUI_Property_Is_Classified()
    {
        // The generic test above cannot see a modifier that only exists in the type-specific
        // shape — including this table's own RichTextBlockElement font overloads. A property
        // whose ONLY modifier is type-specific would slip past unclassified, which is the
        // exact gap that let `.FontSize` on a RichTextBlock go unsuggested. Same forced
        // choice between "map it" and "exclude it with a reason", other declaration shape.
        var candidates = ReadTypeSpecificModifiers().Keys
            .Where(IsSettableWinUiProperty)
            .Where(name => !ModifierTable.Properties.ContainsKey(name))
            .Where(name => !ModifierTable.DeliberatelyExcluded.ContainsKey(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            candidates.Count == 0,
            "These type-specific modifiers match a settable WinUI property but are neither " +
            "mapped in ModifierTable.Properties nor listed in ModifierTable.DeliberatelyExcluded: " +
            $"[{string.Join(", ", candidates)}]. " +
            "Either add a mapping with the declaring element types (so REACTOR_MOD_002 " +
            "suggests the modifier for '.Set(x => x.PROP = ...)' on those receivers), or add " +
            "an exclusion explaining why the modifier is not an equivalent replacement.");
    }

    /// <summary>
    /// True when one of the WinUI base types Reactor's modifiers target declares a public
    /// settable instance property with this name — i.e. a name a <c>.Set</c> lambda could
    /// plausibly assign. Reflection only reads metadata; no WinUI object is constructed, so
    /// this is safe in the headless test host.
    /// </summary>
    private static bool IsSettableWinUiProperty(string name) =>
        HasSettableProperty<Microsoft.UI.Xaml.UIElement>(name)
        || HasSettableProperty<Microsoft.UI.Xaml.FrameworkElement>(name)
        || HasSettableProperty<Microsoft.UI.Xaml.Controls.Control>(name)
        || HasSettableProperty<Microsoft.UI.Xaml.Controls.ContentControl>(name)
        || HasSettableProperty<Microsoft.UI.Xaml.Controls.Panel>(name)
        || HasSettableProperty<Microsoft.UI.Xaml.Controls.Border>(name)
        || HasSettableProperty<Microsoft.UI.Xaml.Controls.TextBlock>(name);

    // Generic + annotated rather than iterating a Type[]: the trim analyzer cannot see
    // through an array element to the reflection target, so the array form trips IL2075.
    private static bool HasSettableProperty<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(string name)
    {
        var prop = typeof(T).GetProperty(
            name,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
        return prop is not null && prop.CanWrite;
    }

    // ── Drift against the runtime authority ──────────────────────────────────

    [Fact]
    public void Every_ControlGate_Matches_The_Types_ApplyModifiers_Writes_To()
    {
        // ControlGate hand-copies allow-lists that Reconciler.ApplyModifiers encodes
        // independently, and the two failure directions are both silent: a gate that is too
        // WIDE makes the analyzer suggest a modifier the reconciler never writes (the rewrite
        // compiles and does nothing — the ValueList/CellComponent regression), while one that
        // is too NARROW just drops diagnostics. Nothing else notices either, so pin the copy
        // to its source.
        var actualGates = ReadApplyModifierControlGates();
        var problems = new List<string>();

        foreach (var (prop, info) in ModifierTable.Properties)
        {
            if (info.ControlGate is not { } declared)
                continue;

            if (!actualGates.TryGetValue(prop, out var actual))
            {
                problems.Add(
                    $"{prop}: ModifierTable declares a control gate [{string.Join("|", declared)}], " +
                    "but ApplyModifiers has no 'fe is <Type>' test guarded by 'm." + prop + "'");
                continue;
            }

            if (!actual.SetEquals(declared))
            {
                problems.Add(
                    $"{prop}: ModifierTable says [{string.Join("|", declared.OrderBy(t => t, StringComparer.Ordinal))}] " +
                    $"but ApplyModifiers writes to [{string.Join("|", actual.OrderBy(t => t, StringComparer.Ordinal))}]");
            }
        }

        Assert.True(
            problems.Count == 0,
            "ModifierTable.ControlGate has drifted from Reconciler.ApplyModifiers, which is the " +
            "runtime authority for which controls a modifier is actually written to:\n  " +
            string.Join("\n  ", problems));
    }

    /// <summary>
    /// Modifier property name → the WinUI type names <c>ApplyModifiers</c> actually writes it
    /// to, read out of <c>Reconciler.cs</c>.
    /// </summary>
    /// <remarks>
    /// Parsed with Roslyn rather than matched with a regex: the gate lives in a type-test
    /// pattern nested inside the <c>if (m.PROP…)</c> that guards it, and tying the two
    /// together textually would be guesswork about brace depth. Walking the syntax tree makes
    /// the containment relationship exact, so this test fails on real drift instead of on
    /// reformatting.
    /// </remarks>
    private static Dictionary<string, HashSet<string>> ReadApplyModifierControlGates()
    {
        var root = RepoRootFinder.FindRepoRoot();
        Assert.NotNull(root);
        var file = Path.Join(root!, "src", "Reactor", "Core", "Reconciler.cs");
        Assert.True(File.Exists(file), $"Reconciler.cs not found at {file}");

        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(File.ReadAllText(file));
        var methods = tree.GetRoot()
            .DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == "ApplyModifiers")
            .ToList();

        Assert.True(methods.Count > 0, "No ApplyModifiers method found in Reconciler.cs");

        var gates = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var method in methods)
        {
            // Padding and BorderThickness are computed into a local first (to overlay the
            // BiDi-aware inline variants), so the guard reads `resolvedPadding`, not
            // `m.Padding`. Map those locals back to the modifier they were seeded from.
            var localToProperty = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var declarator in method.DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax>())
            {
                if (declarator.Initializer is null)
                    continue;
                var seed = ModifierPropertyNames(declarator.Initializer.Value).FirstOrDefault();
                if (seed is not null)
                    localToProperty[declarator.Identifier.Text] = seed;
            }

            foreach (var ifStatement in method.DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.IfStatementSyntax>())
            {
                // Which modifier does this branch guard? `m.Background is not null`,
                // `resolvedPadding.HasValue`, `oldM?.FontSize.HasValue == true`, …
                var guarded = ModifierPropertyNames(ifStatement.Condition)
                    .Concat(ifStatement.Condition
                        .DescendantNodesAndSelf()
                        .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax>()
                        .Select(id => localToProperty.TryGetValue(id.Identifier.Text, out var mapped) ? mapped : null)
                        .Where(name => name is not null)!)
                    .FirstOrDefault();

                if (guarded is null)
                    continue;

                // Type tests on the FrameworkElement inside this branch (and its else clauses)
                // are the gate: `fe is WinUI.Control padCtrl`.
                foreach (var pattern in ifStatement.DescendantNodes()
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.IsPatternExpressionSyntax>())
                {
                    if (pattern.Expression is not Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax { Identifier.Text: "fe" })
                        continue;
                    if (pattern.Pattern is not Microsoft.CodeAnalysis.CSharp.Syntax.DeclarationPatternSyntax declaration)
                        continue;

                    var typeName = declaration.Type switch
                    {
                        Microsoft.CodeAnalysis.CSharp.Syntax.QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
                        Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax simple => simple.Identifier.Text,
                        _ => null,
                    };
                    if (typeName is null)
                        continue;

                    if (!gates.TryGetValue(guarded, out var set))
                        gates[guarded] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(typeName);
                }
            }
        }

        return gates;
    }

    /// <summary>
    /// Modifier property names read off the new or old modifier bag inside
    /// <paramref name="node"/>, in source order — both <c>m.Foo</c> / <c>oldM.Foo</c> and the
    /// conditional <c>oldM?.Foo</c> form.
    /// </summary>
    private static IEnumerable<string> ModifierPropertyNames(Microsoft.CodeAnalysis.SyntaxNode node)
    {
        foreach (var descendant in node.DescendantNodesAndSelf())
        {
            switch (descendant)
            {
                case Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax access
                    when access.Expression is Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax bag
                        && (bag.Identifier.Text == "m" || bag.Identifier.Text == "oldM"):
                    yield return access.Name.Identifier.Text;
                    break;

                // `oldM?.Padding` — the name hangs off a member binding, and the receiver is
                // on the enclosing conditional access.
                case Microsoft.CodeAnalysis.CSharp.Syntax.ConditionalAccessExpressionSyntax conditional
                    when conditional.Expression is Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax bag
                        && (bag.Identifier.Text == "m" || bag.Identifier.Text == "oldM"):
                {
                    var binding = conditional.WhenNotNull
                        .DescendantNodesAndSelf()
                        .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MemberBindingExpressionSyntax>()
                        .FirstOrDefault();
                    if (binding is not null)
                        yield return binding.Name.Identifier.Text;
                    break;
                }
            }
        }
    }

    // ── Source-scanning helpers ──────────────────────────────────────────────
    //
    // Source scanning rather than reflection over ElementExtensions, to match the approach
    // already proven in PoolResetSetConsistencyTests and to distinguish the generic
    // `T Foo<T>(this T el, ...)` shape from the type-specific overloads — a distinction
    // reflection over extension methods makes awkward.

    private static HashSet<string> ReadGenericModifierNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in ReadElementExtensionSources())
        {
            foreach (Match m in Regex.Matches(
                source, @"public\s+static\s+T\s+(\w+)\s*<T>\s*\(\s*this\s+T\s+\w+"))
            {
                names.Add(m.Groups[1].Value);
            }
        }
        return names;
    }

    /// <summary>
    /// Modifier method name → the element types declaring a type-specific overload, i.e.
    /// <c>public static XxxElement Foo(this XxxElement el, ...)</c>.
    /// </summary>
    private static Dictionary<string, HashSet<string>> ReadTypeSpecificModifiers()
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var source in ReadElementExtensionSources())
        {
            foreach (Match m in Regex.Matches(
                source, @"public\s+static\s+(\w+)\s+(\w+)\s*\(\s*this\s+(\w+)\s+\w+"))
            {
                var returnType = m.Groups[1].Value;
                var method = m.Groups[2].Value;
                var receiver = m.Groups[3].Value;

                // A fluent modifier returns its receiver type. This also filters out the
                // generic form, whose receiver is the type parameter `T`.
                if (receiver == "T" || returnType != receiver)
                    continue;

                if (!map.TryGetValue(method, out var set))
                    map[method] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(receiver);
            }
        }
        return map;
    }

    private static IEnumerable<string> ReadElementExtensionSources()
    {
        var root = RepoRootFinder.FindRepoRoot();
        Assert.NotNull(root);
        var dir = Path.Join(root!, "src", "Reactor", "Elements");
        Assert.True(Directory.Exists(dir), $"Elements directory not found at {dir}");

        var files = Directory.GetFiles(dir, "ElementExtensions*.cs");
        Assert.True(files.Length > 0, $"No ElementExtensions*.cs found in {dir}");

        foreach (var file in files)
            yield return File.ReadAllText(file);
    }
}

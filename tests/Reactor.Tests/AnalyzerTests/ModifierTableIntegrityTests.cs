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

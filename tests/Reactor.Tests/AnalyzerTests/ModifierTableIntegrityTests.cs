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

            foreach (var listed in info.ElementTypes.Where(listed => !declaredOn.Contains(listed)))
                wrong.Add($"{prop}: '.{info.Modifier}()' is NOT declared on {listed}");
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

            // Only element records participate in the '.Set' DSL; the inline
            // RichTextParagraph / RichTextRun / RichTextHyperlink types do not.
            foreach (var declared in declaredOn.Where(declared =>
                declared.EndsWith("Element", StringComparison.Ordinal)
                && !info.ElementTypes.Contains(declared)))
            {
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

    // ── Attached-property integrity ──────────────────────────────────────────

    /// <summary>
    /// Owner types <see cref="ModifierTable.AttachedProperties"/> is allowed to name, with
    /// the reflection probes used to verify each entry against the real type.
    /// </summary>
    /// <remarks>
    /// Hand-listed rather than resolved from the entry's namespace string via
    /// <c>Type.GetType</c>, so the checks stay statically analyzable (IL2057) and so adding a
    /// new owner to the table is a deliberate two-place edit rather than a silent widening.
    /// Reflection only reads metadata; no WinUI object is constructed.
    /// </remarks>
    private static readonly Dictionary<string, (Func<string, bool> HasSetter, Func<string, bool> HasDependencyProperty, Func<string, Type?> SetterValueType)>
        KnownAttachedOwners = new(StringComparer.Ordinal)
        {
            ["Microsoft.UI.Xaml.Automation.AutomationProperties"] = (
                setter => HasStaticTwoArgMethod(typeof(Microsoft.UI.Xaml.Automation.AutomationProperties), setter),
                prop => HasDependencyPropertyField(typeof(Microsoft.UI.Xaml.Automation.AutomationProperties), prop),
                setter => StaticTwoArgValueType(typeof(Microsoft.UI.Xaml.Automation.AutomationProperties), setter)),
            ["Microsoft.UI.Xaml.Controls.ToolTipService"] = (
                setter => HasStaticTwoArgMethod(typeof(Microsoft.UI.Xaml.Controls.ToolTipService), setter),
                prop => HasDependencyPropertyField(typeof(Microsoft.UI.Xaml.Controls.ToolTipService), prop),
                setter => StaticTwoArgValueType(typeof(Microsoft.UI.Xaml.Controls.ToolTipService), setter)),
            ["Microsoft.UI.Xaml.Controls.TitleBar"] = (
                setter => HasStaticTwoArgMethod(typeof(Microsoft.UI.Xaml.Controls.TitleBar), setter),
                prop => HasDependencyPropertyField(typeof(Microsoft.UI.Xaml.Controls.TitleBar), prop),
                setter => StaticTwoArgValueType(typeof(Microsoft.UI.Xaml.Controls.TitleBar), setter)),
            ["Microsoft.UI.Reactor.Layout.FlexPanel"] = (
                setter => HasStaticTwoArgMethod(typeof(Microsoft.UI.Reactor.Layout.FlexPanel), setter),
                prop => HasDependencyPropertyField(typeof(Microsoft.UI.Reactor.Layout.FlexPanel), prop),
                setter => StaticTwoArgValueType(typeof(Microsoft.UI.Reactor.Layout.FlexPanel), setter)),
        };

    [Fact]
    public void Every_Attached_Entry_Matches_A_Real_Setter_And_DependencyProperty()
    {
        // The attached analog of Every_Entry_Names_A_Modifier_That_Exists, and stricter
        // because an attached entry carries three names that can each be wrong independently:
        // the owner, the static setter the analyzer matches at the call site, and the
        // dependency property PoolResetSetConsistencyTests scans CleanElement for. A typo in
        // the setter makes the rule silently stop firing; a typo in the property makes the
        // consistency invariant pass vacuously.
        var broken = new List<string>();

        foreach (var (key, info) in ModifierTable.AttachedProperties)
        {
            Assert.Equal(info.Owner + "." + info.Property, key);

            var ownerKey = info.OwnerNamespace + "." + info.Owner;
            if (!KnownAttachedOwners.TryGetValue(ownerKey, out var probes))
            {
                broken.Add($"{key}: unknown owner type '{ownerKey}' — add it to KnownAttachedOwners");
                continue;
            }

            if (!probes.HasSetter(info.Setter))
                broken.Add($"{key}: '{ownerKey}.{info.Setter}(_, _)' does not exist");

            if (!probes.HasDependencyProperty(info.Property))
                broken.Add($"{key}: '{ownerKey}.{info.Property}Property' is not a DependencyProperty field");
        }

        Assert.True(
            broken.Count == 0,
            "These ModifierTable.AttachedProperties entries do not match the real type: " +
            $"[{string.Join("; ", broken)}].");
    }

    [Fact]
    public void Every_Attached_Entry_Names_A_Generic_Modifier_That_Exists()
    {
        // Same guarantee as the instance table's version — a wrong modifier name makes
        // PoolResetSetCodeFix emit a call that does not compile — but resolved by reflection
        // over the built assembly rather than by scanning ElementExtensions*.cs, because
        // .Flex(...) lives in FlexExtensions.cs, which that source glob does not cover.
        var broken = ModifierTable.AttachedProperties
            .Where(pair => !DeclaredGenericModifiers.Value.Contains(pair.Value.Modifier))
            .Select(pair => $"{pair.Key} -> .{pair.Value.Modifier}()")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            broken.Count == 0,
            "These attached entries name a modifier that no Reactor extension class declares " +
            "as 'public static T Name<T>(this T el, ...)', so the suggestion — and any code " +
            $"fix built from it — would not compile: [{string.Join(", ", broken)}].");
    }

    [Fact]
    public void No_Attached_Property_Is_Both_Mapped_And_Excluded()
    {
        var overlap = ModifierTable.AttachedProperties.Keys
            .Where(ModifierTable.DeliberatelyExcludedAttached.ContainsKey)
            .ToList();

        Assert.True(
            overlap.Count == 0,
            "These attached properties appear in BOTH ModifierTable.AttachedProperties and " +
            $"DeliberatelyExcludedAttached, which is contradictory: [{string.Join(", ", overlap)}].");
    }

    [Fact]
    public void Every_Attached_Exclusion_Carries_A_Reason()
    {
        var blank = ModifierTable.DeliberatelyExcludedAttached
            .Where(kvp => string.IsNullOrWhiteSpace(kvp.Value))
            .Select(kvp => kvp.Key)
            .ToList();

        Assert.True(
            blank.Count == 0,
            $"These attached exclusions have no documented reason: [{string.Join(", ", blank)}]. " +
            "An unexplained exclusion is indistinguishable from an oversight.");
    }

    [Fact]
    public void Attached_Setter_Lookup_Covers_Every_Entry()
    {
        // AttachedBySetter is what the analyzer actually queries; AttachedProperties is what
        // the consistency test scans. A property/setter pair that collapses to the same
        // Owner.Setter key on two entries would silently drop one of them from the rule.
        Assert.Equal(ModifierTable.AttachedProperties.Count, ModifierTable.AttachedBySetter.Count);

        foreach (var (key, info) in ModifierTable.AttachedProperties)
        {
            Assert.True(
                ModifierTable.AttachedBySetter.TryGetValue(info.Owner + "." + info.Setter, out var viaSetter),
                $"'{key}' is missing from ModifierTable.AttachedBySetter.");
            Assert.Same(info, viaSetter);
        }
    }

    [Fact]
    public void Every_Attached_Setter_Is_Named_After_Its_DependencyProperty()
    {
        // Without this, a row could pair one property with an unrelated setter of the same
        // arity — e.g. FullDescription + SetName — and every other check would still pass:
        // both names exist on AutomationProperties, and the generated stubs in
        // PoolResetSetConsistencyTests are built FROM the row, so they would agree with it.
        // The result would be a diagnostic that names the wrong property and a consistency
        // invariant that silently stops covering the real one.
        var divergent = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // FlexPanel prefixes the DP to avoid colliding with FrameworkElement.MinWidth /
            // MinHeight, but leaves the setters unprefixed.
            ["FlexPanel.FlexMinWidth"] = "SetMinWidth",
            ["FlexPanel.FlexMinHeight"] = "SetMinHeight",
        };

        var mismatched = ModifierTable.AttachedProperties
            .Where(pair => divergent.TryGetValue(pair.Key, out var expected)
                ? pair.Value.Setter != expected
                : pair.Value.Setter != "Set" + pair.Value.Property)
            .Select(pair => $"{pair.Key} -> {pair.Value.Setter}")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            mismatched.Count == 0,
            "These attached entries name a setter that does not correspond to their dependency " +
            $"property: [{string.Join(", ", mismatched)}]. Either fix the pairing, or — if the " +
            "owner really does name them differently — add the row to this test's 'divergent' " +
            "map with the reason.");
    }

    [Fact]
    public void Every_AutoFixable_Attached_Entry_Maps_One_To_One_Onto_Its_Modifier()
    {
        // The assertion behind AutoFix: true. PoolResetSetCodeFix passes the setter's single
        // value argument straight through to the modifier, so that is only sound when the
        // modifier really has a one-value overload whose parameter accepts the setter's value
        // type. Checked against the real DSL and the real owner type, so flipping any
        // AutoFix: false row to true fails here rather than shipping a rewrite that does not
        // compile — .PositionInSet takes two values, .Required() takes none, and .Flex(...)
        // takes eleven.
        var broken = new List<string>();

        foreach (var (key, info) in ModifierTable.AttachedProperties.Where(pair => pair.Value.AutoFix))
        {
            var ownerKey = info.OwnerNamespace + "." + info.Owner;
            if (!KnownAttachedOwners.TryGetValue(ownerKey, out var probes))
                continue; // Reported by Every_Attached_Entry_Matches_A_Real_Setter_And_DependencyProperty.

            var setterValueType = probes.SetterValueType(info.Setter);
            if (setterValueType is null)
                continue; // Same.

            // FixValueType is the documented escape for a setter typed more loosely than its
            // modifier: the analyzer refuses the fix unless the value really is that type, so
            // that — not the setter's parameter — is what the modifier must accept.
            var required = info.FixValueType;

            var accepted = ModifierValueTypes(info.Modifier);
            if (accepted.Count == 0)
            {
                broken.Add($"{key}: no '{info.Modifier}<T>(this T, value)' overload exists");
                continue;
            }

            var ok = accepted.Any(candidate => required is not null
                ? FullName(candidate) == required
                : AcceptsValueOfType(candidate, setterValueType));

            if (!ok)
            {
                var expected = required ?? FullName(setterValueType);
                broken.Add(
                    $"{key}: '{info.Modifier}' has no single-value overload accepting '{expected}' " +
                    $"(overloads accept: {string.Join(" | ", accepted.Select(FullName))})");
            }
        }

        Assert.True(
            broken.Count == 0,
            "These attached entries are marked AutoFix: true but the modifier does not line up " +
            $"1:1 with the setter, so the code fix would not compile: [{string.Join("; ", broken)}]. " +
            "Mark them AutoFix: false, or set fixValueType when the setter is merely typed more " +
            "loosely than the modifier.");
    }

    /// <summary>
    /// Value-parameter types of every <c>public static T Name&lt;T&gt;(this T el, X value)</c>
    /// overload — the single-value fluent shape the attached code fix rewrites into.
    /// </summary>
    private static List<Type> ModifierValueTypes(string modifier)
    {
        var types = new List<Type>();
        CollectModifierValueTypes(typeof(Microsoft.UI.Reactor.ElementExtensions), modifier, types);
        CollectModifierValueTypes(typeof(Microsoft.UI.Reactor.FlexExtensions), modifier, types);
        return types;
    }

    private static void CollectModifierValueTypes(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type type,
        string modifier,
        List<Type> types)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (!method.IsGenericMethodDefinition
                || !string.Equals(method.Name, modifier, StringComparison.Ordinal))
            {
                continue;
            }
            var typeArguments = method.GetGenericArguments();
            var parameters = method.GetParameters();
            if (typeArguments.Length != 1 || parameters.Length != 2)
                continue;
            if (parameters[0].ParameterType != typeArguments[0])
                continue;
            types.Add(parameters[1].ParameterType);
        }
    }

    /// <summary>
    /// True when a value of <paramref name="setterValueType"/> can be passed verbatim to a
    /// parameter of <paramref name="modifierParameterType"/>. Covers the identity case and the
    /// implicit <c>T</c> → <c>T?</c> lift (<c>TitleBar.SetIsDragRegion(_, bool)</c> →
    /// <c>.IsDragRegion(bool?)</c>), which reflection does not model as assignability.
    /// </summary>
    private static bool AcceptsValueOfType(Type modifierParameterType, Type setterValueType)
        => modifierParameterType == setterValueType
            || Nullable.GetUnderlyingType(modifierParameterType) == setterValueType
            || modifierParameterType.IsAssignableFrom(setterValueType);

    private static string FullName(Type type) => type.FullName ?? type.Name;

    /// <summary>
    /// Names of every <c>public static T Name&lt;T&gt;(this T el, ...)</c> modifier declared
    /// by Reactor's extension classes. Restricted to the classes that actually hold them so
    /// the reflection stays targeted (and trim-analyzable); a modifier added to a third class
    /// fails <see cref="Every_Attached_Entry_Names_A_Generic_Modifier_That_Exists"/> until
    /// that class is listed here.
    /// </summary>
    private static readonly Lazy<HashSet<string>> DeclaredGenericModifiers = new(() =>
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        CollectGenericModifiers(typeof(Microsoft.UI.Reactor.ElementExtensions), names);
        CollectGenericModifiers(typeof(Microsoft.UI.Reactor.FlexExtensions), names);
        return names;
    });

    private static void CollectGenericModifiers(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type type,
        HashSet<string> names)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (!method.IsGenericMethodDefinition)
                continue;
            var typeArguments = method.GetGenericArguments();
            var parameters = method.GetParameters();
            // The fluent shape: one type parameter, and the receiver is that parameter.
            if (typeArguments.Length != 1 || parameters.Length == 0)
                continue;
            if (parameters[0].ParameterType != typeArguments[0])
                continue;
            names.Add(method.Name);
        }
    }

    // Type parameter + annotation rather than a generic type argument: the extension classes
    // are static, and a static type cannot be used as a type argument (CS0718).
    private static bool HasStaticTwoArgMethod(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type type,
        string name)
        => StaticTwoArgValueType(type, name) is not null;

    /// <summary>
    /// The value (second) parameter type of the owner's <c>public static void Set&lt;X&gt;(target, value)</c>,
    /// or <c>null</c> when no such method exists.
    /// </summary>
    private static Type? StaticTwoArgValueType(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type type,
        string name)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (!string.Equals(method.Name, name, StringComparison.Ordinal))
                continue;
            var parameters = method.GetParameters();
            if (parameters.Length == 2)
                return parameters[1].ParameterType;
        }
        return null;
    }

    private static bool HasDependencyPropertyField(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields
            | DynamicallyAccessedMemberTypes.PublicProperties)] Type type,
        string propertyName)
    {
        const BindingFlags Flags =
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;

        // Reactor's own attached properties are `public static readonly DependencyProperty`
        // fields; the WinRT projection surfaces WinUI's as static *properties* instead. Accept
        // either — what matters is that the DP identifier CleanElement clears really exists
        // under this exact name.
        var field = type.GetField(propertyName + "Property", Flags);
        if (field is not null)
            return field.FieldType == typeof(Microsoft.UI.Xaml.DependencyProperty);

        var property = type.GetProperty(propertyName + "Property", Flags);
        return property is not null
            && property.PropertyType == typeof(Microsoft.UI.Xaml.DependencyProperty);
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
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax>()
                .Where(declarator => declarator.Initializer is not null))
            {
                var seed = ModifierPropertyNames(declarator.Initializer!.Value).FirstOrDefault();
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
                var typeNames = ifStatement.DescendantNodes()
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.IsPatternExpressionSyntax>()
                    .Where(pattern =>
                        pattern.Expression is Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax { Identifier.Text: "fe" }
                        && pattern.Pattern is Microsoft.CodeAnalysis.CSharp.Syntax.DeclarationPatternSyntax)
                    .Select(pattern => ((Microsoft.CodeAnalysis.CSharp.Syntax.DeclarationPatternSyntax)pattern.Pattern).Type switch
                    {
                        Microsoft.CodeAnalysis.CSharp.Syntax.QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
                        Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax simple => simple.Identifier.Text,
                        _ => null,
                    })
                    .Where(typeName => typeName is not null);

                foreach (var typeName in typeNames)
                {
                    if (!gates.TryGetValue(guarded, out var set))
                        gates[guarded] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(typeName!);
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

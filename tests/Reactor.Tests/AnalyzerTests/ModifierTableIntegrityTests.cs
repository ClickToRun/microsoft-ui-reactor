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
    /// The reverse of <see cref="Every_ControlGate_Matches_The_Types_ApplyModifiers_Writes_To"/>:
    /// every control gate that exists in <c>ApplyModifiers</c> must be accounted for here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// That test iterates <see cref="ModifierTable.Properties"/> and skips entries whose
    /// <see cref="ModifierInfo.ControlGate"/> is <see langword="null"/>, so a gate the reconciler
    /// enforces but the table does not name is invisible to it — including a brand-new type-gated
    /// modifier, and the <c>IsEnabled</c> / <c>H|VContentAlignment</c> trio whose gate is
    /// deliberately left null for the <c>.Set</c> direction.
    /// </para>
    /// <para>
    /// That gap matters because <see cref="NoOpModifierAnalyzer"/> (<c>REACTOR_MOD_003</c>) reads the
    /// same table in the opposite direction — "you wrote the modifier, does it reach this control?"
    /// — where a null gate means "never report" rather than "no predicate needed". Requiring every
    /// reconciler gate to be either declared or listed in
    /// <see cref="ModifierTable.GateOnlyInReconciler"/> makes adding one a deliberate decision for
    /// both rules instead of a silent no-op for one of them.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_ApplyModifiers_ControlGate_Is_Declared_Or_Explicitly_Recorded()
    {
        var actualGates = ReadApplyModifierControlGates();

        // Self-validation: the extraction must actually have found the gates. Without this the
        // test would pass vacuously if ReadApplyModifierControlGates ever stopped matching (a
        // rename of `fe`, a restructure of the guards), whereas the forward test would fail loudly.
        Assert.True(
            actualGates.Count >= 8,
            $"Only {actualGates.Count} control gates were read out of ApplyModifiers; expected at least 8 " +
            "(Padding, CornerRadius, BorderThickness, BorderBrush, Background, Foreground, and the fonts). " +
            "The gate reader has probably stopped matching — fix it rather than lowering this floor.");

        var problems = new List<string>();

        foreach (var (prop, actual) in actualGates.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (ModifierTable.Properties.TryGetValue(prop, out var info) && info.ControlGate is not null)
                continue;   // covered by the forward test above.

            if (ModifierTable.GateOnlyInReconciler.ContainsKey(prop))
                continue;

            problems.Add(
                $"{prop}: ApplyModifiers gates it on [{string.Join("|", actual.OrderBy(t => t, StringComparer.Ordinal))}] " +
                "but ModifierTable neither declares a ControlGate for it nor records it in " +
                "GateOnlyInReconciler. Declare the gate (so REACTOR_MOD_002 withholds its suggestion and " +
                "REACTOR_MOD_003 reports the silent drop), or add a GateOnlyInReconciler entry saying why " +
                "neither rule needs it.");
        }

        Assert.True(
            problems.Count == 0,
            "Reconciler.ApplyModifiers gates modifiers that ModifierTable does not account for:\n  " +
            string.Join("\n  ", problems));
    }

    /// <summary>
    /// Every entry in <see cref="ModifierTable.GateOnlyInReconciler"/> must name a gate that
    /// <c>ApplyModifiers</c> really enforces, and carry a reason — otherwise the exclusion list
    /// accumulates stale rows that quietly suppress the completeness check above.
    /// </summary>
    [Fact]
    public void Every_GateOnlyInReconciler_Entry_Is_Real_And_Explained()
    {
        var actualGates = ReadApplyModifierControlGates();
        var problems = new List<string>();

        foreach (var (prop, reason) in ModifierTable.GateOnlyInReconciler)
        {
            if (!actualGates.ContainsKey(prop))
            {
                problems.Add(
                    $"{prop}: recorded as gated-only-in-the-reconciler, but ApplyModifiers has no " +
                    "'fe is <Type>' test for it. Remove the row.");
            }

            if (string.IsNullOrWhiteSpace(reason))
                problems.Add($"{prop}: no reason recorded.");

            if (ModifierTable.Properties.TryGetValue(prop, out var info) && info.ControlGate is not null)
            {
                problems.Add(
                    $"{prop}: declares a ControlGate AND appears in GateOnlyInReconciler. The declared " +
                    "gate wins, so the row is dead — remove it.");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n  ", problems));
    }

    /// <summary>
    /// <c>Reconciler</c> carries a <b>second</b> copy of the same allow-list:
    /// <c>GetDependencyPropertyName</c> decides which properties a <c>ThemeRef</c> binding may emit
    /// a <c>&lt;Setter&gt;</c> for. Nothing pins it to <c>ApplyModifiers</c>, so
    /// <c>.Background(Theme.X)</c> and <c>.Background("#fff")</c> can silently disagree about which
    /// controls they reach — and both analyzers would be right about only one of them.
    /// </summary>
    [Fact]
    public void GetDependencyPropertyName_Agrees_With_ApplyModifiers_And_The_Table()
    {
        var applyGates = ReadApplyModifierControlGates();
        var themeGates = ReadGetDependencyPropertyNameGates();

        // Self-validation: Background, Foreground, BorderBrush.
        Assert.True(
            themeGates.Count >= 3,
            $"Only {themeGates.Count} gates were read out of GetDependencyPropertyName; expected at least 3. " +
            "The reader has probably stopped matching.");

        var problems = new List<string>();

        foreach (var (prop, themeGate) in themeGates.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!applyGates.TryGetValue(prop, out var applyGate))
            {
                problems.Add(
                    $"{prop}: GetDependencyPropertyName gates the ThemeRef path on " +
                    $"[{string.Join("|", themeGate.OrderBy(t => t, StringComparer.Ordinal))}] but ApplyModifiers " +
                    "has no control gate for it at all.");
                continue;
            }

            if (!themeGate.SetEquals(applyGate))
            {
                problems.Add(
                    $"{prop}: the ThemeRef path reaches [{string.Join("|", themeGate.OrderBy(t => t, StringComparer.Ordinal))}] " +
                    $"but the brush path reaches [{string.Join("|", applyGate.OrderBy(t => t, StringComparer.Ordinal))}]. " +
                    "A modifier that works with a literal brush and not with a Theme token (or vice versa) is a " +
                    "silent, overload-dependent bug.");
            }

            if (ModifierTable.Properties.TryGetValue(prop, out var info)
                && info.ControlGate is { } declared
                && !themeGate.SetEquals(declared))
            {
                problems.Add(
                    $"{prop}: ModifierTable declares [{string.Join("|", declared.OrderBy(t => t, StringComparer.Ordinal))}] " +
                    $"but the ThemeRef path reaches [{string.Join("|", themeGate.OrderBy(t => t, StringComparer.Ordinal))}].");
            }
        }

        Assert.True(
            problems.Count == 0,
            "Reconciler's two applicability copies have drifted:\n  " + string.Join("\n  ", problems));
    }

    /// <summary>
    /// Property name → the WinUI type names <c>Reconciler.GetDependencyPropertyName</c> will emit a
    /// <c>{ThemeResource}</c> setter for, read out of <c>Reconciler.cs</c>. The method's body is a
    /// chain of <c>if (property == "X" &amp;&amp; (fe is A || fe is B)) return "X";</c>, so each
    /// branch's string comparison names the property and the type tests are the gate.
    /// </summary>
    private static Dictionary<string, HashSet<string>> ReadGetDependencyPropertyNameGates()
    {
        var root = RepoRootFinder.FindRepoRoot();
        Assert.NotNull(root);
        var file = Path.Join(root!, "src", "Reactor", "Core", "Reconciler.cs");
        Assert.True(File.Exists(file), $"Reconciler.cs not found at {file}");

        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(File.ReadAllText(file));
        var method = tree.GetRoot()
            .DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == "GetDependencyPropertyName");

        Assert.NotNull(method);

        var gates = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var ifStatement in method!.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.IfStatementSyntax>())
        {
            var property = ifStatement.Condition
                .DescendantNodesAndSelf()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.LiteralExpressionSyntax>()
                .Select(literal => literal.Token.ValueText)
                .FirstOrDefault();

            if (property is null)
                continue;

            foreach (var binary in ifStatement.Condition
                .DescendantNodesAndSelf()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.BinaryExpressionSyntax>())
            {
                if (!binary.RawKind.Equals((int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.IsExpression)
                    || binary.Left is not Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax { Identifier.Text: "fe" })
                    continue;

                var typeName = binary.Right switch
                {
                    Microsoft.CodeAnalysis.CSharp.Syntax.QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
                    Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax simple => simple.Identifier.Text,
                    _ => null,
                };
                if (typeName is null)
                    continue;

                if (!gates.TryGetValue(property, out var set))
                    gates[property] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(typeName);
            }
        }

        return gates;
    }

    /// <summary>
    /// <see cref="NoOpModifierAnalyzer"/> resolves an element's mounted control from Reactor's
    /// public <c>Set(this TElement, Action&lt;TControl&gt;)</c> overload, because the generator
    /// attributes do not flow to consumers (<c>Reactor.Wrappers.Abstractions</c> is referenced with
    /// <c>PrivateAssets="all"</c>). That is only sound while the <c>Set</c> overload names the same
    /// control the descriptor was generated for — so pin the two together for every element that
    /// declares the attribute.
    /// </summary>
    /// <remarks>
    /// Reflection reads metadata only; no WinUI object is constructed, so this is safe in the
    /// headless host. Changing an element's <c>Set</c> signature without changing its descriptor —
    /// or vice versa — fails here rather than silently moving the analyzer's gate.
    /// </remarks>
    [Fact]
    [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming", "IL2026",
        Justification = "Test-only contract guard: enumerates the Reactor assembly's element types and the ElementExtensions surface by design. This host is never trimmed; behaviour-neutral.")]
    [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming", "IL2075",
        Justification = "Test-only contract guard: reflects the public static methods of ElementExtensions, resolved by name from the Reactor assembly. Intentional and JIT-only; behaviour-neutral.")]
    public void Every_Element_Set_Overload_Names_The_Control_Its_Descriptor_Mounts()
    {
        var elementType = typeof(Microsoft.UI.Reactor.Core.Element);
        var elementExtensions = elementType.Assembly.GetType("Microsoft.UI.Reactor.ElementExtensions");
        Assert.NotNull(elementExtensions);

        // element type → the Action<TControl> named by its own Set overload(s).
        var setControls = new Dictionary<Type, HashSet<Type>>();
        foreach (var method in elementExtensions!.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (method.Name != "Set")
                continue;

            var parameters = method.GetParameters();
            if (parameters.Length != 2
                || !parameters[1].ParameterType.IsGenericType
                || parameters[1].ParameterType.GetGenericTypeDefinition() != typeof(Action<>))
                continue;

            var receiver = parameters[0].ParameterType;
            if (receiver.IsGenericType)
                receiver = receiver.GetGenericTypeDefinition();

            if (!setControls.TryGetValue(receiver, out var controls))
                setControls[receiver] = controls = new HashSet<Type>();
            controls.Add(parameters[1].ParameterType.GetGenericArguments()[0]);
        }

        var checkedElements = 0;
        var problems = new List<string>();

        foreach (var element in elementType.Assembly.GetTypes()
                     .Where(t => elementType.IsAssignableFrom(t) && !t.IsAbstract)
                     .OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            if (DeclaredControl(element) is not { } declared)
                continue;   // no generator attribute — nothing to cross-check against.

            var key = element.IsGenericType ? element.GetGenericTypeDefinition() : element;
            if (!setControls.TryGetValue(key, out var fromSet))
                continue;   // no Set overload; the analyzer skips these elements entirely.

            checkedElements++;

            if (!fromSet.Contains(declared))
            {
                problems.Add(
                    $"{element.Name}: the descriptor mounts {declared.Name}, but its Set overload(s) take " +
                    $"[{string.Join("|", fromSet.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal))}]. " +
                    "REACTOR_MOD_003 reads the mounted control off Set, so it would gate against the wrong type.");
            }
        }

        Assert.True(
            problems.Count == 0,
            "An element's Set overload has drifted from the control its descriptor mounts:\n  " +
            string.Join("\n  ", problems));

        // Self-validation: dozens of elements carry both. A collapse to zero would mean the
        // attribute or Set reflection stopped resolving and the guard is running vacuously.
        Assert.True(
            checkedElements >= 40,
            $"Only {checkedElements} elements were cross-checked; expected 40+. The Set/attribute " +
            "reflection has probably stopped resolving.");
    }

    [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming", "IL2075",
        Justification = "Test-only contract guard: reads the generator attribute off a type enumerated by the surrounding Assembly.GetTypes scan. Behaviour-neutral.")]
    private static Type? DeclaredControl(Type element)
    {
        for (var current = element; current is not null; current = current.BaseType)
        {
            foreach (var attribute in current.GetCustomAttributesData())
            {
                var name = attribute.AttributeType.Name;
                if (name is not ("GenerateReactorWrapperAttribute" or "GenerateReactorDescriptorAttribute")
                    || attribute.ConstructorArguments.Count < 1)
                    continue;

                if (attribute.ConstructorArguments[0].Value is Type control)
                    return control;
            }
        }

        return null;
    }

    /// <summary>
    /// A generated descriptor's <c>Customize</c> hook may read a <b>common modifier</b> off the
    /// element and write it to the control itself — <c>RichTextBlockElement</c> does exactly that
    /// for <c>Padding</c>. On such an element <c>ApplyModifiers</c>' control gate is not the
    /// authority: the value is applied even though the gate says it would be dropped, so
    /// <see cref="NoOpModifierAnalyzer"/> must stay silent or it reports a false positive on correct
    /// code. That exception list is hand-maintained, so pin it to the descriptors.
    /// </summary>
    [Fact]
    public void Descriptor_Applied_Common_Modifiers_Match_The_Analyzer_Exception_List()
    {
        var (found, customizeHooks) = ReadDescriptorAppliedCommonModifiers();

        // Self-validation: descriptor Customize hooks are everywhere in Element.cs; a collapse to
        // zero means the reader stopped matching and the comparison below would pass vacuously.
        Assert.True(
            customizeHooks >= 20,
            $"Only {customizeHooks} descriptor Customize hooks were parsed; expected 20+. The reader " +
            "has probably stopped matching.");

        var declared = new HashSet<string>(
            NoOpModifierAnalyzer.DescriptorAppliedModifiers, StringComparer.Ordinal);

        var missing = found.Except(declared, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var stale = declared.Except(found, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToArray();

        Assert.True(
            missing.Length == 0,
            "A descriptor now applies a gated common modifier itself, but NoOpModifierAnalyzer still " +
            "treats ApplyModifiers' gate as the authority for it — REACTOR_MOD_003 would report a false " +
            "positive on correct code. Add to NoOpModifierAnalyzer.DescriptorAppliedModifiers:\n  " +
            string.Join("\n  ", missing));

        Assert.True(
            stale.Length == 0,
            "NoOpModifierAnalyzer.DescriptorAppliedModifiers suppresses a modifier no descriptor applies " +
            "any more, so a real silent drop is going unreported. Remove:\n  " +
            string.Join("\n  ", stale));
    }

    /// <summary>
    /// Scans every generated-descriptor <c>Customize</c> hook in <c>src/Reactor</c> for reads of a
    /// gated common modifier off the element lambda parameter (e.g.
    /// <c>get: static e =&gt; e.Padding…</c>), keyed as <c>Namespace.ElementType|Modifier</c>.
    /// Returns the set plus the number of hooks inspected, for the non-vacuity floor.
    /// </summary>
    private static (HashSet<string> Keys, int CustomizeHooks) ReadDescriptorAppliedCommonModifiers()
    {
        var root = RepoRootFinder.FindRepoRoot();
        Assert.NotNull(root);
        var sourceDir = Path.Join(root!, "src", "Reactor");
        Assert.True(Directory.Exists(sourceDir), $"src/Reactor not found at {sourceDir}");

        // Only the modifiers REACTOR_MOD_003 reports on can produce a false positive.
        var gated = new HashSet<string>(
            ModifierTable.Properties.Where(p => p.Value.ControlGate is not null).Select(p => p.Key),
            StringComparer.Ordinal);

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var hooks = 0;

        foreach (var file in Directory.GetFiles(sourceDir, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("Customize"))
                continue;

            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(text);
            foreach (var method in tree.GetRoot()
                .DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
                .Where(m => m.Identifier.Text == "Customize"))
            {
                if (method.Parent is not Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax owner)
                    continue;

                hooks++;

                var elementName = QualifiedTypeName(owner);

                foreach (var access in method.DescendantNodes()
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax>())
                {
                    if (access.Expression is not Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax receiver
                        || !gated.Contains(access.Name.Identifier.Text)
                        || !IsLambdaParameter(access, receiver.Identifier.Text))
                        continue;

                    keys.Add(NoOpModifierAnalyzer.ElementModifierKey(elementName, access.Name.Identifier.Text));
                }
            }
        }

        return (keys, hooks);
    }

    /// <summary>Namespace-qualified name of the type declaration owning a member.</summary>
    private static string QualifiedTypeName(Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax type)
    {
        for (Microsoft.CodeAnalysis.SyntaxNode? node = type.Parent; node is not null; node = node.Parent)
        {
            var ns = node switch
            {
                Microsoft.CodeAnalysis.CSharp.Syntax.FileScopedNamespaceDeclarationSyntax file => file.Name.ToString(),
                Microsoft.CodeAnalysis.CSharp.Syntax.NamespaceDeclarationSyntax block => block.Name.ToString(),
                _ => null,
            };
            if (ns is not null)
                return ns + "." + type.Identifier.Text;
        }

        return type.Identifier.Text;
    }

    /// <summary>
    /// True when <paramref name="name"/> is a parameter of some lambda enclosing
    /// <paramref name="node"/> — i.e. the member access reads the descriptor's element/control
    /// argument rather than an unrelated local of the same name.
    /// </summary>
    private static bool IsLambdaParameter(Microsoft.CodeAnalysis.SyntaxNode node, string name)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case Microsoft.CodeAnalysis.CSharp.Syntax.SimpleLambdaExpressionSyntax simple
                    when simple.Parameter.Identifier.Text == name:
                    return true;
                case Microsoft.CodeAnalysis.CSharp.Syntax.ParenthesizedLambdaExpressionSyntax paren
                    when paren.ParameterList.Parameters.Any(p => p.Identifier.Text == name):
                    return true;
                case Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax:
                    return false;
            }
        }

        return false;
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

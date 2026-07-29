using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Core.V1Protocol.Descriptor;
using WinUI = Microsoft.UI.Xaml.Controls;
using Xunit;

namespace Microsoft.UI.Reactor.Tests;

/// <summary>
/// Guards Reactor's contract for a declared <c>bool</c> that the NATIVE control
/// can also mutate — <c>InfoBar.IsOpen</c>, which WinUI sets to <c>false</c>
/// when the user dismisses the bar with its built-in ✕ (issue R7).
///
/// <para>The contract is <b>edge-triggered</b>: the element declares a
/// <i>transition</i>, not a mirror, so Reactor writes the control only when the
/// declared value changes, and callers wire <c>OnClosed</c> to sync their state.
/// These tests pin the two structural facts that make that true. Behavioural
/// coverage against a live control is
/// <c>IsOpenEdgeTriggeredFixtures</c> in the selftest host — Reactor.Tests
/// cannot instantiate WinUI controls headlessly.</para>
/// </summary>
public class InfoBarIsOpenEdgeTriggeredTests
{
    /// <summary>
    /// <c>InfoBar.IsOpen</c> must be bound through a plain diff-the-element
    /// <see cref="OneWayPropEntry{TElement,TControl,TValue}"/>.
    ///
    /// <para>Fails if the binding is ever re-routed through a live-readback /
    /// controlled entry that re-asserts the declared value against the control.
    /// That change is tempting — it is what R7 asked for — but
    /// <c>InfoBarElement.IsOpen</c> defaults to <c>true</c>, so it would make
    /// every InfoBar written without an <c>OnClosed</c> handler undismissable:
    /// the next unrelated re-render would bring the dismissed bar back.</para>
    /// </summary>
    [Fact]
    public void IsOpen_IsBoundThroughAPlainOneWayEntry()
    {
        var entry = FindIsOpenEntry();

        Assert.StartsWith("OneWayPropEntry`3", entry.GetType().Name, StringComparison.Ordinal);
    }

    /// <summary>
    /// The entry owning <c>IsOpen</c> must hold no reference to the live
    /// control's value — no readback lambda, no dependency property. That is
    /// what makes the write edge-triggered <i>by construction</i>: the only
    /// inputs it can diff are the old and the new element.
    ///
    /// <para>Differential control: the sibling
    /// <see cref="ControlledPropEntry{TElement,TControl,TValue,TArgs}"/> shape
    /// <i>does</i> carry a readback, so this assertion is checking a real
    /// distinction rather than a property every entry happens to satisfy.</para>
    /// </summary>
    [Fact]
    public void IsOpenEntry_CannotObserveTheLiveControl()
    {
        var fields = InstanceFieldNames(FindIsOpenEntry().GetType());

        Assert.DoesNotContain("_readBack", fields);
        Assert.DoesNotContain("_dp", fields);

        // The distinction is real: the controlled shape does carry a readback.
        Assert.Contains("_readBack", InstanceFieldNames(
            typeof(ControlledPropEntry<InfoBarElement, WinUI.InfoBar, bool, EventArgs>)));
    }

    /// <summary>
    /// Pins the element-side default. The whole risk calculus above depends on
    /// <c>IsOpen</c> defaulting to <c>true</c> (so <c>InfoBar("t","m")</c> is
    /// visible without ceremony); if that ever changes, the reasoning recorded
    /// in these tests needs revisiting.
    /// </summary>
    [Fact]
    public void IsOpen_DefaultsToTrue()
    {
        Assert.True(new InfoBarElement("t", "m").IsOpen);
    }

    /// <summary>
    /// Locates the descriptor entry bound to <c>IsOpen</c> by behaviour, not by
    /// position: <c>IsClosable</c> is also an auto-mapped <c>bool</c> one-way
    /// prop on the same descriptor, so the getter is identified by responding to
    /// <c>IsOpen</c> and <i>not</i> to <c>IsClosable</c>.
    /// </summary>
    private static PropEntry<InfoBarElement, WinUI.InfoBar> FindIsOpenEntry()
    {
        var baseline = new InfoBarElement("t", "m");
        var closed = baseline with { IsOpen = false };
        var notClosable = baseline with { IsClosable = false };

        var matches = InfoBarElement.Descriptor.Properties
            .Where(e => ReadsIsOpen(e, baseline, closed, notClosable))
            .ToList();

        return Assert.Single(matches);
    }

    private static bool ReadsIsOpen(
        PropEntry<InfoBarElement, WinUI.InfoBar> entry,
        InfoBarElement baseline,
        InfoBarElement closed,
        InfoBarElement notClosable)
    {
        if (GetPrivateField(entry, "_get") is not Func<InfoBarElement, bool> get)
            return false;

        return get(baseline) && !get(closed) && get(notClosable);
    }

    [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Test-only: reflects non-public instance fields on concrete descriptor entry types the test resolves at runtime. JIT-only (this host is never trimmed) and behaviour-neutral — neither preserves nor prunes members.")]
    private static IReadOnlyCollection<string> InstanceFieldNames(Type type)
        => type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
               .Select(f => f.Name)
               .ToList();

    [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Test-only: reflects a known non-public field on a concrete descriptor entry. JIT-only (this host is never trimmed) and behaviour-neutral.")]
    private static object? GetPrivateField(object owner, string name)
        => owner.GetType()
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(owner);
}

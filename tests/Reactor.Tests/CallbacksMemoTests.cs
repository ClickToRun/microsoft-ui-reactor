using System;
using System.Collections.Generic;
using Microsoft.UI.Reactor.Core;
using Xunit;

namespace Microsoft.UI.Reactor.Tests;

/// <summary>
/// Tests for issue #151: keeping delegate props out of memo comparison via the
/// opt-in <see cref="Callbacks{T}"/> wrapper, plus the reconciler's stale-delegate
/// guard (live Props refresh on a memo-skip).
///
/// These are headless: they exercise the production memo gate
/// (<see cref="IPropsComparable.CompareProps"/> → <c>Component&lt;TProps&gt;.ShouldUpdate</c>)
/// and the production props-refresh primitive (<see cref="IPropsReceiver.SetProps"/>) —
/// the same two pieces the reconciler wires together in <c>ReconcileComponent</c>.
/// </summary>
public class CallbacksMemoTests
{
    // ── Test payloads / props / components ───────────────────────

    private sealed record Cbs(Action OnTap, Action<string>? OnText = null);

    // AFTER: callbacks ride along inside a Callbacks<T> wrapper.
    private sealed record WrappedProps(int Data, Callbacks<Cbs> Cb);

    // BEFORE: the old shape with an inline delegate field that compares by reference.
    private sealed record InlineProps(int Data, Action OnTap);

    private sealed class WrappedComp : Component<WrappedProps>
    {
        public override Element Render() => new TextBlockElement($"data={Props.Data}");

        // A handler that reads the live callback off Props at *dispatch* time.
        public void Dispatch() => Props.Cb.Value.OnTap();
    }

    private sealed class InlineComp : Component<InlineProps>
    {
        public override Element Render() => new TextBlockElement($"data={Props.Data}");
    }

    // Mirrors the reconciler's memo gate: true => re-render, false => skip.
    private static bool Gate(Component comp, object? oldProps, object? newProps)
        => ((IPropsComparable)comp).CompareProps(oldProps, newProps);

    // ── Callbacks<T> semantics ───────────────────────────────────

    [Fact]
    public void Callbacks_Equals_Is_Always_True_Regardless_Of_Payload()
    {
        var a = new Callbacks<Cbs>(new Cbs(() => { }));
        var b = new Callbacks<Cbs>(new Cbs(() => { })); // different delegates entirely
        Assert.True(a.Equals(b));
        Assert.True(b.Equals(a));
        Assert.True(a == b);
    }

    [Fact]
    public void Callbacks_GetHashCode_Is_Always_Zero()
    {
        Assert.Equal(0, new Callbacks<Cbs>(new Cbs(() => { })).GetHashCode());
        Assert.Equal(0, new Callbacks<Action>(() => { }).GetHashCode());
    }

    [Fact]
    public void Callbacks_Value_Returns_The_Wrapped_Payload()
    {
        var payload = new Cbs(() => { });
        var wrapped = new Callbacks<Cbs>(payload);
        Assert.Same(payload, wrapped.Value);
    }

    [Fact]
    public void Callbacks_Implicit_Conversion_From_Payload()
    {
        var payload = new Cbs(() => { });
        Callbacks<Cbs> wrapped = payload; // implicit
        Assert.Same(payload, wrapped.Value);
    }

    [Fact]
    public void Owning_Record_Equality_Ignores_Callbacks_Slot()
    {
        // Same data, totally different callbacks => records are equal.
        var p1 = new WrappedProps(1, new Cbs(() => { }));
        var p2 = new WrappedProps(1, new Cbs(() => { }));
        Assert.Equal(p1, p2);

        // Different data => not equal (data still drives equality).
        var p3 = new WrappedProps(2, p1.Cb);
        Assert.NotEqual(p1, p3);
    }

    // ── (a) No re-render when only a callback delegate's identity changes ──

    [Fact]
    public void Skips_Render_When_Only_Callback_Identity_Changes()
    {
        var comp = new WrappedComp();
        var oldProps = new WrappedProps(7, new Cbs(() => { }));
        var newProps = new WrappedProps(7, new Cbs(() => { })); // fresh delegate, same data

        // Gate returns false => reconciler skips the re-render.
        Assert.False(Gate(comp, oldProps, newProps));
    }

    // ── (b) Re-renders when data changes ─────────────────────────

    [Fact]
    public void Re_Renders_When_Data_Changes()
    {
        var comp = new WrappedComp();
        var cb = new Callbacks<Cbs>(new Cbs(() => { }));
        var oldProps = new WrappedProps(7, cb);
        var newProps = new WrappedProps(8, cb); // same callbacks, different data

        Assert.True(Gate(comp, oldProps, newProps));
    }

    // ── (c) Stale-delegate guard: latest delegate is invoked, not the memoized one ──

    [Fact]
    public void Skip_Refreshes_Live_Props_So_Current_Delegate_Is_Invoked()
    {
        var comp = new WrappedComp();

        int firstCalls = 0, secondCalls = 0;
        var firstProps = new WrappedProps(5, new Cbs(() => firstCalls++));
        var secondProps = new WrappedProps(5, new Cbs(() => secondCalls++)); // new delegate, same data

        // Initial mount-equivalent: props are set on the instance.
        ((IPropsReceiver)comp).SetProps(firstProps);

        // Reconcile with new props: data unchanged, only the callback identity
        // differs, so the gate skips the re-render...
        bool reRender = Gate(comp, firstProps, secondProps);
        Assert.False(reRender);

        // ...but the reconciler's skip path (issue #151) still refreshes the live
        // Props so the CURRENT delegate dispatches. This mirrors the SetProps call
        // in Reconciler.ReconcileComponent's skipRender branch.
        ((IPropsReceiver)comp).SetProps(secondProps);

        comp.Dispatch();

        Assert.Equal(0, firstCalls);  // stale delegate NOT invoked
        Assert.Equal(1, secondCalls); // current delegate invoked
    }

    [Fact]
    public void Without_The_Refresh_The_Stale_Delegate_Would_Be_Invoked()
    {
        // Documents WHY the reconciler refresh is load-bearing: an always-equal
        // callbacks slot without a Props refresh on skip would dispatch the stale
        // delegate. (This is the buggy behavior the #151 fix prevents.)
        var comp = new WrappedComp();

        int firstCalls = 0, secondCalls = 0;
        var firstProps = new WrappedProps(5, new Cbs(() => firstCalls++));
        var secondProps = new WrappedProps(5, new Cbs(() => secondCalls++));

        ((IPropsReceiver)comp).SetProps(firstProps);
        Assert.False(Gate(comp, firstProps, secondProps));
        // NOTE: deliberately NOT refreshing Props here.

        comp.Dispatch();

        Assert.Equal(1, firstCalls);  // stale delegate invoked — the hazard
        Assert.Equal(0, secondCalls);
    }

    // ── Headless render-count bench: 9-of-9 → 1-of-9 ─────────────

    [Fact]
    public void RenderCount_Bench_Nine_Children_One_Mutates()
    {
        const int childCount = 9;
        const int mutatedIndex = 3;

        // Build the "previous render" prop set: child i has data=i and its own delegate.
        var oldInline = new InlineProps[childCount];
        var oldWrapped = new WrappedProps[childCount];
        for (int i = 0; i < childCount; i++)
        {
            int captured = i;
            oldInline[i] = new InlineProps(captured, () => { _ = captured; });
            oldWrapped[i] = new WrappedProps(captured, new Cbs(() => { _ = captured; }));
        }

        // Build the "next render" prop set: the parent re-renders, allocating FRESH
        // delegates for every child (as lambdas/local functions always do), and only
        // child #mutatedIndex's data actually changes.
        var newInline = new InlineProps[childCount];
        var newWrapped = new WrappedProps[childCount];
        for (int i = 0; i < childCount; i++)
        {
            int data = i == mutatedIndex ? 100 + i : i;
            int captured = i;
            newInline[i] = new InlineProps(data, () => { _ = captured; }); // fresh delegate
            newWrapped[i] = new WrappedProps(data, new Cbs(() => { _ = captured; })); // fresh delegate
        }

        var inlineComp = new InlineComp();
        var wrappedComp = new WrappedComp();

        int inlineRerenders = 0, wrappedRerenders = 0;
        for (int i = 0; i < childCount; i++)
        {
            if (Gate(inlineComp, oldInline[i], newInline[i])) inlineRerenders++;
            if (Gate(wrappedComp, oldWrapped[i], newWrapped[i])) wrappedRerenders++;
        }

        // BEFORE (inline delegate field): every child re-renders because each got a
        // fresh delegate identity — 9 of 9.
        Assert.Equal(childCount, inlineRerenders);

        // AFTER (Callbacks<T> wrapper): only the child whose data changed re-renders —
        // 1 of 9.
        Assert.Equal(1, wrappedRerenders);
    }
}

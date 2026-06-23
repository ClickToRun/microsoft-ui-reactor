using System;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Core.V1Protocol;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.Spec048;

/// <summary>
/// Spec-048 §3.4 / issue #486 — clear failure mode + the public opt-in
/// <see cref="ReactorApp.RegisterAllBuiltIns"/> catalog registration.
///
/// <para>Once the eager <c>RegisterV1BuiltInHandlers</c> bootstrap was
/// deleted, an element record whose handler was never registered (e.g. a
/// direct-record construction that bypassed its factory) would otherwise
/// silently no-op-mount as <c>null</c>. The reconciler now throws an
/// actionable <see cref="InvalidOperationException"/> instead.</para>
/// </summary>
public sealed class UnregisteredHandlerAndRegisterAllBuiltInsTests
{
    private static readonly Action NoOp = static () => { };

    // A bespoke element type that no factory and no registration path ever
    // touches — so it misses all dispatch arms and trips the throw. (The test
    // assembly's module-initializer registers every *built-in* handler, so a
    // built-in element type could not exercise this path.)
    private sealed record UnregisteredProbeElement(string Label) : Element;

    [Fact]
    public void Mount_Of_Unregistered_Element_Throws_Actionable_InvalidOperationException()
    {
        var reconciler = new Reconciler();

        var ex = Assert.Throws<InvalidOperationException>(
            () => reconciler.Mount(new UnregisteredProbeElement("x"), NoOp));

        // Names the concrete element type.
        Assert.Contains(nameof(UnregisteredProbeElement), ex.Message);
        // Points at both/all remediation paths required by issue #486.
        Assert.Contains("factory", ex.Message);
        Assert.Contains("RegisterAllBuiltIns", ex.Message);
        Assert.Contains("ControlRegistry.Register", ex.Message);
    }

    [Fact]
    public void Mount_Of_EmptyElement_Does_Not_Throw()
    {
        // EmptyElement is a legitimately handler-less sentinel — the throw
        // must not fire for it.
        var reconciler = new Reconciler();
        var control = reconciler.Mount(new EmptyElement(), NoOp);
        Assert.Null(control);
    }

    [Fact]
    public void RegisterAllBuiltIns_Registers_Representative_BuiltIns()
    {
        // Idempotent + process-wide: safe to call (the test bootstrap already
        // called it once via [ModuleInitializer]). This asserts the public
        // catalog method actually names a representative spread of built-ins —
        // a descriptor value control, a decorator, and a base-derived type.
        ReactorApp.RegisterAllBuiltIns();
        ReactorApp.RegisterAllBuiltIns(); // second call must be a no-op, not a throw

        Assert.True(ControlRegistry.ContainsForType(typeof(TextBlockElement)),
            "RegisterAllBuiltIns should register the TextBlock descriptor handler.");
        Assert.True(ControlRegistry.ContainsForType(typeof(ButtonElement)),
            "RegisterAllBuiltIns should register the Button decorator handler.");
        Assert.True(ControlRegistry.ContainsForType(typeof(ListViewElement)),
            "RegisterAllBuiltIns should register the ListView handler.");
    }
}

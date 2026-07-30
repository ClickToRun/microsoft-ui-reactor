using System;
using System.Collections.Generic;
using Microsoft.UI.Reactor.Hosting;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.Hosting;

// A managed subclass of a WinRT-projected type, at file scope. Never instantiated — only its
// Type metadata is read — but WinUI-derived types must still be `partial`, and CsWinRT1028
// requires the *whole containing chain* to be partial too, so this cannot be nested inside a
// non-partial test class.
internal sealed partial class ProbeManagedPageSubclass : Microsoft.UI.Xaml.Controls.Page { }

/// <summary>
/// Headless coverage for the guard that stops <c>Frame.Navigate</c> taking the process
/// down when the XAML metadata chain cannot resolve the navigation target.
///
/// <para>WinUI resolves a custom navigation target through <c>Application.Current</c>'s
/// <c>IXamlMetadataProvider</c> and dereferences the null it gets back, terminating the
/// process with <c>0xC0000005</c> rather than throwing — see
/// <c>docs/specs/011-navigation-design.md</c> §"Why WinUI Frame is not the answer".
/// Reactor deliberately does <b>not</b> make code-only pages resolvable (that would breach
/// spec 011's zero-XAML-dependency goal and only partially work); it refuses the navigation
/// and reports it instead.</para>
///
/// <para>Everything here is deliberately WinUI-object-free: the headless test host cannot
/// construct a <c>Microsoft.UI.Xaml</c> instance, which is why the decision is factored
/// behind a <c>Func&lt;Type, object?&gt;</c> resolver rather than taking the interface.</para>
/// </summary>
public sealed class FrameNavigationGuardTests
{
    private sealed class ProbePage { }
    private sealed class ProbeOtherPage { }
    private sealed class ProbeOpenGenericPage<T> { }

    // ── WinRT-projected types resolve natively ──────────────────────────────

    [Fact]
    public void CanResolvePageType_Allows_A_WinRT_Projected_Type_Without_Asking_The_Resolver()
    {
        var consulted = false;

        // Microsoft.UI.Xaml.Controls.Page lives in the native WinRT type system, so
        // MetadataAPI finds it without any managed provider. Application.Current returns
        // null for it — refusing on that null would break navigation that works today.
        var result = FrameNavigation.CanResolvePageType(
            typeof(Microsoft.UI.Xaml.Controls.Page),
            _ => { consulted = true; return null; });

        Assert.True(result);
        Assert.False(consulted);
    }

    [Fact]
    public void CanResolvePageType_Does_Not_Let_A_Managed_Subclass_Inherit_Its_Bases_Projection()
    {
        // The `inherit: false` in the guard is what makes this hold. Were it `true`, every
        // code-only `class MyPage : Page` would look natively resolvable and sail through to
        // Frame.Navigate — reinstating the exact access violation this guard exists to stop.
        Assert.True(typeof(Microsoft.UI.Xaml.Controls.Page)
            .IsDefined(typeof(global::WinRT.WindowsRuntimeTypeAttribute), inherit: false));
        Assert.False(typeof(ProbeManagedPageSubclass)
            .IsDefined(typeof(global::WinRT.WindowsRuntimeTypeAttribute), inherit: false));

        var consulted = false;
        var result = FrameNavigation.CanResolvePageType(
            typeof(ProbeManagedPageSubclass),
            _ => { consulted = true; return null; });

        // Falls through to the resolver — and the resolver's "no" stands.
        Assert.True(consulted);
        Assert.False(result);
    }

    // ── The decision seam ───────────────────────────────────────────────────

    [Fact]
    public void CanResolvePageType_Differentiates_On_The_Resolver_Result()
    {
        var pageType = typeof(ProbePage);

        // Same input type, two resolvers differing only in what they return: the decision
        // must follow the resolver, not the input. If the method ever hard-coded a verdict,
        // these two would stop differing.
        var refused = FrameNavigation.CanResolvePageType(pageType, static _ => null);
        var allowed = FrameNavigation.CanResolvePageType(pageType, static _ => new object());

        Assert.False(refused);
        Assert.True(allowed);
        Assert.NotEqual(refused, allowed);
    }

    [Fact]
    public void CanResolvePageType_Passes_The_Page_Type_To_The_Resolver()
    {
        var seen = new List<Type>();
        FrameNavigation.CanResolvePageType(typeof(ProbePage), t => { seen.Add(t); return new object(); });

        // Exactly one consultation, with the requested type — not a cached or substituted one.
        Assert.Equal(new[] { typeof(ProbePage) }, seen);
    }

    [Fact]
    public void CanResolvePageType_Refuses_A_Null_Page_Type_Without_Consulting_The_Resolver()
    {
        var consulted = false;
        var result = FrameNavigation.CanResolvePageType(null, _ => { consulted = true; return new object(); });

        Assert.False(result);
        Assert.False(consulted);
    }

    [Fact]
    public void CanResolvePageType_Treats_A_Throwing_Resolver_As_Unresolvable()
    {
        var consulted = false;

        // A broken third-party provider must degrade into a refused navigation, never into
        // the access violation that calling Navigate anyway would produce. "Could not answer"
        // and "answered no" are the same verdict here because they carry the same risk.
        var result = FrameNavigation.CanResolvePageType(
            typeof(ProbePage),
            _ => { consulted = true; throw new InvalidOperationException("provider is broken"); });

        Assert.True(consulted);
        Assert.False(result);
    }

    [Fact]
    public void CanResolvePageType_Lets_The_Two_Fatal_Exceptions_Propagate()
    {
        // The catch filter carves out OOM/SO deliberately; a fail-safe default is not worth
        // swallowing a condition the process cannot continue through.
        Assert.Throws<OutOfMemoryException>(
            () => FrameNavigation.CanResolvePageType(typeof(ProbePage), _ => throw new OutOfMemoryException()));
    }

    [Fact]
    public void CanResolvePageType_Rejects_A_Null_Resolver()
        => Assert.Throws<ArgumentNullException>(
            () => FrameNavigation.CanResolvePageType(typeof(ProbePage), null!));

    // ── The refusal message ─────────────────────────────────────────────────
    //
    // This is the whole user-facing value of refusing rather than crashing, so it is pinned
    // rather than left to prose. A message that says only "could not resolve" sends the
    // reader looking for a way to make Frame work; the fix is to stop using Frame.

    [Fact]
    public void Refusal_Message_Names_The_Type_That_Was_Refused()
    {
        var message = FrameNavigation.BuildUnresolvableMessage(typeof(ProbePage));

        Assert.Contains(typeof(ProbePage).FullName!, message, StringComparison.Ordinal);
        // Differential: a different type must produce a different message, so the name is
        // genuinely interpolated rather than a constant that happens to contain the word.
        Assert.NotEqual(message, FrameNavigation.BuildUnresolvableMessage(typeof(ProbeOtherPage)));
    }

    [Fact]
    public void Refusal_Message_Redirects_To_The_Supported_Navigation_System()
    {
        var message = FrameNavigation.BuildUnresolvableMessage(typeof(ProbePage));

        // The redirect is the point: spec 011 makes UseNavigation the navigation system and
        // Frame an interop escape hatch. Dropping either half of this turns a signpost back
        // into a dead end.
        Assert.Contains("UseNavigation", message, StringComparison.Ordinal);
        Assert.Contains("NavigationHost", message, StringComparison.Ordinal);
        // ...and it must say *why* Frame failed, or the reader cannot tell whether their own
        // XAML-backed page would have worked.
        Assert.Contains(".xaml", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refusal_Message_Falls_Back_To_Name_When_FullName_Is_Null()
    {
        // Generic parameters have a null FullName. WinUI keys its lookup on FullName, so this
        // is a genuinely unresolvable input — the message must still identify something.
        var openGeneric = typeof(ProbeOpenGenericPage<>).GetGenericArguments()[0];
        Assert.Null(openGeneric.FullName);

        var message = FrameNavigation.BuildUnresolvableMessage(openGeneric);

        Assert.Contains(openGeneric.Name, message, StringComparison.Ordinal);
        // Guards the '' that a bare FullName interpolation would have produced.
        Assert.DoesNotContain("''", message, StringComparison.Ordinal);
    }
}

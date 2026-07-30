using System;
using System.Collections.Generic;
using Microsoft.UI.Reactor.Hosting;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.Hosting;

// A two-level app-defined hierarchy over a WinUI base. Never instantiated — only its
// Type metadata is walked — but WinUI-derived types must still be `partial` for the
// CsWinRT ABI analyzer, which CI promotes to an error.
internal partial class ProbeAppDefinedBasePage : Microsoft.UI.Xaml.Controls.Page { }

internal sealed partial class ProbeDerivedFromAppBasePage : ProbeAppDefinedBasePage { }

/// <summary>
/// Headless coverage for the metadata-publishing layer that makes
/// <c>Frame.Navigate</c> survive a code-only <c>Page</c> type.
///
/// <para>Reactor apps ship no <c>.xaml</c>, so the XAML compiler emits no
/// <c>&lt;App&gt;_XamlTypeInfo</c> for them and every app-defined page is invisible to
/// <c>Application.Current</c>'s <c>IXamlMetadataProvider</c> chain. WinUI's
/// <c>MetadataAPI::GetClassInfoByTypeName</c> dereferences the null it gets back and
/// terminates the process with an access violation — see
/// <c>docs/specs/011-navigation-design.md</c> §"Why WinUI Frame is not the answer".
/// <see cref="ReactorPageTypeRegistry"/> supplies the missing metadata and
/// <see cref="FrameNavigation"/> refuses to call <c>Navigate</c> when it is still
/// missing.</para>
///
/// <para>Everything here is deliberately WinUI-object-free: the headless test host
/// cannot construct a <c>Microsoft.UI.Xaml</c> instance.</para>
/// </summary>
public sealed class FrameNavigationMetadataTests
{
    // Distinct probe types per test so the process-wide registry can't make one test
    // pass because another already registered the same type.
    private sealed class ProbeRegisterPage { }
    private sealed class ProbeByNamePage { }
    private sealed class ProbeIdempotentPage { }
    private sealed class ProbeUnregisteredPage { }
    private sealed class ProbeBaseTypePage { }

    private sealed class ProbeActivatedPage
    {
        internal static int Constructions;
        public ProbeActivatedPage() => Constructions++;
    }

    private sealed class ProbeThrowingPage
    {
        internal const string Message = "ProbeThrowingPage ctor failed on purpose";
        public ProbeThrowingPage() => throw new InvalidOperationException(Message);
    }

    // ── Registry ────────────────────────────────────────────────────────────

    [Fact]
    public void Unregistered_Type_Is_Not_Resolvable()
    {
        // The pre-fix state: nothing published, nothing resolves. This is the exact
        // condition under which calling Frame.Navigate kills the process.
        Assert.Null(ReactorPageTypeRegistry.Resolve(typeof(ProbeUnregisteredPage)));
        Assert.Null(ReactorPageTypeRegistry.Resolve(typeof(ProbeUnregisteredPage).FullName!));
    }

    [Fact]
    public void Register_Makes_The_Type_Resolvable_By_Type()
    {
        Assert.Null(ReactorPageTypeRegistry.Resolve(typeof(ProbeRegisterPage)));

        ReactorPageTypeRegistry.Register(typeof(ProbeRegisterPage));

        var resolved = ReactorPageTypeRegistry.Resolve(typeof(ProbeRegisterPage));
        Assert.NotNull(resolved);
        Assert.Equal(typeof(ProbeRegisterPage), resolved!.UnderlyingType);
        Assert.Equal(typeof(ProbeRegisterPage).FullName, resolved.FullName);
    }

    [Fact]
    public void Register_Makes_The_Type_Resolvable_By_Full_Name()
    {
        // WinUI's metadata lookup is keyed on the full name string, so the by-name index
        // matters just as much as the by-Type one.
        var fullName = typeof(ProbeByNamePage).FullName!;
        Assert.Null(ReactorPageTypeRegistry.Resolve(fullName));

        ReactorPageTypeRegistry.Register(typeof(ProbeByNamePage));

        var resolved = ReactorPageTypeRegistry.Resolve(fullName);
        Assert.NotNull(resolved);
        Assert.Equal(typeof(ProbeByNamePage), resolved!.UnderlyingType);
    }

    [Fact]
    public void Register_Is_Idempotent_And_Returns_The_Same_Metadata_Instance()
    {
        ReactorPageTypeRegistry.Register(typeof(ProbeIdempotentPage));
        var first = ReactorPageTypeRegistry.Resolve(typeof(ProbeIdempotentPage));
        var countAfterFirst = ReactorPageTypeRegistry.Count;

        ReactorPageTypeRegistry.Register(typeof(ProbeIdempotentPage));
        var second = ReactorPageTypeRegistry.Resolve(typeof(ProbeIdempotentPage));

        Assert.Same(first, second);
        Assert.Equal(countAfterFirst, ReactorPageTypeRegistry.Count);
    }

    [Fact]
    public void Register_Rejects_Null()
        => Assert.Throws<ArgumentNullException>(() => ReactorPageTypeRegistry.Register(null!));

    // ── Synthesized IXamlType ───────────────────────────────────────────────

    [Fact]
    public void Registered_Metadata_Reports_The_Shape_WinUI_Needs_To_Activate()
    {
        ReactorPageTypeRegistry.Register(typeof(ProbeActivatedPage));
        var resolved = Assert.IsType<ReactorUserXamlType>(
            ReactorPageTypeRegistry.Resolve(typeof(ProbeActivatedPage)));

        // IsConstructible false would send WinUI down its "schema-only type" path and the
        // navigation would never produce a page; IsLocalType is what marks it app-defined.
        Assert.True(resolved.IsConstructible);
        Assert.True(resolved.IsLocalType);
        Assert.False(resolved.IsCollection);
        Assert.False(resolved.IsDictionary);
        Assert.False(resolved.IsMarkupExtension);
        Assert.False(resolved.IsReturnTypeStub);
    }

    [Fact]
    public void ActivateInstance_Really_Constructs_The_Registered_Type()
    {
        ReactorPageTypeRegistry.Register(typeof(ProbeActivatedPage));
        var resolved = ReactorPageTypeRegistry.Resolve(typeof(ProbeActivatedPage))!;

        var before = ProbeActivatedPage.Constructions;
        var instance = resolved.ActivateInstance();
        var after = ProbeActivatedPage.Constructions;

        // The constructor side effect — not just the returned reference — is the oracle:
        // an activator stubbed to return default would leave the counter untouched.
        Assert.Equal(before + 1, after);
        Assert.IsType<ProbeActivatedPage>(instance);
    }

    [Fact]
    public void ActivateInstance_Surfaces_The_Constructors_Own_Exception()
    {
        ReactorPageTypeRegistry.Register(typeof(ProbeThrowingPage));
        var resolved = ReactorPageTypeRegistry.Resolve(typeof(ProbeThrowingPage))!;

        // Activator wraps ctor failures in TargetInvocationException; leaving it wrapped
        // would report the useless "Exception has been thrown by the target of an
        // invocation" through OnNavigationFailed instead of the page's real error.
        var ex = Assert.Throws<InvalidOperationException>(() => resolved.ActivateInstance());
        Assert.Equal(ProbeThrowingPage.Message, ex.Message);
    }

    [Fact]
    public void BaseType_Resolves_To_The_Nearest_Framework_Ancestor()
    {
        // WinUI needs the base chain to terminate at a type it already knows. A plain
        // managed probe type has no framework ancestor at all, so it reports none...
        ReactorPageTypeRegistry.Register(typeof(ProbeBaseTypePage));
        Assert.Null(ReactorPageTypeRegistry.Resolve(typeof(ProbeBaseTypePage))!.BaseType);

        // ...while a WinUI-derived type reports the framework ancestor, skipping the
        // app-defined intermediate. Only Type metadata is touched — nothing is activated,
        // so this stays headless-safe.
        var forDerived = Assert.IsType<ReactorSystemBaseXamlType>(
            ReactorSystemBaseXamlType.ForNearestFrameworkAncestor(typeof(ProbeDerivedFromAppBasePage)));
        Assert.Equal(typeof(Microsoft.UI.Xaml.Controls.Page).FullName, forDerived.FullName);
        Assert.Equal(typeof(Microsoft.UI.Xaml.Controls.Page), forDerived.UnderlyingType);
        Assert.False(forDerived.IsConstructible);
        Assert.False(forDerived.IsLocalType);
    }

    // ── Navigation gate ─────────────────────────────────────────────────────

    [Fact]
    public void CanResolvePageType_Differentiates_On_The_Resolver_Result()
    {
        var pageType = typeof(ProbeUnregisteredPage);

        // Same input type, two resolvers differing only in what they return: the decision
        // must follow the resolver, not the input.
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
        FrameNavigation.CanResolvePageType(typeof(ProbeRegisterPage), t => { seen.Add(t); return new object(); });

        Assert.Equal(new[] { typeof(ProbeRegisterPage) }, seen);
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

        // A broken provider must degrade into a refused navigation, never into the access
        // violation that calling Navigate anyway would produce.
        var result = FrameNavigation.CanResolvePageType(
            typeof(ProbeRegisterPage),
            _ => { consulted = true; throw new InvalidOperationException("provider is broken"); });

        Assert.True(consulted);
        Assert.False(result);
    }

    [Fact]
    public void CanResolvePageType_Rejects_A_Null_Resolver()
        => Assert.Throws<ArgumentNullException>(
            () => FrameNavigation.CanResolvePageType(typeof(ProbeRegisterPage), null!));
}

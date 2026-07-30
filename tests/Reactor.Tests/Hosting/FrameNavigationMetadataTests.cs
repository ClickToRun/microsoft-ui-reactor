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
    private sealed class ProbeOpenGenericPage<T> { }
    private sealed class ProbePublicApiPage { }

    // ── Public opt-in (ReactorApp.RegisterPageType) ─────────────────────────

    [Fact]
    public void ReactorApp_RegisterPageType_Publishes_Through_The_Public_Entry_Point()
    {
        // The public escape hatch for authors who navigate imperatively — e.g.
        // Frame().Set(f => f.Navigate(typeof(MyPage))) — which bypasses Reactor's guarded
        // path entirely and would otherwise still hit the access violation.
        Assert.Null(ReactorPageTypeRegistry.Resolve(typeof(ProbePublicApiPage)));

        ReactorApp.RegisterPageType(typeof(ProbePublicApiPage));

        var resolved = ReactorPageTypeRegistry.Resolve(typeof(ProbePublicApiPage));
        Assert.NotNull(resolved);
        Assert.Equal(typeof(ProbePublicApiPage), resolved!.UnderlyingType);
        // Resolvable by name too — that is the key WinUI actually looks up by.
        Assert.Same(resolved, ReactorPageTypeRegistry.Resolve(typeof(ProbePublicApiPage).FullName!));
    }

    [Fact]
    public void ReactorApp_RegisterPageType_Rejects_Null()
        => Assert.Throws<ArgumentNullException>(() => ReactorApp.RegisterPageType(null!));

    private sealed class ProbeActivatedPage
    {
        // Instance state set by the constructor, rather than a static counter: the registry
        // is process-wide and xUnit runs collections in parallel, so a shared counter is not
        // a stable oracle (and writing one from a constructor trips CodeQL). Asserting on the
        // returned instance is also a stronger check — see the test for why.
        internal readonly bool ConstructorRan;
        public ProbeActivatedPage() => ConstructorRan = true;
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

        ReactorPageTypeRegistry.Register(typeof(ProbeIdempotentPage));
        var second = ReactorPageTypeRegistry.Resolve(typeof(ProbeIdempotentPage));

        // Same instance — a second Register neither replaced the entry nor added a rival.
        // Asserted on identity rather than registry size: the registry is process-wide and
        // xUnit runs collections in parallel, so a count is not a stable oracle.
        Assert.Same(first, second);
        Assert.Same(first, ReactorPageTypeRegistry.Resolve(typeof(ProbeIdempotentPage).FullName!));
    }

    [Fact]
    public void Register_Rejects_Null()
        => Assert.Throws<ArgumentNullException>(() => ReactorPageTypeRegistry.Register(null!));

    [Fact]
    public void Register_Ignores_An_Open_Generic_Type()
    {
        // WinUI activates a navigation target by full name and cannot construct an open
        // generic, so publishing one would put an unusable entry in the chain. Its FullName
        // is non-null, so the FullName guard alone does not catch it.
        var openGeneric = typeof(ProbeOpenGenericPage<>);
        Assert.NotNull(openGeneric.FullName);

        ReactorPageTypeRegistry.Register(openGeneric);

        Assert.Null(ReactorPageTypeRegistry.Resolve(openGeneric));
        Assert.Null(ReactorPageTypeRegistry.Resolve(openGeneric.FullName!));

        // The closed construction is a perfectly good target and must still publish —
        // otherwise the guard would be over-broad.
        ReactorPageTypeRegistry.Register(typeof(ProbeOpenGenericPage<int>));
        Assert.NotNull(ReactorPageTypeRegistry.Resolve(typeof(ProbeOpenGenericPage<int>)));
    }

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

        var first = resolved.ActivateInstance();
        var second = resolved.ActivateInstance();

        // An activator stubbed to return default gives null and fails the type check...
        var typedFirst = Assert.IsType<ProbeActivatedPage>(first);
        Assert.IsType<ProbeActivatedPage>(second);

        // ...one that fabricated the object without running the constructor fails this...
        Assert.True(typedFirst.ConstructorRan);

        // ...and one that handed back a cached singleton fails this. Together they pin
        // "constructs a fresh instance on every activation", which is what WinUI needs.
        Assert.NotSame(first, second);
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

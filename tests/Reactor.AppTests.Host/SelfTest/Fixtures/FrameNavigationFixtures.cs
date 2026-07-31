using System.Collections.Generic;
using Microsoft.UI.Reactor.AppTests.Host.SelfTest;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Microsoft.UI.Reactor.AppTests.Host.SelfTest.Fixtures;

// ════════════════════════════════════════════════════════════════════════
//  Frame navigation — the guard, and what it deliberately refuses
// ════════════════════════════════════════════════════════════════════════
//
// WinUI resolves a Frame.Navigate target through Application.Current's
// IXamlMetadataProvider and dereferences the null it gets back, terminating the
// process with 0xC0000005 — not a managed exception, so nothing can catch it and
// Application.UnhandledException never fires.
//
// A Reactor app ships no .xaml, so the XAML compiler emits no <App>_XamlTypeInfo
// and every app-defined Page is invisible to that chain. Reactor therefore checks
// resolvability *before* calling Navigate and refuses when it would fault.
//
// Reactor deliberately does NOT make code-only pages resolvable: spec 011 makes
// UseNavigation<TRoute> + NavigationHost the navigation system and Frame an interop
// escape hatch for apps that already have XAML pages. Publishing synthesized
// metadata would breach spec 011's zero-XAML-dependency goal and would only
// partially work — three further Frame constraints remain (the IPage hard-cast,
// parameterless-constructor activation, and the absence of extension points).
//
// The fixtures below are built around one differential: identical code path, two
// target types, opposite outcomes, and the ONLY thing that differs is whether the
// XAML metadata chain can resolve the type.
//
//   typeof(Page)          framework type, in the core metadata provider  -> navigates
//   SelfTestCodeOnlyPage  app-defined, no .xaml, absent from metadata    -> refused

/// <summary>
/// Code-only navigation target — deliberately has no <c>.xaml</c>, so it is exactly
/// what the guard exists to refuse. Never successfully navigated to; only its
/// <c>Type</c> reaches WinUI.
/// </summary>
internal sealed partial class SelfTestCodeOnlyPage : Page { }

/// <summary>Second code-only target, to prove assertions track the requested type.</summary>
internal sealed partial class SelfTestOtherCodeOnlyPage : Page { }

internal static class FrameNavigationFixtures
{
    // ════════════════════════════════════════════════════════════════════════
    //  1. THE REGRESSION TEST. A code-only Page is refused, not fatal.
    // ════════════════════════════════════════════════════════════════════════
    //
    // Without the guard this fixture does not fail — it takes the whole test host
    // down with an access violation, producing no TAP output at all.

    internal class CodeOnlyPageRefusedNotFatal(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var failures = new List<(Type Page, Exception Error)>();
            var navigated = new List<Type>();

            var host = H.CreateHost();
            host.Mount(_ => VStack(
                Frame(typeof(SelfTestCodeOnlyPage))
                    .Navigated(navigated.Add)
                    .NavigationFailed((t, ex) => failures.Add((t, ex)))
                    .Height(120)));

            // Reaching the next line at all is the headline result: pre-fix the process
            // is gone by here and nothing below ever runs.
            await Harness.Render();

            H.Check("FrameNav_HostSurvivedUnresolvableTarget", true);

            H.Check("FrameNav_RefusalReportedOnce", failures.Count == 1);
            H.Check("FrameNav_RefusalCarriesRequestedType",
                failures.Count == 1 && failures[0].Page == typeof(SelfTestCodeOnlyPage));
            H.Check("FrameNav_RefusalIsNotSomeOtherPage",
                failures.Count == 1 && failures[0].Page != typeof(SelfTestOtherCodeOnlyPage));

            // Never handed to WinUI, so no navigation can have succeeded.
            H.Check("FrameNav_RefusedNavigationDidNotFireNavigated", navigated.Count == 0);

            // The message must redirect, not merely report — this is the whole user-facing
            // value of refusing instead of crashing.
            var message = failures.Count == 1 ? failures[0].Error.Message : "";
            H.Check("FrameNav_RefusalNamesTheType",
                message.Contains(typeof(SelfTestCodeOnlyPage).FullName!, StringComparison.Ordinal));
            H.Check("FrameNav_RefusalPointsAtUseNavigation",
                message.Contains("UseNavigation", StringComparison.Ordinal));
            H.Check("FrameNav_RefusalExplainsTheXamlRequirement",
                message.Contains(".xaml", StringComparison.Ordinal));

            // The Frame itself stays in the tree — a refused navigation is not a mount failure.
            var frame = H.FindControl<Frame>(_ => true);
            H.Check("FrameNav_FrameStillMountedAfterRefusal", frame is not null);
            H.Check("FrameNav_ContentLeftEmptyAfterRefusal", frame?.Content is null);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  2. THE CONTROL. A resolvable Page still navigates.
    // ════════════════════════════════════════════════════════════════════════
    //
    // Without this, fixture 1 is satisfied by a guard that refuses everything.

    internal class ResolvablePageStillNavigates(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var navigated = new List<Type>();
            var failures = new List<Type>();

            var host = H.CreateHost();
            host.Mount(_ => VStack(
                Frame(typeof(Page))
                    .Navigated(navigated.Add)
                    .NavigationFailed((t, _) => failures.Add(t))
                    .Height(120)));

            await Harness.Render();

            var frame = H.FindControl<Frame>(_ => true);
            H.Check("FrameNavOk_FrameMounted", frame is not null);

            // The real page instance, not merely "something non-null" — proves WinUI both
            // resolved and activated it.
            H.Check("FrameNavOk_ContentIsThePage", frame?.Content is Page);
            H.Check("FrameNavOk_NoFailureReported", failures.Count == 0);

            // Navigated fires for the mount-time navigation. This is the other half of the
            // fix: the navigate moved out of the prop loop (which ran before any event was
            // subscribed) into AfterChildrenMount, so these callbacks can observe it at all.
            H.Check("FrameNavOk_NavigatedFiredOnce", navigated.Count == 1);
            H.Check("FrameNavOk_NavigatedCarriesRequestedType",
                navigated.Count == 1 && navigated[0] == typeof(Page));
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  3. Navigating precedes Navigated for the mount-time navigation.
    // ════════════════════════════════════════════════════════════════════════

    internal class NavigatingPrecedesNavigated(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var order = new List<string>();

            var host = H.CreateHost();
            host.Mount(_ => VStack(
                Frame(typeof(Page))
                    .Navigating(t => order.Add("navigating:" + t.Name))
                    .Navigated(t => order.Add("navigated:" + t.Name))
                    .Height(120)));

            await Harness.Render();

            // Both firing at all is the mount-ordering fix; before it the navigation
            // completed while the event list was still empty.
            H.Check("FrameNavOrder_BothFired", order.Count == 2);
            H.Check("FrameNavOrder_NavigatingFirst",
                order.Count == 2 && order[0] == "navigating:" + nameof(Page));
            H.Check("FrameNavOrder_NavigatedSecond",
                order.Count == 2 && order[1] == "navigated:" + nameof(Page));
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  4. Navigation is mount-only: an unrelated update must not re-navigate.
    // ════════════════════════════════════════════════════════════════════════

    internal class UpdateDoesNotRenavigate(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var navigated = new List<Type>();

            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var (bumped, setBumped) = ctx.UseState(false);
                return VStack(
                    // No .WithKey: the same Frame control is reconciled in place, so a
                    // re-navigation would have to come from the descriptor, not a remount.
                    Frame(typeof(Page)).Navigated(navigated.Add).Height(bumped ? 130 : 120),
                    Button("Bump", () => setBumped(true)));
            });

            await Harness.Render();
            var frameBefore = H.FindControl<Frame>(_ => true);
            H.Check("FrameNavUpdate_NavigatedOnceOnMount", navigated.Count == 1);

            H.ClickButton("Bump");
            await Harness.Render();

            var frameAfter = H.FindControl<Frame>(_ => true);

            // Guards the premise: if the control were remounted, the "no re-navigation"
            // assertion below would be measuring the wrong thing.
            H.Check("FrameNavUpdate_SameFrameInstanceReused", ReferenceEquals(frameBefore, frameAfter));
            H.Check("FrameNavUpdate_UpdateActuallyApplied", frameAfter?.Height == 130);

            // Navigation is mount-only by design (spec 058 §15 P5.6) — re-running it on
            // every record-`with` would re-navigate on unrelated state changes.
            H.Check("FrameNavUpdate_NoSecondNavigation", navigated.Count == 1);
            H.Check("FrameNavUpdate_ContentUnchanged", frameAfter?.Content is Page);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  5. Without a handler the refusal surfaces rather than being swallowed.
    // ════════════════════════════════════════════════════════════════════════

    internal class RefusalWithoutHandlerSurfacesError(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            // Same unresolvable target as fixture 1, but with no .NavigationFailed(...)
            // wired. Marking the failure handled is gated on the element having a handler,
            // so with none it stays unhandled and surfaces as an ordinary managed exception
            // — which keeps it debuggable. ReactorHost's render guard turns that into the
            // standard error fallback instead of the process kill an unguarded Navigate
            // would have produced.
            var host = H.CreateHost();
            host.Mount(_ => VStack(
                Frame(typeof(SelfTestCodeOnlyPage)).Height(120)));

            await Harness.Render();

            // ReactorHost.ShowErrorFallback renders "Render error: {Type}: {Message}".
            // Matching on the redirect text proves the refusal reason propagated intact
            // rather than being replaced by a generic mount error.
            var errorHeader = H.FindControl<TextBlock>(
                tb => tb.Text.StartsWith("Render error:", StringComparison.Ordinal)
                      && tb.Text.Contains("UseNavigation", StringComparison.Ordinal));
            H.Check("FrameNavNoHandler_ErrorFallbackReportsRefusalReason", errorHeader is not null);

            // The differential against fixture 1: with a handler wired the Frame stays in
            // the tree; without one the mount was abandoned, so it never reached the tree.
            H.Check("FrameNavNoHandler_FrameNeverReachedTree",
                H.FindControl<Frame>(_ => true) is null);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  6. Reactor does NOT publish app pages into the metadata chain.
    // ════════════════════════════════════════════════════════════════════════
    //
    // Pins spec 011 Goal 3 ("zero XAML dependency — no .xaml files, no
    // IXamlMetadataProvider"). This fails if anyone reintroduces a publishing layer
    // to make code-only Frame navigation work.

    internal class ReactorDoesNotPublishAppPages(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var provider = Application.Current as Microsoft.UI.Xaml.Markup.IXamlMetadataProvider;
            H.Check("FrameNavMeta_AppIsMetadataProvider", provider is not null);

            // Guards the premise: a provider that returned null for *everything* would make
            // the null assertions below vacuously true. Note this cannot use a framework type
            // like Page — those resolve natively in the WinRT type system and the managed
            // provider legitimately returns null for them, which is precisely why the guard
            // has a separate WinRT-projection arm.
            var resolvesSomething =
                provider?.GetXamlType("Microsoft.UI.Xaml.Controls.Button") is not null
                || provider?.GetXamlType("Microsoft.UI.Xaml.Controls.TextBlock") is not null
                || provider?.GetXamlType("Microsoft.UI.Xaml.Controls.Grid") is not null;
            H.Check("FrameNavMeta_ProviderIsNotADeadEnd", resolvesSomething);

            H.Check("FrameNavMeta_CodeOnlyPageUnresolvedBefore",
                provider?.GetXamlType(typeof(SelfTestCodeOnlyPage)) is null);

            var host = H.CreateHost();
            host.Mount(_ => VStack(
                Frame(typeof(SelfTestCodeOnlyPage))
                    .NavigationFailed((_, _) => { })
                    .Height(120)));
            await Harness.Render();

            // Mounting a Frame at a code-only page must leave the chain untouched — by Type
            // and by full name, which is the key WinUI actually looks up by.
            H.Check("FrameNavMeta_CodeOnlyPageStillUnresolvedAfterMount",
                provider?.GetXamlType(typeof(SelfTestCodeOnlyPage)) is null);
            H.Check("FrameNavMeta_CodeOnlyPageStillUnresolvedByName",
                provider?.GetXamlType(typeof(SelfTestCodeOnlyPage).FullName!) is null);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  7. XamlPageElement goes through the same guard.
    // ════════════════════════════════════════════════════════════════════════

    internal class XamlPageUsesTheSameGuard(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var host = H.CreateHost();
            host.Mount(_ => VStack(
                new Microsoft.UI.Reactor.Hosting.XamlPageElement(typeof(Page))));

            await Harness.Render();

            var frame = H.FindControl<Frame>(_ => true);
            H.Check("XamlPage_FrameMounted", frame is not null);
            H.Check("XamlPage_ResolvablePageNavigated", frame?.Content is Page);
        }
    }
}

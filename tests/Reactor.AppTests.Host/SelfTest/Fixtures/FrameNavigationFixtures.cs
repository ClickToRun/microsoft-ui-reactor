using System.Collections.Generic;
using Microsoft.UI.Reactor.AppTests.Host.SelfTest;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using static Microsoft.UI.Reactor.Factories;

namespace Microsoft.UI.Reactor.AppTests.Host.SelfTest.Fixtures;

// ════════════════════════════════════════════════════════════════════════
//  Frame navigation to code-only Page types
// ════════════════════════════════════════════════════════════════════════
//
// A Reactor app has no .xaml files, so the XAML compiler emits no
// <App>_XamlTypeInfo provider for it and every app-defined Page is invisible to
// Application.Current's IXamlMetadataProvider chain. Frame.Navigate resolves its
// target through exactly that chain and dereferences the null it gets back —
// killing the process with an access violation (0xC0000005) that
// Application.UnhandledException never sees.
//
// These fixtures pin the two halves of the fix: Reactor publishes the target type
// to the metadata chain so navigation genuinely works, and the navigation runs
// late enough in mount that the Navigating/Navigated/NavigationFailed callbacks
// are already subscribed.

/// <summary>Code-only navigation target — deliberately has no <c>.xaml</c>.</summary>
internal sealed partial class SelfTestFramePage : Page
{
    internal const string Marker = "SelfTestFramePage-content";

    public SelfTestFramePage()
    {
        Content = new TextBlock { Text = Marker };
        Background = new SolidColorBrush(Color.FromArgb(0x10, 0x00, 0x80, 0xFF));
    }
}

/// <summary>Second code-only target, used to prove the assertion tracks the requested type.</summary>
internal sealed partial class SelfTestFrameOtherPage : Page
{
    public SelfTestFrameOtherPage() => Content = new TextBlock { Text = "SelfTestFrameOtherPage-content" };
}

/// <summary>Throws from its constructor so navigation fails the way a real broken page would.</summary>
internal sealed partial class SelfTestFrameThrowingPage : Page
{
    internal const string FailureMessage = "SelfTestFrameThrowingPage ctor failed on purpose";

    public SelfTestFrameThrowingPage()
        => throw new global::System.InvalidOperationException(FailureMessage);
}

/// <summary>Never navigated to, so it must never appear in the metadata chain.</summary>
internal sealed partial class SelfTestFrameNeverNavigatedPage : Page { }

internal static class FrameNavigationFixtures
{
    // ════════════════════════════════════════════════════════════════════════
    //  1. A code-only Page actually loads into the Frame.
    // ════════════════════════════════════════════════════════════════════════

    internal class CodeOnlyPageNavigates(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var navigatedTo = new List<Type>();

            var host = H.CreateHost();
            host.Mount(_ => VStack(
                Frame(typeof(SelfTestFramePage))
                    .Navigated(navigatedTo.Add)
                    .Height(120)));

            await Harness.Render();

            var frame = H.FindControl<Frame>(_ => true);
            H.Check("FrameNav_FrameMounted", frame is not null);

            // The page instance itself — not merely "something non-null" — is what proves the
            // synthesized IXamlType was resolved AND activated by native WinUI.
            H.Check("FrameNav_ContentIsRequestedPage", frame?.Content is SelfTestFramePage);

            // ...and its own content survived construction, so the page really ran.
            H.Check("FrameNav_PageContentRendered",
                (frame?.Content as SelfTestFramePage)?.Content is TextBlock tb
                && tb.Text == SelfTestFramePage.Marker);

            // Navigated fires for the *mount-time* navigation. This is the half of the fix that
            // moved the navigate out of the prop loop (which ran before any event subscribed)
            // into AfterChildrenMount.
            H.Check("FrameNav_NavigatedFiredOnce", navigatedTo.Count == 1);
            H.Check("FrameNav_NavigatedCarriesRequestedType",
                navigatedTo.Count == 1 && navigatedTo[0] == typeof(SelfTestFramePage));
            H.Check("FrameNav_NavigatedIsNotSomeOtherPage",
                navigatedTo.Count == 1 && navigatedTo[0] != typeof(SelfTestFrameOtherPage));
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  2. Navigating precedes Navigated for the mount-time navigation.
    // ════════════════════════════════════════════════════════════════════════

    internal class NavigatingPrecedesNavigated(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var order = new List<string>();

            var host = H.CreateHost();
            host.Mount(_ => VStack(
                Frame(typeof(SelfTestFrameOtherPage))
                    .Navigating(t => order.Add("navigating:" + t.Name))
                    .Navigated(t => order.Add("navigated:" + t.Name))
                    .Height(120)));

            await Harness.Render();

            H.Check("FrameNavOrder_BothFired", order.Count == 2);
            H.Check("FrameNavOrder_NavigatingFirst",
                order.Count == 2 && order[0] == "navigating:" + nameof(SelfTestFrameOtherPage));
            H.Check("FrameNavOrder_NavigatedSecond",
                order.Count == 2 && order[1] == "navigated:" + nameof(SelfTestFrameOtherPage));
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  3. A page whose ctor throws degrades into OnNavigationFailed.
    // ════════════════════════════════════════════════════════════════════════

    internal class ThrowingPageRaisesNavigationFailed(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var failures = new List<(Type Page, Exception Error)>();
            var navigated = new List<Type>();

            var host = H.CreateHost();
            host.Mount(_ => VStack(
                Frame(typeof(SelfTestFrameThrowingPage))
                    .Navigated(navigated.Add)
                    .NavigationFailed((t, ex) => failures.Add((t, ex)))
                    .Height(120)));

            // Reaching this line at all is a result: without the fix the failing navigation
            // escapes the mount pass instead of being reported.
            await Harness.Render();

            H.Check("FrameNavFail_ReportedOnce", failures.Count == 1);
            H.Check("FrameNavFail_CarriesRequestedType",
                failures.Count == 1 && failures[0].Page == typeof(SelfTestFrameThrowingPage));

            // The page's own exception, not Activator's TargetInvocationException wrapper.
            H.Check("FrameNavFail_CarriesPageException",
                failures.Count == 1
                && failures[0].Error.Message == SelfTestFrameThrowingPage.FailureMessage);

            H.Check("FrameNavFail_NavigatedDidNotFire", navigated.Count == 0);

            var frame = H.FindControl<Frame>(_ => true);
            H.Check("FrameNavFail_FrameStillMounted", frame is not null);
            H.Check("FrameNavFail_ContentLeftEmpty", frame?.Content is null);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  4. Navigation is mount-only: an update must not re-navigate.
    // ════════════════════════════════════════════════════════════════════════

    internal class UpdateDoesNotRenavigate(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var navigatedTo = new List<Type>();

            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var (swapped, setSwapped) = ctx.UseState(false);
                return VStack(
                    // No .WithKey: the same Frame control is reconciled in place, so a
                    // re-navigation would have to come from the descriptor, not a remount.
                    // The page type is a `typeof` in each branch rather than state-held, so
                    // the PublicParameterlessConstructor annotation on Frame(...) is
                    // satisfied under trim/AOT analysis.
                    Frame(swapped ? typeof(SelfTestFrameOtherPage) : typeof(SelfTestFramePage))
                        .Navigated(navigatedTo.Add).Height(120),
                    Button("Swap", () => setSwapped(true)));
            });

            await Harness.Render();
            var frameBefore = H.FindControl<Frame>(_ => true);
            H.Check("FrameNavUpdate_NavigatedOnceOnMount", navigatedTo.Count == 1);

            H.ClickButton("Swap");
            await Harness.Render();

            var frameAfter = H.FindControl<Frame>(_ => true);

            // Guards the premise: if the control were remounted the "no re-navigation"
            // assertion below would be measuring the wrong thing.
            H.Check("FrameNavUpdate_SameFrameInstanceReused", ReferenceEquals(frameBefore, frameAfter));

            // Navigation is mount-only by design (spec 058 §15 P5.6) — re-running it on
            // every record-`with` would re-navigate on unrelated state changes.
            H.Check("FrameNavUpdate_NoSecondNavigation", navigatedTo.Count == 1);
            H.Check("FrameNavUpdate_ContentUnchanged", frameAfter?.Content is SelfTestFramePage);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  5. Without an OnNavigationFailed handler the failure is NOT marked handled.
    // ════════════════════════════════════════════════════════════════════════

    internal class ThrowingPageWithoutHandlerSurfacesError(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            // Same throwing page as fixture 3, but with no .NavigationFailed(...) wired.
            // The agreed semantic is that the failure then surfaces as an ordinary managed
            // exception rather than being silently swallowed — ReactorHost's render guard
            // turns it into the standard error fallback instead of a process kill (which is
            // what an unguarded Frame.Navigate to an unresolvable type used to produce).
            var host = H.CreateHost();
            host.Mount(_ => VStack(
                Frame(typeof(SelfTestFrameThrowingPage)).Height(120)));

            await Harness.Render();

            // ReactorHost.ShowErrorFallback renders "Render error: {Type}: {Message}".
            // Matching on the page's own message proves the failure propagated out of mount
            // AND that the reported exception is the page's, not Activator's wrapper.
            var errorHeader = H.FindControl<Microsoft.UI.Xaml.Controls.TextBlock>(
                tb => tb.Text.StartsWith("Render error:", StringComparison.Ordinal)
                      && tb.Text.Contains(SelfTestFrameThrowingPage.FailureMessage, StringComparison.Ordinal));
            H.Check("FrameNavNoHandler_ErrorFallbackReportsPageException", errorHeader is not null);

            // The differential against fixture 3: with a handler wired the Frame stays in the
            // tree (content null) and no error fallback appears; without one the mount was
            // abandoned, so the Frame never reached the tree at all.
            H.Check("FrameNavNoHandler_FrameNeverReachedTree",
                H.FindControl<Frame>(_ => true) is null);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  6. The synthesized metadata is reachable through Application.Current.
    // ════════════════════════════════════════════════════════════════════════

    internal class MetadataChainResolvesRegisteredPageOnly(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var provider = Application.Current as Microsoft.UI.Xaml.Markup.IXamlMetadataProvider;
            H.Check("FrameNavMeta_AppIsMetadataProvider", provider is not null);

            // Before anything is published, a code-only page is invisible — this is the
            // pre-fix state that made Frame.Navigate fault.
            H.Check("FrameNavMeta_UnpublishedTypeUnresolved",
                provider?.GetXamlType(typeof(SelfTestFrameNeverNavigatedPage)) is null);

            var host = H.CreateHost();
            host.Mount(_ => VStack(Frame(typeof(SelfTestFramePage)).Height(120)));
            await Harness.Render();

            // Mounting the Frame published its target, and the publication is reachable
            // through the real chain WinUI consults — by Type and by full name.
            var byType = provider?.GetXamlType(typeof(SelfTestFramePage));
            H.Check("FrameNavMeta_PublishedTypeResolvesByType", byType is not null);
            H.Check("FrameNavMeta_ResolvedTypeIsTheRequestedOne",
                byType?.UnderlyingType == typeof(SelfTestFramePage));
            H.Check("FrameNavMeta_PublishedTypeIsConstructible", byType?.IsConstructible == true);
            H.Check("FrameNavMeta_PublishedTypeResolvesByName",
                provider?.GetXamlType(typeof(SelfTestFramePage).FullName!) is not null);

            // Publishing one page must not turn the provider into a blanket resolver.
            H.Check("FrameNavMeta_StillUnresolvedForUnpublishedType",
                provider?.GetXamlType(typeof(SelfTestFrameNeverNavigatedPage)) is null);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  7. XamlPageElement goes through the same guarded navigation.
    // ════════════════════════════════════════════════════════════════════════

    internal class XamlPageHostsCodeOnlyPage(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var (swapped, setSwapped) = ctx.UseState(false);
                return VStack(
                    new Microsoft.UI.Reactor.Hosting.XamlPageElement(
                        swapped ? typeof(SelfTestFrameOtherPage) : typeof(SelfTestFramePage)),
                    Button("Swap", () => setSwapped(true)));
            });

            await Harness.Render();

            var frame = H.FindControl<Frame>(_ => true);
            H.Check("XamlPage_FrameMounted", frame is not null);
            H.Check("XamlPage_ContentIsRequestedPage", frame?.Content is SelfTestFramePage);

            // Unlike FrameElement, XamlPageElement re-navigates on update when the page
            // type changes — exercise that arm of the guarded path too.
            H.ClickButton("Swap");
            await Harness.Render();

            var frameAfter = H.FindControl<Frame>(_ => true);
            H.Check("XamlPage_ContentSwappedOnUpdate", frameAfter?.Content is SelfTestFrameOtherPage);
        }
    }
}

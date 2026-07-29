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
}

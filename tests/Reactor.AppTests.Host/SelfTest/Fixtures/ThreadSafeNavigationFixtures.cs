using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Navigation;
using Microsoft.UI.Reactor.AppTests.Host.SelfTest;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Microsoft.UI.Reactor.AppTests.Host.SelfTest.Fixtures;

/// <summary>
/// Selfhost tests for issue #234: <c>NavigationHandle&lt;TRoute&gt;</c> mutators
/// invoked off the UI thread must auto-marshal onto the captured dispatcher and
/// apply correctly. The unit tests in <c>ThreadSafeNavigationTests</c> only prove
/// off-thread <em>rejection</em> when no dispatcher exists; these fixtures drive a
/// real pumped WinUI <c>DispatcherQueue</c> and assert the happy path end-to-end —
/// the store ends in the right state and the component actually re-renders.
/// </summary>
internal static class ThreadSafeNavigationFixtures
{
    private enum NavRoute { Home, Detail, Settings }

    /// <summary>
    /// Mounts a component with <c>UseNavigation</c>, then calls <c>Navigate</c> and
    /// <c>Replace</c> from a background <c>Task.Run</c>. Verifies the navigation
    /// marshals onto the UI thread, the back/forward stacks end in the right state,
    /// and the bound component re-renders to reflect the new route.
    /// </summary>
    internal class NavigateOffThreadMarshals(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            NavigationHandle<NavRoute>? nav = null;

            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var handle = ctx.UseNavigation(NavRoute.Home);
                nav = handle;
                return TextBlock($"Route: {handle.CurrentRoute}");
            });

            await Harness.Render();
            H.Check("NavMarshal_Initial", H.FindText("Route: Home") is not null);

            // Navigate from a background thread — the exact #234 scenario that used
            // to mutate the List<T> backing store off-thread with no protection.
            var done = new TaskCompletionSource();
            _ = Task.Run(() =>
            {
                try
                {
                    nav!.Navigate(NavRoute.Detail);
                    done.TrySetResult();
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }
            });

            var winner = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            H.Check("NavMarshal_NavigateCompleted", winner == done.Task);
            if (winner == done.Task) await done.Task; // surface any captured exception

            // Drain the marshaled mutation + the rerender it requests.
            for (int i = 0; i < 4; i++) await Harness.Render();

            // The store ended in the right state: current advanced, Home pushed.
            H.Check("NavMarshal_NavigateCurrent", nav!.CurrentRoute.Equals(NavRoute.Detail));
            H.Check("NavMarshal_NavigateBackStack",
                nav!.BackStack.Count == 1 && nav.BackStack[0].Equals(NavRoute.Home));
            // ...and the rerender actually fired (not just the store mutated).
            H.Check("NavMarshal_NavigateRerendered", H.FindText("Route: Detail") is not null);

            // Replace from a background thread — a second mutator end-to-end.
            var done2 = new TaskCompletionSource();
            _ = Task.Run(() =>
            {
                try
                {
                    nav!.Replace(NavRoute.Settings);
                    done2.TrySetResult();
                }
                catch (Exception ex)
                {
                    done2.TrySetException(ex);
                }
            });

            var winner2 = await Task.WhenAny(done2.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            H.Check("NavMarshal_ReplaceCompleted", winner2 == done2.Task);
            if (winner2 == done2.Task) await done2.Task;

            for (int i = 0; i < 4; i++) await Harness.Render();

            // Replace swaps current without growing the back stack.
            H.Check("NavMarshal_ReplaceCurrent", nav!.CurrentRoute.Equals(NavRoute.Settings));
            H.Check("NavMarshal_ReplaceBackStack",
                nav!.BackStack.Count == 1 && nav.BackStack[0].Equals(NavRoute.Home));
            H.Check("NavMarshal_ReplaceRerendered", H.FindText("Route: Settings") is not null);
        }
    }
}

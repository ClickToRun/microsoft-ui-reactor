using Microsoft.UI.Reactor.AppTests.Host.SelfTest;
using Microsoft.UI.Reactor.Core;
using WinUI = Microsoft.UI.Xaml.Controls;
using WinPrim = Microsoft.UI.Xaml.Controls.Primitives;
using static Microsoft.UI.Reactor.Factories;

namespace Microsoft.UI.Reactor.AppTests.Host.SelfTest.Fixtures;

/// <summary>
/// Regression cover for the <c>Flyout(...)</c>-with-default-placement process kill.
///
/// Reactor's flyout elements default <c>Placement</c> to
/// <see cref="WinPrim.FlyoutPlacementMode.Auto"/> (13). WinUI's
/// <c>FlyoutBase::ShowAtCore</c> validates the effective placement through
/// <c>ValidateAndSetParameters</c>, whose switch only accepts 0..12 — so a flyout left at
/// <c>Auto</c> fails with <c>E_INVALIDARG</c> the moment it is shown and terminates the
/// process with a stowed <c>ArgumentException</c>. These fixtures actually *open* the
/// flyout, which is the only tier that catches it: without the guard the host process
/// fail-fasts and the whole TAP run dies.
/// </summary>
public static class FlyoutPlacementFixtures
{
    private static WinUI.Flyout? FlyoutOn(Harness h, string buttonLabel)
        => h.FindButton(buttonLabel)?.Flyout as WinUI.Flyout;

    // ────────────────────────────────────────────────────────────────────
    //  Flyout(...) left at the default placement must open, not fail-fast.
    // ────────────────────────────────────────────────────────────────────
    internal class Flyout_DefaultPlacement_Opens(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var host = H.CreateHost();
            host.Mount(_ => VStack(
                Flyout(Button("DefaultPlacementTarget"), TextBlock("default placement body"))));
            await Harness.Render();

            var flyout = FlyoutOn(H, "DefaultPlacementTarget");
            H.Check("FlyoutPlacement_Default_FlyoutAttached", flyout is not null);
            H.Check("FlyoutPlacement_Default_DpIsNotAuto",
                flyout is not null && flyout.Placement != WinPrim.FlyoutPlacementMode.Auto);

            // The load-bearing step: clicking the button routes into FlyoutBase::ShowAtCore.
            H.ClickButton("DefaultPlacementTarget");
            await Harness.WaitFor(() => flyout?.IsOpen == true);
            H.Check("FlyoutPlacement_Default_Opened", flyout?.IsOpen == true);

            flyout?.Hide();
            await Harness.Render();
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  Differential isolation: an explicit placement must still reach the DP.
    //  (A guard that swallowed every write would pass the fixture above.)
    // ────────────────────────────────────────────────────────────────────
    internal class Flyout_ExplicitPlacement_ReachesTheControl(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var host = H.CreateHost();
            host.Mount(_ => VStack(
                Flyout(Button("ExplicitPlacementTarget"), TextBlock("explicit placement body"))
                    with { Placement = WinPrim.FlyoutPlacementMode.RightEdgeAlignedTop }));
            await Harness.Render();

            var flyout = FlyoutOn(H, "ExplicitPlacementTarget");
            H.Check("FlyoutPlacement_Explicit_FlyoutAttached", flyout is not null);
            H.Check("FlyoutPlacement_Explicit_DpMatchesElement",
                flyout?.Placement == WinPrim.FlyoutPlacementMode.RightEdgeAlignedTop);

            H.ClickButton("ExplicitPlacementTarget");
            await Harness.WaitFor(() => flyout?.IsOpen == true);
            H.Check("FlyoutPlacement_Explicit_Opened", flyout?.IsOpen == true);

            flyout?.Hide();
            await Harness.Render();
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  Update path: an explicit placement lands on the already-mounted flyout,
    //  and going back to Auto never writes Auto (it keeps the last real value).
    // ────────────────────────────────────────────────────────────────────
    internal class Flyout_PlacementUpdate_NeverWritesAuto(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var (step, setStep) = ctx.UseState(0);
                var placement = step switch
                {
                    1 => WinPrim.FlyoutPlacementMode.Left,
                    _ => WinPrim.FlyoutPlacementMode.Auto,   // steps 0 and 2
                };
                return VStack(
                    Button("AdvancePlacement", () => setStep(step + 1)),
                    Flyout(Button("UpdatePlacementTarget"), TextBlock("update body"))
                        with { Placement = placement });
            });
            await Harness.Render();

            var flyout = FlyoutOn(H, "UpdatePlacementTarget");
            H.Check("FlyoutPlacement_Update_FlyoutAttached", flyout is not null);
            H.Check("FlyoutPlacement_Update_MountedNotAuto",
                flyout is not null && flyout.Placement != WinPrim.FlyoutPlacementMode.Auto);

            // Auto → Left: the explicit value must be pushed onto the live control.
            H.ClickButton("AdvancePlacement");
            await Harness.WaitFor(() => flyout?.Placement == WinPrim.FlyoutPlacementMode.Left);
            H.Check("FlyoutPlacement_Update_ExplicitApplied",
                flyout?.Placement == WinPrim.FlyoutPlacementMode.Left);

            // Left → Auto: documented no-reset semantic — the last real value stays,
            // and crucially Auto is never written back onto the DP.
            H.ClickButton("AdvancePlacement");
            await Harness.Render();
            H.Check("FlyoutPlacement_Update_AutoDoesNotOverwrite",
                flyout?.Placement == WinPrim.FlyoutPlacementMode.Left);

            H.ClickButton("UpdatePlacementTarget");
            await Harness.WaitFor(() => flyout?.IsOpen == true);
            H.Check("FlyoutPlacement_Update_OpensAfterUpdate", flyout?.IsOpen == true);

            flyout?.Hide();
            await Harness.Render();
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  ContentFlyout via .WithFlyout() — the second crashing entry point
    //  (Reconciler.CreateFlyoutFromElement / UpdateFlyoutInPlace).
    // ────────────────────────────────────────────────────────────────────
    internal class ContentFlyout_DefaultPlacement_Opens(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var host = H.CreateHost();
            host.Mount(_ => VStack(
                Button("ContentFlyoutTarget", () => { })
                    .WithFlyout(ContentFlyout(TextBlock("content flyout body")))));
            await Harness.Render();

            var flyout = FlyoutOn(H, "ContentFlyoutTarget");
            H.Check("FlyoutPlacement_ContentFlyout_FlyoutAttached", flyout is not null);
            H.Check("FlyoutPlacement_ContentFlyout_DpIsNotAuto",
                flyout is not null && flyout.Placement != WinPrim.FlyoutPlacementMode.Auto);

            H.ClickButton("ContentFlyoutTarget");
            await Harness.WaitFor(() => flyout?.IsOpen == true);
            H.Check("FlyoutPlacement_ContentFlyout_Opened", flyout?.IsOpen == true);

            flyout?.Hide();
            await Harness.Render();
        }
    }
}

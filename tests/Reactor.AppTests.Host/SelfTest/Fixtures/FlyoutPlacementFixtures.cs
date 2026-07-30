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
    //  The load-bearing platform assumption, measured rather than assumed.
    //
    //  The whole fix is "don't write Auto, leave the DP alone". That is only
    //  safe because the DP's own default is a value the show-time validator
    //  accepts. If a future Windows App SDK shipped FlyoutBase.Placement
    //  defaulting to Auto, skipping the write would silently stop protecting
    //  anything and the crash would come back — with every other test in this
    //  file still green, because they all assert "!= Auto" against a DP that
    //  would now BE Auto. So pin the platform default directly.
    // ────────────────────────────────────────────────────────────────────
    internal class Platform_FlyoutBase_PlacementDefault(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var flyout = new WinUI.Flyout();
            var menuFlyout = new WinUI.MenuFlyout();
            var commandBarFlyout = new WinUI.CommandBarFlyout();

            // Emitted as TAP comments so the measured values are in the log even
            // when the assertions pass — a future SDK bump shows up as a diff here.
            Console.WriteLine($"# measured Flyout.Placement default          = {flyout.Placement}");
            Console.WriteLine($"# measured MenuFlyout.Placement default      = {menuFlyout.Placement}");
            Console.WriteLine($"# measured CommandBarFlyout.Placement default = {commandBarFlyout.Placement}");

            H.Check("FlyoutPlacement_Platform_FlyoutDefaultIsTop",
                flyout.Placement == WinPrim.FlyoutPlacementMode.Top);
            H.Check("FlyoutPlacement_Platform_MenuFlyoutDefaultIsTop",
                menuFlyout.Placement == WinPrim.FlyoutPlacementMode.Top);
            H.Check("FlyoutPlacement_Platform_DefaultIsValidatorAccepted",
                (int)flyout.Placement is >= 0 and <= 12);

            await Harness.Render();
        }
    }

    /// <summary>
    /// Closes a flyout and waits for the close to land. Returning while a light-dismiss
    /// overlay is still up would leak it into the next in-process fixture.
    /// </summary>
    private static async Task HideAndSettle(WinPrim.FlyoutBase? flyout)
    {
        flyout?.Hide();
        await Harness.WaitFor(() => flyout?.IsOpen != true);
    }

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

            await HideAndSettle(flyout);
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

            await HideAndSettle(flyout);
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
                    // Render witness: proves each click actually produced a new render,
                    // so the step-2 "value did not change" assertion is not trivially true.
                    TextBlock($"PlacementStep={step}"),
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
            await Harness.WaitFor(() => H.FindText("PlacementStep=2") is not null);
            H.Check("FlyoutPlacement_Update_Step2Rendered", H.FindText("PlacementStep=2") is not null);
            H.Check("FlyoutPlacement_Update_AutoDoesNotOverwrite",
                flyout?.Placement == WinPrim.FlyoutPlacementMode.Left);

            H.ClickButton("UpdatePlacementTarget");
            await Harness.WaitFor(() => flyout?.IsOpen == true);
            H.Check("FlyoutPlacement_Update_OpensAfterUpdate", flyout?.IsOpen == true);

            await HideAndSettle(flyout);
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
            host.Mount(ctx =>
            {
                var (pinned, setPinned) = ctx.UseState(false);
                return VStack(
                    Button("PinContentFlyout", () => setPinned(true)),
                    Button("ContentFlyoutTarget", () => { })
                        .WithFlyout(ContentFlyout(
                            TextBlock("content flyout body"),
                            pinned ? WinPrim.FlyoutPlacementMode.Bottom : WinPrim.FlyoutPlacementMode.Auto)),
                    // Differential sibling: same CreateFlyoutFromElement arm, explicit
                    // placement. Without it, deleting the Apply call entirely would still
                    // satisfy the "not Auto" check above (an untouched DP reads Top).
                    Button("ContentFlyoutPinnedTarget", () => { })
                        .WithFlyout(ContentFlyout(
                            TextBlock("pinned content body"),
                            WinPrim.FlyoutPlacementMode.LeftEdgeAlignedBottom)));
            });
            await Harness.Render();

            var flyout = FlyoutOn(H, "ContentFlyoutTarget");
            H.Check("FlyoutPlacement_ContentFlyout_FlyoutAttached", flyout is not null);
            H.Check("FlyoutPlacement_ContentFlyout_DpIsNotAuto",
                flyout is not null && flyout.Placement != WinPrim.FlyoutPlacementMode.Auto);
            H.Check("FlyoutPlacement_ContentFlyout_CreateAppliesExplicit",
                FlyoutOn(H, "ContentFlyoutPinnedTarget")?.Placement
                    == WinPrim.FlyoutPlacementMode.LeftEdgeAlignedBottom);

            H.ClickButton("ContentFlyoutTarget");
            await Harness.WaitFor(() => flyout?.IsOpen == true);
            H.Check("FlyoutPlacement_ContentFlyout_Opened", flyout?.IsOpen == true);
            await HideAndSettle(flyout);

            // UpdateFlyoutInPlace's ContentFlyout arm — an explicit placement must land
            // on the flyout object that is already attached to the button.
            H.ClickButton("PinContentFlyout");
            await Harness.WaitFor(() => flyout?.Placement == WinPrim.FlyoutPlacementMode.Bottom);
            H.Check("FlyoutPlacement_ContentFlyout_UpdateAppliesExplicit",
                flyout?.Placement == WinPrim.FlyoutPlacementMode.Bottom);
            H.Check("FlyoutPlacement_ContentFlyout_UpdateReusedFlyout",
                ReferenceEquals(flyout, FlyoutOn(H, "ContentFlyoutTarget")));
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  MenuFlyout — the arms whose pre-existing `!= Auto` guards were folded
    //  into the shared choke point (CreateFlyoutFromElement + UpdateFlyoutInPlace).
    //  Driven through .WithContextFlyout(), which is its own reconciler path.
    // ────────────────────────────────────────────────────────────────────
    internal class MenuFlyout_ContextFlyout_DefaultPlacement(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var (pinned, setPinned) = ctx.UseState(false);
                return VStack(
                    Button("PinMenuPlacement", () => setPinned(true)),
                    Border(TextBlock("context menu target"))
                        .WithContextFlyout(pinned
                            ? MenuItems(WinPrim.FlyoutPlacementMode.Right, MenuItem("Copy"), MenuItem("Paste"))
                            : MenuItems(MenuItem("Copy"))),
                    // Differential sibling: same create arm, explicit placement, so the
                    // "not Auto" check above cannot be satisfied by deleting the write.
                    Border(TextBlock("pinned menu target"))
                        .WithContextFlyout(MenuItems(WinPrim.FlyoutPlacementMode.Full, MenuItem("Pinned"))));
            });
            await Harness.Render();

            var border = H.FindControl<Microsoft.UI.Xaml.Controls.Border>(
                b => b.Child is Microsoft.UI.Xaml.Controls.TextBlock tb && tb.Text == "context menu target");
            var menu = border?.ContextFlyout as WinUI.MenuFlyout;
            H.Check("FlyoutPlacement_MenuFlyout_ContextFlyoutAttached", menu is not null);
            H.Check("FlyoutPlacement_MenuFlyout_DpIsNotAuto",
                menu is not null && menu.Placement != WinPrim.FlyoutPlacementMode.Auto);

            var pinnedBorder = H.FindControl<Microsoft.UI.Xaml.Controls.Border>(
                b => b.Child is Microsoft.UI.Xaml.Controls.TextBlock tb && tb.Text == "pinned menu target");
            H.Check("FlyoutPlacement_MenuFlyout_CreateAppliesExplicit",
                (pinnedBorder?.ContextFlyout as WinUI.MenuFlyout)?.Placement
                    == WinPrim.FlyoutPlacementMode.Full);

            // UpdateFlyoutInPlace's MenuFlyout arm — explicit placement still lands.
            H.ClickButton("PinMenuPlacement");
            await Harness.WaitFor(() => menu?.Placement == WinPrim.FlyoutPlacementMode.Right);
            H.Check("FlyoutPlacement_MenuFlyout_UpdateAppliesExplicit",
                menu?.Placement == WinPrim.FlyoutPlacementMode.Right);
            H.Check("FlyoutPlacement_MenuFlyout_UpdateReusedFlyout",
                ReferenceEquals(menu, border?.ContextFlyout));
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  Button flyout slots — DropDownButton (MenuFlyout) and SplitButton
    //  (Flyout) both resolve through Reconciler.CreateFlyoutFromElement.
    // ────────────────────────────────────────────────────────────────────
    internal class ButtonFlyoutSlots_DefaultPlacement(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var host = H.CreateHost();
            host.Mount(_ => VStack(
                DropDownButton("DropDownSlot", MenuItems(MenuItem("One"), MenuItem("Two"))),
                SplitButton("SplitSlot", () => { }, ContentFlyout(TextBlock("split body"))),
                // Differential siblings: same create arms, explicit placements — so the
                // "not Auto" checks cannot be satisfied by deleting the write outright.
                DropDownButton("DropDownPinnedSlot",
                    MenuItems(WinPrim.FlyoutPlacementMode.BottomEdgeAlignedRight, MenuItem("Pinned"))),
                SplitButton("SplitPinnedSlot", () => { },
                    ContentFlyout(TextBlock("split pinned body"), WinPrim.FlyoutPlacementMode.Right))));
            await Harness.Render();

            var ddb = H.FindControl<WinUI.DropDownButton>(b => b.Content is string s && s == "DropDownSlot");
            var ddbFlyout = ddb?.Flyout as WinUI.MenuFlyout;
            H.Check("FlyoutPlacement_DropDownButton_MenuFlyoutAttached", ddbFlyout is not null);
            H.Check("FlyoutPlacement_DropDownButton_DpIsNotAuto",
                ddbFlyout is not null && ddbFlyout.Placement != WinPrim.FlyoutPlacementMode.Auto);
            H.Check("FlyoutPlacement_DropDownButton_CreateAppliesExplicit",
                (H.FindControl<WinUI.DropDownButton>(b => b.Content is string s && s == "DropDownPinnedSlot")
                    ?.Flyout as WinUI.MenuFlyout)?.Placement
                        == WinPrim.FlyoutPlacementMode.BottomEdgeAlignedRight);

            var split = H.FindControl<WinUI.SplitButton>(b => b.Content is string s && s == "SplitSlot");
            var splitFlyout = split?.Flyout as WinUI.Flyout;
            H.Check("FlyoutPlacement_SplitButton_FlyoutAttached", splitFlyout is not null);
            H.Check("FlyoutPlacement_SplitButton_DpIsNotAuto",
                splitFlyout is not null && splitFlyout.Placement != WinPrim.FlyoutPlacementMode.Auto);
            H.Check("FlyoutPlacement_SplitButton_CreateAppliesExplicit",
                (H.FindControl<WinUI.SplitButton>(b => b.Content is string s && s == "SplitPinnedSlot")
                    ?.Flyout as WinUI.Flyout)?.Placement == WinPrim.FlyoutPlacementMode.Right);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  CommandBarFlyout is DELIBERATELY excluded from the guard.
    //  Suppressing the write is not neutral: FlyoutBase.Placement defaults to Top,
    //  so "don't write" means the flyout pins to Top. Flyout/MenuFlyout want that
    //  (their validator rejects Auto outright), but CommandBarFlyout resolves Auto
    //  itself and auto-positions today — guarding it would silently move it to Top.
    //  This fixture pins the asymmetry so a later "consistency" cleanup cannot
    //  regress it, and so the DP is observably Auto rather than merely unguarded.
    // ────────────────────────────────────────────────────────────────────
    internal class CommandBarFlyout_KeepsAuto_Unguarded(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var host = H.CreateHost();
            host.Mount(_ => VStack(
                CommandBarFlyout(
                    Button("CbfButtonTarget", () => { }),
                    primaryCommands: [AppBarButton("Bold")]),
                // Differential sibling: an explicit placement must still land verbatim.
                CommandBarFlyout(
                    Button("CbfPinnedButtonTarget", () => { }),
                    primaryCommands: [AppBarButton("Italic")])
                    with { Placement = WinPrim.FlyoutPlacementMode.BottomEdgeAlignedLeft }));
            await Harness.Render();

            var cbfButton = H.FindButton("CbfButtonTarget");
            H.Check("FlyoutPlacement_CommandBarFlyout_TargetMounted", cbfButton is not null);

            var mounted = CommandBarFlyoutOn(cbfButton);
            H.Check("FlyoutPlacement_CommandBarFlyout_MountAttached", mounted is not null);
            H.Check("FlyoutPlacement_CommandBarFlyout_KeepsAuto",
                mounted?.Placement == WinPrim.FlyoutPlacementMode.Auto);

            H.Check("FlyoutPlacement_CommandBarFlyout_ExplicitStillApplies",
                CommandBarFlyoutOn(H.FindButton("CbfPinnedButtonTarget"))?.Placement
                    == WinPrim.FlyoutPlacementMode.BottomEdgeAlignedLeft);
        }

        // Reads both attachment slots: CommandBarFlyout uses SetAttachedFlyout today, but
        // that is being reworked to SetFlyoutOnControl — checking both keeps this fixture
        // valid across that change instead of failing for an unrelated reason.
        private static WinUI.CommandBarFlyout? CommandBarFlyoutOn(Microsoft.UI.Xaml.Controls.Button? button)
            => button is null
                ? null
                : (button.Flyout ?? WinPrim.FlyoutBase.GetAttachedFlyout(button)) as WinUI.CommandBarFlyout;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Flyout(...) whose Target element type changes — UpdateFlyoutElement's
    //  "create fresh" branch, which builds a brand-new WinUI.Flyout.
    // ────────────────────────────────────────────────────────────────────
    internal class Flyout_TargetTypeChange_FreshFlyoutNotAuto(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var (swapped, setSwapped) = ctx.UseState(false);
                return VStack(
                    Button("SwapFlyoutTarget", () => setSwapped(true)),
                    Flyout(
                        swapped
                            ? Border(TextBlock("FreshBorderTarget"))
                            : (Element)Button("FreshButtonTarget", () => { }),
                        TextBlock("fresh body")),
                    // Differential sibling: same fresh-create branch, explicit placement.
                    Flyout(
                        swapped
                            ? Border(TextBlock("FreshPinnedBorderTarget"))
                            : (Element)Button("FreshPinnedButtonTarget", () => { }),
                        TextBlock("fresh pinned body"))
                        with { Placement = WinPrim.FlyoutPlacementMode.TopEdgeAlignedRight });
            });
            await Harness.Render();

            H.Check("FlyoutPlacement_Fresh_MountedOnButton",
                FlyoutOn(H, "FreshButtonTarget") is not null);

            H.ClickButton("SwapFlyoutTarget");
            await Harness.WaitFor(() => AttachedFlyout(H, "FreshBorderTarget") is not null);

            var fresh = AttachedFlyout(H, "FreshBorderTarget");
            H.Check("FlyoutPlacement_Fresh_FlyoutAttached", fresh is not null);
            H.Check("FlyoutPlacement_Fresh_DpIsNotAuto",
                fresh is not null && fresh.Placement != WinPrim.FlyoutPlacementMode.Auto);
            H.Check("FlyoutPlacement_Fresh_AppliesExplicit",
                AttachedFlyout(H, "FreshPinnedBorderTarget")?.Placement
                    == WinPrim.FlyoutPlacementMode.TopEdgeAlignedRight);

            var target = FreshTarget(H, "FreshBorderTarget");
            if (target is not null) WinPrim.FlyoutBase.ShowAttachedFlyout(target);
            await Harness.WaitFor(() => fresh?.IsOpen == true);
            H.Check("FlyoutPlacement_Fresh_Opened", fresh?.IsOpen == true);

            await HideAndSettle(fresh);
        }

        private static Microsoft.UI.Xaml.Controls.Border? FreshTarget(Harness h, string markerText)
            => h.FindControl<Microsoft.UI.Xaml.Controls.Border>(
                b => b.Child is Microsoft.UI.Xaml.Controls.TextBlock tb && tb.Text == markerText);

        private static WinUI.Flyout? AttachedFlyout(Harness h, string markerText)
        {
            var target = FreshTarget(h, markerText);
            return target is null ? null : WinPrim.FlyoutBase.GetAttachedFlyout(target) as WinUI.Flyout;
        }
    }
}

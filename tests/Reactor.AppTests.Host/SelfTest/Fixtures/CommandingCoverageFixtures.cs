using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using static Microsoft.UI.Reactor.Factories;

namespace Microsoft.UI.Reactor.AppTests.Host.SelfTest.Fixtures;

/// <summary>
/// Mount-based fixtures for Phase 5 commanding coverage (spec 027 Tier 4).
/// Each fixture mounts a command-driven control, raises the native Click / toggle
/// event, and verifies the <see cref="Command"/> runs plus that Description /
/// AccessKey metadata flowed through to the mounted control.
/// </summary>
internal static class CommandingCoverageFixtures
{
    private static int _primaryClickCount;

    internal class SplitButtonCommandInvokesExecute(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            _primaryClickCount = 0;
            var cmd = new Command
            {
                Label = "Save",
                Execute = () => _primaryClickCount++,
                Description = "Saves the current doc",
                AccessKey = "S",
            };

            var host = H.CreateHost();
            host.Mount(ctx => SplitButton(cmd).Set(sb => sb.Name = "splitCmdBtn"));
            await Harness.Render();

            var sb = H.FindControl<SplitButton>(b => b.Name == "splitCmdBtn");
            H.Check("SplitButton_Command_Mounted", sb is not null);
            H.Check("SplitButton_Command_LabelContent", sb is not null && (sb.Content as string) == "Save");
            H.Check("SplitButton_Command_IsEnabled", sb is not null && sb.IsEnabled);
            H.Check("SplitButton_Command_AccessKeyFlowed", sb is not null && sb.AccessKey == "S");
        }
    }

    internal class HyperlinkButtonCommandInvokesExecute(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            int count = 0;
            var cmd = new Command { Label = "Details", Execute = () => count++ };

            var host = H.CreateHost();
            host.Mount(ctx => HyperlinkButton(cmd).Set(b => b.Name = "hlCmdBtn"));
            await Harness.Render();

            var hb = H.FindControl<HyperlinkButton>(b => b.Name == "hlCmdBtn");
            H.Check("HyperlinkButton_Command_Mounted", hb is not null);
            H.Check("HyperlinkButton_Command_Content", hb is not null && (hb.Content as string) == "Details");
            H.Check("HyperlinkButton_Command_Enabled", hb is not null && hb.IsEnabled);
        }
    }

    internal class ToggleButtonCommandFiresOnToggle(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            int count = 0;
            var cmd = new Command { Label = "Bold", Execute = () => count++ };

            var host = H.CreateHost();
            host.Mount(ctx => ToggleButton(cmd).Set(b => b.Name = "togCmdBtn"));
            await Harness.Render();

            var tb = H.FindControl<ToggleButton>(b => b.Name == "togCmdBtn");
            H.Check("ToggleButton_Command_Mounted", tb is not null);
            if (tb is not null)
            {
                // OnToggled binds to Click, which fires for real user toggles
                // (mouse, keyboard, and AutomationPeer.Invoke) — programmatic
                // IsChecked writes don't, by design. Simulate user toggles via
                // the toggle automation pattern.
                var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(tb);
                var toggle = peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Toggle)
                    as Microsoft.UI.Xaml.Automation.Provider.IToggleProvider;
                toggle?.Toggle();
                toggle?.Toggle();
            }
            H.Check("ToggleButton_Command_InvokedOnEachToggle", count == 2);
        }
    }

    internal class RepeatButtonCommandInvokesExecute(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var cmd = new Command
            {
                Label = "Tick",
                Execute = () => { },
                Description = "Tick helper",
                AccessKey = "T",
            };

            var host = H.CreateHost();
            host.Mount(ctx => RepeatButton(cmd).Set(b => b.Name = "repCmdBtn"));
            await Harness.Render();

            var rb = H.FindControl<RepeatButton>(b => b.Name == "repCmdBtn");
            H.Check("RepeatButton_Command_Mounted", rb is not null);
            H.Check("RepeatButton_Command_AccessKeyFlowed", rb is not null && rb.AccessKey == "T");
            H.Check("RepeatButton_Command_IsEnabled", rb is not null && rb.IsEnabled);
        }
    }

    internal class DisabledCommandDisablesControl(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var cmd = new Command { Label = "Save", Execute = () => { }, CanExecute = false };

            var host = H.CreateHost();
            host.Mount(ctx => SplitButton(cmd).Set(sb => sb.Name = "disabledSplit"));
            await Harness.Render();

            var sb = H.FindControl<SplitButton>(b => b.Name == "disabledSplit");
            H.Check("DisabledCmd_Mounted", sb is not null);
            H.Check("DisabledCmd_DisablesControl", sb is not null && !sb.IsEnabled);
        }
    }

    /// <summary>
    /// Issue #133 regression: a custom-content button bound via the
    /// <c>.Command(command)</c> modifier must re-apply <c>command.IsEnabled</c> to the
    /// live control on every update — not capture it once at construction. Mounts an
    /// icon-style (custom content) button whose command flips from enabled to disabled
    /// across a state-driven re-render and asserts the reused control's IsEnabled tracks it.
    /// </summary>
    internal class CustomContentCommandReappliesIsEnabledOnUpdate(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var (disabled, setDisabled) = ctx.UseState(false);
                var cmd = new Command { Label = "Run", Execute = () => { }, CanExecute = !disabled };
                return VStack(
                    Button("toggleCmdState", () => setDisabled(true)),
                    Button(TextBlock("Run")).Command(cmd).Set(b => b.Name = "cmdContentBtn"));
            });
            await Harness.Render();

            var btn = H.FindControl<Button>(b => b.Name == "cmdContentBtn");
            H.Check("CmdContent_Mounted", btn is not null);
            H.Check("CmdContent_InitiallyEnabled", btn is not null && btn.IsEnabled);

            H.ClickButton("toggleCmdState");
            await Harness.Render();

            var btn2 = H.FindControl<Button>(b => b.Name == "cmdContentBtn");
            H.Check("CmdContent_Reused", ReferenceEquals(btn, btn2));
            H.Check("CmdContent_DisabledAfterUpdate", btn2 is not null && !btn2.IsEnabled);
        }
    }

    /// <summary>
    /// The HyperlinkButton / RepeatButton / ToggleButton <c>.Command()</c> paths apply
    /// IsEnabled solely through the command-apply descriptor entry (they have no record
    /// IsEnabled prop like ButtonElement). When the bound command's <c>CanExecute</c> flips
    /// across a re-render, <see cref="Command"/> is no longer structurally equal modulo
    /// delegates, so the reconciler runs Update and the <c>OneWay&lt;Command?&gt;</c> entry
    /// re-applies <c>ApplyButtonBaseCommon</c> (issue #153 — typed Command property; replaces
    /// the per-render Setters array that previously forced the re-run). Mount each, flip
    /// CanExecute across a re-render, and assert the reused live control becomes disabled.
    /// </summary>
    internal class HyperlinkButtonCommandReappliesIsEnabledOnUpdate(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var (disabled, setDisabled) = ctx.UseState(false);
                var cmd = new Command { Label = "Run", Execute = () => { }, CanExecute = !disabled };
                return VStack(
                    Button("toggleHl", () => setDisabled(true)),
                    HyperlinkButton("Run").Command(cmd).Set(b => b.Name = "hlReapplyBtn"));
            });
            await Harness.Render();

            var hb = H.FindControl<HyperlinkButton>(b => b.Name == "hlReapplyBtn");
            H.Check("HlReapply_InitiallyEnabled", hb is not null && hb.IsEnabled);

            H.ClickButton("toggleHl");
            await Harness.Render();

            var hb2 = H.FindControl<HyperlinkButton>(b => b.Name == "hlReapplyBtn");
            H.Check("HlReapply_Reused", ReferenceEquals(hb, hb2));
            H.Check("HlReapply_DisabledAfterUpdate", hb2 is not null && !hb2.IsEnabled);
        }
    }

    internal class RepeatButtonCommandReappliesIsEnabledOnUpdate(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var (disabled, setDisabled) = ctx.UseState(false);
                var cmd = new Command { Label = "Tick", Execute = () => { }, CanExecute = !disabled };
                return VStack(
                    Button("toggleRep", () => setDisabled(true)),
                    RepeatButton("Tick").Command(cmd).Set(b => b.Name = "repReapplyBtn"));
            });
            await Harness.Render();

            var rb = H.FindControl<RepeatButton>(b => b.Name == "repReapplyBtn");
            H.Check("RepReapply_InitiallyEnabled", rb is not null && rb.IsEnabled);

            H.ClickButton("toggleRep");
            await Harness.Render();

            var rb2 = H.FindControl<RepeatButton>(b => b.Name == "repReapplyBtn");
            H.Check("RepReapply_Reused", ReferenceEquals(rb, rb2));
            H.Check("RepReapply_DisabledAfterUpdate", rb2 is not null && !rb2.IsEnabled);
        }
    }

    internal class ToggleButtonCommandReappliesIsEnabledOnUpdate(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var (disabled, setDisabled) = ctx.UseState(false);
                var cmd = new Command { Label = "Bold", Execute = () => { }, CanExecute = !disabled };
                return VStack(
                    Button("toggleTog", () => setDisabled(true)),
                    ToggleButton("Bold").Command(cmd).Set(b => b.Name = "togReapplyBtn"));
            });
            await Harness.Render();

            var tb = H.FindControl<ToggleButton>(b => b.Name == "togReapplyBtn");
            H.Check("TogReapply_InitiallyEnabled", tb is not null && tb.IsEnabled);

            H.ClickButton("toggleTog");
            await Harness.Render();

            var tb2 = H.FindControl<ToggleButton>(b => b.Name == "togReapplyBtn");
            H.Check("TogReapply_Reused", ReferenceEquals(tb, tb2));
            H.Check("TogReapply_DisabledAfterUpdate", tb2 is not null && !tb2.IsEnabled);
        }
    }

    /// <summary>
    /// PR review M1: a disabled command bound via <c>.Command()</c> must not override
    /// <c>.IsDisabledFocusable()</c> — the button stays IsEnabled=true (reachable via Tab,
    /// click suppressed by the trampoline) and dimmed (Opacity 0.4). Pinned in both modifier
    /// orderings since the fix is descriptor/record-owned, not capture-order dependent.
    /// </summary>
    internal class CommandDisabledFocusableStaysFocusable(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var cmd = new Command { Label = "Submit", Execute = () => { }, CanExecute = false };

            var host = H.CreateHost();
            host.Mount(ctx => Button(TextBlock("Submit"))
                .Command(cmd)
                .IsDisabledFocusable()
                .Set(b => b.Name = "cmdDfBtn"));
            await Harness.Render();

            var btn = H.FindControl<Button>(b => b.Name == "cmdDfBtn");
            H.Check("CmdDf_Mounted", btn is not null);
            // Disabled command + IsDisabledFocusable: must stay enabled (focusable) despite the
            // disabled command — the command setter must not clobber the descriptor coercion.
            H.Check("CmdDf_StaysFocusable", btn is not null && btn.IsEnabled);
            H.Check("CmdDf_Dimmed", btn is not null && global::System.Math.Abs(btn.Opacity - 0.4) < 0.001);
        }
    }

    internal class CommandDisabledFocusableStaysFocusableReverseOrder(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var cmd = new Command { Label = "Submit", Execute = () => { }, CanExecute = false };

            var host = H.CreateHost();
            host.Mount(ctx => Button(TextBlock("Submit"))
                .IsDisabledFocusable()
                .Command(cmd)
                .Set(b => b.Name = "cmdDfRevBtn"));
            await Harness.Render();

            var btn = H.FindControl<Button>(b => b.Name == "cmdDfRevBtn");
            H.Check("CmdDfRev_Mounted", btn is not null);
            H.Check("CmdDfRev_StaysFocusable", btn is not null && btn.IsEnabled);
            H.Check("CmdDfRev_Dimmed", btn is not null && global::System.Math.Abs(btn.Opacity - 0.4) < 0.001);
        }
    }

    /// <summary>
    /// Issue #153: the <c>Button(Command)</c> factory lowers Command to a typed property,
    /// applied by a descriptor entry. When the bound command changes across a re-render, the
    /// command metadata (AccessKey, IsEnabled) must update on the reused live control.
    /// </summary>
    internal class BoundButtonCommandChangeUpdatesMetadata(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var (flipped, setFlipped) = ctx.UseState(false);
                var cmd = flipped
                    ? new Command { Label = "Open", Execute = () => { }, AccessKey = "D", CanExecute = false }
                    : new Command { Label = "Open", Execute = () => { }, AccessKey = "S", CanExecute = true };
                return VStack(
                    Button("flipCmd", () => setFlipped(true)),
                    Button(cmd).Set(b => b.Name = "cmdChangeBtn"));
            });
            await Harness.Render();

            var btn = H.FindControl<Button>(b => b.Name == "cmdChangeBtn");
            H.Check("CmdChange_Mounted", btn is not null);
            H.Check("CmdChange_InitialAccessKey", btn is not null && btn.AccessKey == "S");
            H.Check("CmdChange_InitiallyEnabled", btn is not null && btn.IsEnabled);

            H.ClickButton("flipCmd");
            await Harness.Render();

            var btn2 = H.FindControl<Button>(b => b.Name == "cmdChangeBtn");
            H.Check("CmdChange_Reused", ReferenceEquals(btn, btn2));
            H.Check("CmdChange_AccessKeyUpdated", btn2 is not null && btn2.AccessKey == "D");
            H.Check("CmdChange_DisabledAfterUpdate", btn2 is not null && !btn2.IsEnabled);
        }
    }

    /// <summary>
    /// Issue #153 fast-path proof: when a command-bound button re-renders with a Command that
    /// is structurally equal modulo its Execute/ExecuteAsync delegates (a fresh instance each
    /// render with identical rendered fields but a new closure), <see cref="Element.ShallowEquals"/>
    /// returns true and the reconciler skips the command-apply entry entirely. Observable proof:
    /// <c>ApplyButtonBaseCommon</c> removes+re-adds a NEW <c>KeyboardAccelerator</c> instance when
    /// it runs, so a reference-equal accelerator across the re-render proves it did NOT run.
    /// </summary>
    internal class BoundButtonUnchangedCommandSkipsReapply(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var (n, setN) = ctx.UseState(0);
                // Fresh Command each render: identical rendered fields, brand-new Execute
                // delegate. Structurally equal modulo delegates ⇒ ShallowEquals fast-paths.
                var cmd = new Command
                {
                    Label = "Open",
                    Execute = () => { },
                    Accelerator = new KeyboardAcceleratorData(
                        global::Windows.System.VirtualKey.O, global::Windows.System.VirtualKeyModifiers.Control),
                    Description = "Open a file",
                };
                return VStack(
                    Button("bumpFastPath", () => setN(n + 1)),
                    Button(cmd));  // no .Set — a fresh Setters array each render would defeat ShallowEquals
            });
            await Harness.Render();

            var btn = H.FindControl<Button>(b => (b.Content as string) == "Open");
            H.Check("FastPath_Mounted", btn is not null && btn.KeyboardAccelerators.Count == 1);
            var accel0 = btn?.KeyboardAccelerators.Count == 1 ? btn.KeyboardAccelerators[0] : null;

            H.ClickButton("bumpFastPath");
            await Harness.Render();

            var btn2 = H.FindControl<Button>(b => (b.Content as string) == "Open");
            H.Check("FastPath_Reused", ReferenceEquals(btn, btn2));
            H.Check("FastPath_SkippedReapply",
                btn2 is not null && accel0 is not null
                && btn2.KeyboardAccelerators.Count == 1
                && ReferenceEquals(btn2.KeyboardAccelerators[0], accel0));
        }
    }
}

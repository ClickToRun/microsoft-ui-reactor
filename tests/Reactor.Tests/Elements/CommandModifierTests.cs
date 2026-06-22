using Microsoft.UI.Reactor.Core;
using Xunit;
using static Microsoft.UI.Reactor.Factories;

namespace Microsoft.UI.Reactor.Tests.Elements;

/// <summary>
/// Tests for the <c>.Command(Command)</c> fluent modifier (issue #133), which binds a
/// <see cref="Command"/>'s enabled state, click handler, and metadata onto an
/// already-built clickable element. This closes the custom-content gap where
/// <c>Button(content, onClick)</c> had no command binding and callers had to re-thread
/// <c>.IsEnabled(command.IsEnabled)</c> by hand.
/// </summary>
public class CommandModifierTests
{
    // ── (a) The modifier applies command.IsEnabled ──────────────────

    [Fact]
    public void Command_On_CustomContent_Button_Applies_IsEnabled_True()
    {
        var cmd = new Command { Label = "Run", Execute = () => { }, CanExecute = true };

        var el = Button(TextBlock("Run")).Command(cmd);

        Assert.True(el.IsEnabled);
    }

    [Fact]
    public void Command_On_CustomContent_Button_Applies_IsEnabled_False_When_Disabled()
    {
        var cmd = new Command { Label = "Run", Execute = () => { }, CanExecute = false };

        var el = Button(TextBlock("Run")).Command(cmd);

        Assert.False(el.IsEnabled);
    }

    [Fact]
    public void Command_Composes_With_CustomContent_Button()
    {
        var cmd = new Command { Label = "Run", Execute = () => { }, CanExecute = false };

        var el = Button(TextBlock("Run")).Command(cmd);

        Assert.False(el.IsEnabled);
        Assert.NotNull(el.ContentElement);
    }

    [Fact]
    public void Command_Appends_CommandBindings_Setter()
    {
        var cmd = new Command { Label = "Run", Execute = () => { } };

        var before = Button(TextBlock("Run"));
        var after = before.Command(cmd);

        // The ApplyButtonBaseCommon setter is appended; it runs on every reconcile
        // pass (mount AND update) which is what re-applies IsEnabled to the control.
        Assert.Equal(before.Setters.Length + 1, after.Setters.Length);
    }

    // ── (b) IsEnabled is re-applied on update when command flips ─────

    [Fact]
    public void Command_Tracks_IsEnabled_Across_Renders()
    {
        // Simulate the UseCommand IsExecuting flip: the SAME render expression with a
        // command whose IsEnabled flips must produce an element whose IsEnabled tracks
        // the current command state — not a value captured once at construction.
        static ButtonElement Render(Command c) => Button(TextBlock("Run")).Command(c);

        var enabled = new Command { Label = "Run", Execute = () => { }, CanExecute = true };
        var disabled = new Command { Label = "Run", Execute = () => { }, CanExecute = false };

        Assert.True(Render(enabled).IsEnabled);
        Assert.False(Render(disabled).IsEnabled);
    }

    [Fact]
    public void Command_Produces_Fresh_Setters_So_Reconciler_Reapplies()
    {
        // Element equality short-circuits Update when Setters are reference-equal
        // (see Element.cs ButtonElement equality). Each .Command() render allocates a
        // fresh Setters array, so the reconciler never skips re-applying the command's
        // IsEnabled — the crux of the bug, where the custom-content path captured state once.
        var cmd = new Command { Label = "Run", Execute = () => { } };

        var first = Button(TextBlock("Run")).Command(cmd);
        var second = Button(TextBlock("Run")).Command(cmd);

        Assert.False(ReferenceEquals(first.Setters, second.Setters));
    }

    // ── (c) Clicking invokes the command ────────────────────────────

    [Fact]
    public void Command_Wires_Click_To_Execute()
    {
        int count = 0;
        var cmd = new Command { Label = "Run", Execute = () => count++ };

        var el = Button(TextBlock("Run")).Command(cmd);
        Assert.NotNull(el.OnClick);
        el.OnClick!();

        Assert.Equal(1, count);
    }

    [Fact]
    public void Command_Wires_Click_To_ExecuteAsync_When_No_Sync_Execute()
    {
        int count = 0;
        var cmd = new Command { Label = "Run", ExecuteAsync = () => { count++; return Task.CompletedTask; } };

        var el = Button(TextBlock("Run")).Command(cmd);
        el.OnClick!();

        Assert.Equal(1, count);
    }

    // ── Non-Button clickables ───────────────────────────────────────

    [Fact]
    public void Command_Wires_HyperlinkButton_Click()
    {
        int count = 0;
        var cmd = new Command { Label = "Details", Execute = () => count++ };

        var el = HyperlinkButton("Details").Command(cmd);
        Assert.NotNull(el.OnClick);
        el.OnClick!();

        Assert.Equal(1, count);
    }

    [Fact]
    public void Command_Wires_RepeatButton_Click()
    {
        int count = 0;
        var cmd = new Command { Label = "Tick", Execute = () => count++ };

        var el = RepeatButton("Tick").Command(cmd);
        el.OnClick!();

        Assert.Equal(1, count);
    }

    [Fact]
    public void Command_Wires_ToggleButton_OnEachToggle()
    {
        int count = 0;
        var cmd = new Command { Label = "Bold", Execute = () => count++ };

        var el = ToggleButton("Bold").Command(cmd);
        Assert.NotNull(el.OnIsCheckedChanged);
        el.OnIsCheckedChanged!(true);
        el.OnIsCheckedChanged!(false);

        Assert.Equal(2, count);
    }

    [Fact]
    public void Command_On_AppBarButton_Maps_Execute_And_IsEnabled()
    {
        int count = 0;
        var cmd = new Command { Label = "Save", Execute = () => count++, CanExecute = false };

        var el = AppBarButton("Save").Command(cmd);

        Assert.False(el.IsEnabled);
        Assert.NotNull(el.OnClick);
        el.OnClick!();
        Assert.Equal(1, count);
    }

    // ── (M3) AppBarButton routes through CommandBindings.Invoke so async-only
    //         commands (ExecuteAsync, no sync Execute) fire instead of no-opping ──

    [Fact]
    public void Command_On_AppBarButton_Modifier_Wires_Click_To_ExecuteAsync_When_No_Sync_Execute()
    {
        int count = 0;
        var cmd = new Command { Label = "Save", ExecuteAsync = () => { count++; return Task.CompletedTask; } };

        var el = AppBarButton("Save").Command(cmd);
        Assert.NotNull(el.OnClick);
        el.OnClick!();

        Assert.Equal(1, count);
    }

    [Fact]
    public void AppBarButton_Command_Factory_Wires_Click_To_ExecuteAsync_When_No_Sync_Execute()
    {
        int count = 0;
        var cmd = new Command { Label = "Save", ExecuteAsync = () => { count++; return Task.CompletedTask; } };

        var el = AppBarButton(cmd);
        Assert.NotNull(el.OnClick);
        el.OnClick!();

        Assert.Equal(1, count);
    }

    // ── (M1) A disabled command must not override .IsDisabledFocusable() ─────
    //         The element keeps IsDisabledFocusable regardless of modifier order;
    //         the live-control coercion (IsEnabled stays true / reachable via Tab)
    //         is pinned by the CommandModifierDisabledFocusable* selftest fixtures.

    [Fact]
    public void Command_Before_IsDisabledFocusable_Keeps_DisabledFocusable()
    {
        var cmd = new Command { Label = "Run", Execute = () => { }, CanExecute = false };

        var el = Button(TextBlock("Run")).Command(cmd).IsDisabledFocusable();

        Assert.True(el.IsDisabledFocusable);
    }

    [Fact]
    public void IsDisabledFocusable_Before_Command_Keeps_DisabledFocusable()
    {
        var cmd = new Command { Label = "Run", Execute = () => { }, CanExecute = false };

        var el = Button(TextBlock("Run")).IsDisabledFocusable().Command(cmd);

        Assert.True(el.IsDisabledFocusable);
    }
}

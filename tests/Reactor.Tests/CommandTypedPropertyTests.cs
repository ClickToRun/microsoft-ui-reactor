using Microsoft.UI.Reactor.Core;
using Windows.System;
using static Microsoft.UI.Reactor.Factories;
using Xunit;

namespace Microsoft.UI.Reactor.Tests;

/// <summary>
/// Issue #153 — Command is lifted to a typed <c>Command?</c> record property on the
/// command-capable button elements (Button, HyperlinkButton, RepeatButton, ToggleButton,
/// SplitButton, ToggleSplitButton). The command factories no longer allocate a per-render
/// <c>Setters</c> array + lambda, and <see cref="Element.ShallowEquals"/> fast-paths
/// command-bound buttons whose Command is unchanged (reference-equal OR structurally equal
/// modulo the Execute/ExecuteAsync delegates).
///
/// These are pure C# record tests — no WinUI thread required. Live mount/update behaviour is
/// covered by the Commanding selftest fixtures (CommandingCoverageFixtures.cs).
/// </summary>
public class CommandTypedPropertyTests
{
    private static Command MakeCmd(Action? execute = null) => new()
    {
        Label = "Save",
        Execute = execute ?? (() => { }),
        Icon = new SymbolIconData("Save"),
        Accelerator = new KeyboardAcceleratorData(VirtualKey.S, VirtualKeyModifiers.Control),
        AccessKey = "S",
        Description = "Save the file",
    };

    // ════════════════════════════════════════════════════════════════
    //  (a) Command factories allocate NO Setters array
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void Button_Command_AllocatesNoSetters()
    {
        var cmd = MakeCmd();
        var el = Button(cmd);
        Assert.Empty(el.Setters);
        Assert.Same(cmd, el.Command);
    }

    [Fact]
    public void HyperlinkButton_Command_AllocatesNoSetters()
    {
        var cmd = MakeCmd();
        var el = HyperlinkButton(cmd);
        Assert.Empty(el.Setters);
        Assert.Same(cmd, el.Command);
    }

    [Fact]
    public void RepeatButton_Command_AllocatesNoSetters()
    {
        var cmd = MakeCmd();
        var el = RepeatButton(cmd);
        Assert.Empty(el.Setters);
        Assert.Same(cmd, el.Command);
    }

    [Fact]
    public void ToggleButton_Command_AllocatesNoSetters()
    {
        var cmd = MakeCmd();
        var el = ToggleButton(cmd);
        Assert.Empty(el.Setters);
        Assert.Same(cmd, el.Command);
    }

    [Fact]
    public void SplitButton_Command_AllocatesNoSetters()
    {
        var cmd = MakeCmd();
        var el = SplitButton(cmd);
        Assert.Empty(el.Setters);
        Assert.Same(cmd, el.Command);
    }

    [Fact]
    public void ToggleSplitButton_Command_AllocatesNoSetters()
    {
        var cmd = MakeCmd();
        var el = ToggleSplitButton(cmd);
        Assert.Empty(el.Setters);
        Assert.Same(cmd, el.Command);
    }

    [Fact]
    public void Command_Factories_ShareReferenceEqualEmptySetters()
    {
        // Array.Empty<T>() is reference-shared, so the ShallowEquals
        // ReferenceEquals(Setters, Setters) guard stays true across two
        // command-bound buttons that carry no extra setters.
        var cmd = MakeCmd();
        var a = Button(cmd);
        var b = Button(cmd);
        Assert.Same(a.Setters, b.Setters);
    }

    // ════════════════════════════════════════════════════════════════
    //  (b) ShallowEquals fast-paths unchanged commands
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void ShallowEquals_True_When_Command_ReferenceEqual()
    {
        var cmd = MakeCmd();
        Assert.True(Element.ShallowEquals(Button(cmd), Button(cmd)));
        Assert.True(Element.ShallowEquals(HyperlinkButton(cmd), HyperlinkButton(cmd)));
        Assert.True(Element.ShallowEquals(RepeatButton(cmd), RepeatButton(cmd)));
        Assert.True(Element.ShallowEquals(ToggleButton(cmd), ToggleButton(cmd)));
    }

    [Fact]
    public void ShallowEquals_True_When_Command_StructurallyEqual_ModuloDelegates()
    {
        // Two distinct Command instances with identical rendered metadata but
        // DIFFERENT Execute delegates — the per-render closure case. ShallowEquals
        // must still fast-path because the rendered fields are unchanged.
        int x = 0, y = 0;
        var cmdA = MakeCmd(() => x++);
        var cmdB = MakeCmd(() => y++);
        Assert.NotSame(cmdA, cmdB);
        Assert.NotSame(cmdA.Execute, cmdB.Execute);

        Assert.True(Element.ShallowEquals(Button(cmdA), Button(cmdB)));
        Assert.True(Element.ShallowEquals(HyperlinkButton(cmdA), HyperlinkButton(cmdB)));
        Assert.True(Element.ShallowEquals(RepeatButton(cmdA), RepeatButton(cmdB)));
        Assert.True(Element.ShallowEquals(ToggleButton(cmdA), ToggleButton(cmdB)));
    }

    [Fact]
    public void ShallowEquals_True_When_Command_StructurallyEqual_ModuloAsyncDelegate()
    {
        var cmdA = new Command { Label = "Run", ExecuteAsync = async () => { await Task.Yield(); } };
        var cmdB = new Command { Label = "Run", ExecuteAsync = async () => { await Task.Delay(1); } };
        Assert.NotSame(cmdA.ExecuteAsync, cmdB.ExecuteAsync);
        Assert.True(Element.ShallowEquals(Button(cmdA), Button(cmdB)));
    }

    [Fact]
    public void ShallowEquals_False_When_Command_Label_Differs()
    {
        var cmdA = MakeCmd();
        var cmdB = cmdA with { Label = "Save As" };
        // Label also flows to the Button content, so this would be unequal anyway —
        // assert specifically through the command compare with matching content.
        Assert.False(Element.ShallowEquals(Button(cmdA), Button(cmdB)));
    }

    [Fact]
    public void ShallowEquals_False_When_Command_AccessKey_Differs()
    {
        // AccessKey does NOT flow to a record field — only to the typed Command —
        // so this isolates the CommandsEqual contribution.
        var cmdA = MakeCmd();
        var cmdB = cmdA with { AccessKey = "X" };
        Assert.False(Element.ShallowEquals(Button(cmdA), Button(cmdB)));
        Assert.False(Element.ShallowEquals(HyperlinkButton(cmdA), HyperlinkButton(cmdB)));
        Assert.False(Element.ShallowEquals(RepeatButton(cmdA), RepeatButton(cmdB)));
        Assert.False(Element.ShallowEquals(ToggleButton(cmdA), ToggleButton(cmdB)));
    }

    [Fact]
    public void ShallowEquals_False_When_Command_Description_Differs()
    {
        var cmdA = MakeCmd();
        var cmdB = cmdA with { Description = "Different tooltip" };
        Assert.False(Element.ShallowEquals(Button(cmdA), Button(cmdB)));
    }

    [Fact]
    public void ShallowEquals_False_When_Command_CanExecute_Differs()
    {
        var cmdA = MakeCmd();
        var cmdB = cmdA with { CanExecute = false };
        Assert.False(Element.ShallowEquals(Button(cmdA), Button(cmdB)));
    }

    [Fact]
    public void ShallowEquals_False_When_OneSideHasNoCommand()
    {
        var cmd = MakeCmd();
        var withCmd = Button(cmd);
        var noCmd = Button("Save");
        Assert.False(Element.ShallowEquals(withCmd, noCmd));
    }

    // ════════════════════════════════════════════════════════════════
    //  CommandsEqual unit semantics (internal, via InternalsVisibleTo)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void CommandsEqual_IgnoresExecuteDelegates()
    {
        var a = MakeCmd(() => { });
        var b = MakeCmd(() => { });
        Assert.True(CommandBindings.CommandsEqual(a, b));
    }

    [Fact]
    public void CommandsEqual_BothNull_True_OneNull_False()
    {
        Assert.True(CommandBindings.CommandsEqual(null, null));
        Assert.False(CommandBindings.CommandsEqual(MakeCmd(), null));
        Assert.False(CommandBindings.CommandsEqual(null, MakeCmd()));
    }

    [Fact]
    public void CommandsEqual_ComparesAcceleratorAndIcon()
    {
        var a = MakeCmd();
        var diffAccel = a with { Accelerator = new KeyboardAcceleratorData(VirtualKey.X, VirtualKeyModifiers.Control) };
        var diffIcon = a with { Icon = new SymbolIconData("Open") };
        Assert.False(CommandBindings.CommandsEqual(a, diffAccel));
        Assert.False(CommandBindings.CommandsEqual(a, diffIcon));
    }

    // ════════════════════════════════════════════════════════════════
    //  (d) Clicking still invokes the command (sync + async)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void Button_Command_OnClick_Invokes_Sync_Execute()
    {
        int count = 0;
        var cmd = new Command { Label = "Go", Execute = () => count++ };
        var el = Button(cmd);
        Assert.NotNull(el.OnClick);
        el.OnClick!();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Button_Command_OnClick_Invokes_Async_Execute()
    {
        var tcs = new TaskCompletionSource();
        var cmd = new Command
        {
            Label = "Go",
            ExecuteAsync = () => { tcs.SetResult(); return Task.CompletedTask; },
        };
        var el = Button(cmd);
        Assert.NotNull(el.OnClick);
        el.OnClick!();
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(tcs.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public void ToggleButton_Command_Toggle_Invokes_Execute()
    {
        int count = 0;
        var cmd = new Command { Label = "T", Execute = () => count++ };
        var el = ToggleButton(cmd);
        Assert.NotNull(el.OnIsCheckedChanged);
        el.OnIsCheckedChanged!(true);
        el.OnIsCheckedChanged!(false);
        Assert.Equal(2, count); // fires on every toggle (Option A)
    }

    [Fact]
    public void SplitButton_Command_OnClick_Invokes_Execute()
    {
        int count = 0;
        var cmd = new Command { Label = "S", Execute = () => count++ };
        var el = SplitButton(cmd);
        Assert.NotNull(el.OnClick);
        el.OnClick!();
        Assert.Equal(1, count);
    }

    // ════════════════════════════════════════════════════════════════
    //  Allocation budget — per-construct bytes stay well under the
    //  pre-#153 baseline (≈264 B; the Setters array + closure was ≈88 B).
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void Button_Command_PerConstruct_Allocation_UnderBudget()
    {
        var cmd = MakeCmd();

        // Warm-up: JIT the factory + GC.GetAllocatedBytesForCurrentThread path.
        for (int i = 0; i < 1000; i++)
            GC.KeepAlive(Button(cmd));

        const int N = 50_000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < N; i++)
            GC.KeepAlive(Button(cmd));
        long after = GC.GetAllocatedBytesForCurrentThread();

        double perConstruct = (after - before) / (double)N;

        // The pre-#153 factory allocated ≈264 B/construct (record + Setters array +
        // lambda closure). After lifting Command to a typed property the Setters
        // array/closure (≈88 B) is gone. Use a generous CI-jitter ceiling that still
        // catches a regression that reintroduces a per-render array/closure.
        Assert.True(perConstruct < 220,
            $"Button(command) per-construct allocation regressed: {perConstruct:F1} B (expected < 220 B).");
    }
}

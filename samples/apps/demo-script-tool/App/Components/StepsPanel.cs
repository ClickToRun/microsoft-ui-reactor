using System;
using System.Collections.Generic;
using System.Linq;
using DemoScriptTool.App.Models;
using static Microsoft.UI.Reactor.Factories;

namespace DemoScriptTool.App.Components;

/// <summary>
/// The panel-level callbacks, wrapped in <see cref="Callbacks{T}"/> so their
/// per-render delegate identity never re-renders the panel (issue #151). Only
/// <see cref="StepsPanelProps.Model"/> / <see cref="StepsPanelProps.IsGenerating"/>
/// drive the memo decision.
/// </summary>
public sealed record StepsPanelCallbacks(
    Action<int, string> OnPromptChanged,
    Action<int, string> OnTitleChanged,
    Action<StepModel> OnRun,
    Action<StepModel> OnCopyDelta,
    Action OnAddStep,
    Action<StepModel> OnDeleteStep,
    Action<StepModel> OnRegenFromStep);

public sealed record StepsPanelProps(
    DemoScriptModel Model,
    bool IsGenerating,
    Callbacks<StepsPanelCallbacks> Cb);

/// <summary>
/// Vertical scroller of <see cref="StepCard"/> instances keyed by step number.
/// Subscribes to the model so step add/remove from external file edits
/// rebuild the list automatically (spec §Reconciliation).
/// </summary>
public sealed class StepsPanel : Component<StepsPanelProps>
{
    public override Element Render()
    {
        var (_, setRevision) = UseState(0, threadSafe: true);
        var counterRef = UseRef(0);

        // Forward callbacks as dispatch-time trampolines that read Props.Cb.Value
        // *at invoke time* (issue #151) — never capture the bundle into a render-time
        // local and bake those delegates into commands/child props. When this panel
        // memo-skips, the reconciler refreshes its live Props but does NOT re-render,
        // so any baked delegate would go stale; a trampoline that closes over `this`
        // and reads Props.Cb.Value on invoke always dispatches the current delegate.
        // The fresh per-render trampoline identity is free: the Add Command lives on
        // this panel, and StepCard wraps its callbacks in a Callbacks<T> too, so the
        // identity is ignored by the card's memo check.

        // StepsChanged only — the steps panel cares about Add/Remove, not about
        // typing in the demo title/prompt (which would otherwise re-render every
        // step card on every keystroke).
        UseEffect(() =>
        {
            void Handler() { counterRef.Current++; setRevision(counterRef.Current); }
            Props.Model.StepsChanged += Handler;
            return () => Props.Model.StepsChanged -= Handler;
        }, Props.Model);

        var steps = Props.Model.Steps;

        var addStepCmd = new Command
        {
            Label = "Add step",
            Execute = () => Props.Cb.Value.OnAddStep(),
            Icon = SymbolIcon("Add"),
            Description = "Append a new empty step at the end of the script",
            Accelerator = Accelerator(Windows.System.VirtualKey.Enter,
                Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift),
        };

        var addButton = Button(addStepCmd)
            .AutomationName("Add a new step")
            .HAlign(HorizontalAlignment.Stretch)
            .Margin(0, 4, 0, 0);

        if (steps.Count == 0)
        {
            return Border(
                VStack(12,
                    SubHeading("No steps yet").Foreground(Theme.SecondaryText),
                    TextBlock("Add steps below, or run Generate All once you have a few in mind.")
                        .Foreground(Theme.SecondaryText)
                        .TextWrapping(TextWrapping.Wrap),
                    addButton))
                .Padding(40)
                .HAlign(HorizontalAlignment.Center)
                .Landmark(Microsoft.UI.Xaml.Automation.Peers.AutomationLandmarkType.Main);
        }

        // Pass each card a reference to the prior step so its show-code mode can
        // bold lines that appeared since the previous step. Card 0 has no prior.
        var cards = steps
            .Select((s, idx) => (Element)Component<StepCard, StepCardProps>(new StepCardProps(
                s,
                idx > 0 ? steps[idx - 1] : null,
                steps.Count,
                Props.IsGenerating,
                new StepCardCallbacks(
                    (n, v) => Props.Cb.Value.OnPromptChanged(n, v),
                    (n, v) => Props.Cb.Value.OnTitleChanged(n, v),
                    step => Props.Cb.Value.OnRun(step),
                    step => Props.Cb.Value.OnCopyDelta(step),
                    step => Props.Cb.Value.OnDeleteStep(step),
                    step => Props.Cb.Value.OnRegenFromStep(step)))))
            .Append(addButton)
            .ToArray();

        return (ScrollViewer(VStack(cards))
            with
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            })
            .Padding(0, 0, 8, 0)
            .Landmark(Microsoft.UI.Xaml.Automation.Peers.AutomationLandmarkType.Main);
    }
}

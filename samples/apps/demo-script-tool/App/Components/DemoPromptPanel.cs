using DemoScriptTool.App.Models;
using static Microsoft.UI.Reactor.Factories;

namespace DemoScriptTool.App.Components;

/// <summary>
/// The demo-prompt callbacks, wrapped in <see cref="Callbacks{T}"/> so their
/// per-render delegate identity never re-renders the panel (issue #151). Only
/// <see cref="DemoPromptPanelProps.Model"/> drives the memo decision.
/// </summary>
public sealed record DemoPromptPanelCallbacks(
    System.Action<string> OnPromptChanged,
    System.Action<string> OnTitleChanged);

public sealed record DemoPromptPanelProps(
    DemoScriptModel Model,
    Callbacks<DemoPromptPanelCallbacks> Cb);

/// <summary>
/// Top-of-body region binding to <c>## Demo Prompt</c>. The text-area writes
/// into the model on every keystroke; the shell debounces persistent saves to
/// disk (spec §Demo Prompt Panel).
/// </summary>
public sealed class DemoPromptPanel : Component<DemoPromptPanelProps>
{
    public override Element Render()
    {
        // Local typing buffer — the source of truth WHILE the user is editing.
        // Keystroke flow: user types → local set → push to model (debounced save).
        // We deliberately do NOT subscribe to model.Changed in this panel: that
        // would round-trip our own keystrokes back through setState in the same
        // frame, which makes WinUI's TextBox clear its selection / reset the
        // caret to 0. The shell mints a NEW DemoScriptModel instance on Open
        // Folder / file-watcher reload, which lands here as a Props.Model swap
        // and is picked up by the dep-keyed effect below.
        var (title, setTitle) = UseState(Props.Model.Title);
        var (prompt, setPrompt) = UseState(Props.Model.DemoPrompt);

        UseEffect(() =>
        {
            setTitle(Props.Model.Title);
            setPrompt(Props.Model.DemoPrompt);
        }, Props.Model);

        var titleField = (TextBox(title, v =>
        {
            setTitle(v);
            Props.Cb.Value.OnTitleChanged(v);
        }, placeholderText: "Demo title (rendered as # heading in demo-script.md)")
            with { AcceptsReturn = false })
            .FontSize(18)
            .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
            .AutomationName("Demo title");

        var promptField = (TextBox(prompt, v =>
        {
            setPrompt(v);
            Props.Cb.Value.OnPromptChanged(v);
        }, placeholderText: "Describe the demo: tech stack, single-file vs multi-file, audience level, constraints…")
            with { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap })
            .MinHeight(96)
            .MaxHeight(220)
            .AutomationName("Demo prompt — persistent context for AI generation");

        return Border(
            VStack(8,
                Caption("DEMO TITLE").Foreground(Theme.SecondaryText),
                titleField,
                TextBlock("").Height(4),
                Caption("DEMO PROMPT").Foreground(Theme.SecondaryText),
                promptField))
            .Background(Theme.LayerFill)
            .CornerRadius(8)
            .Padding(16)
            .Margin(0, 0, 0, 12)
            .Landmark(Microsoft.UI.Xaml.Automation.Peers.AutomationLandmarkType.Form);
    }
}

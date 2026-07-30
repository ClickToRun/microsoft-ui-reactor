using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using static WinUIGalleryReactor.SamplePageHost;

namespace WinUIGalleryReactor.ControlPages.Navigation;

class TabViewPage : Component
{
    /// <summary>
    /// Where the selection lands after a tab is closed: closing a tab to the left of
    /// the selected one shifts it down, and the result is clamped into the shorter list.
    /// </summary>
    static int SelectionAfterClose(int selected, int closed, int remaining) =>
        remaining == 0 ? -1 : Math.Clamp(closed < selected ? selected - 1 : selected, 0, remaining - 1);

    public override Element Render()
    {
        // Each card owns its own tabs and its own selection — sharing either would make
        // closing or selecting in one card silently move the other. The initial arrays are
        // memoized so they are not re-allocated on every render (REACTOR_HOOKS_013).
        var (basicTabs, setBasicTabs) = UseState(UseMemo(() => new[] { "Home", "Document", "Settings" }));
        var (basicIdx, setBasicIdx) = UseState(0);

        var (dynamicTabs, setDynamicTabs) = UseState(UseMemo(() => new[] { "Tab 1", "Tab 2", "Tab 3" }));
        var (dynamicIdx, setDynamicIdx) = UseState(0);
        var (nextTabId, setNextTabId) = UseState(4);

        return ScrollView(
            VStack(16,
                PageHeader("TabView",
                    "A control that displays a set of closable, rearrangeable tabs."),

                SampleCard("Basic TabView",
                    (TabView(basicTabs
                        .Select(t => Tab(t, TextBlock($"{t} content").Padding(16)))
                        .ToArray()) with
                    {
                        SelectedIndex = basicIdx,
                        OnSelectedIndexChanged = i => setBasicIdx(i),
                        // Without this the per-tab ✕ is drawn but inert: WinUI raises
                        // TabCloseRequested and expects the app to remove the tab.
                        OnTabCloseRequested = i =>
                        {
                            var remaining = basicTabs.Where((_, n) => n != i).ToArray();
                            setBasicTabs(remaining);
                            setBasicIdx(SelectionAfterClose(basicIdx, i, remaining.Length));
                        },
                    }).Height(200),
                    @"var (tabs, setTabs) = UseState(new[] { ""Home"", ""Document"", ""Settings"" });
var (idx, setIdx) = UseState(0);

TabView(tabs.Select(t => Tab(t, TextBlock($""{t} content""))).ToArray()) with
{
    SelectedIndex = idx,
    OnSelectedIndexChanged = i => setIdx(i),
    // The per-tab ✕ only raises TabCloseRequested — the app removes the tab.
    OnTabCloseRequested = i => setTabs(tabs.Where((_, n) => n != i).ToArray()),
}"),

                SampleCard("Dynamic Tabs",
                    VStack(8,
                        (TabView(dynamicTabs
                            .Select(t => Tab(t, TextBlock($"Content of {t}").Padding(16)))
                            .ToArray()) with
                        {
                            SelectedIndex = dynamicIdx,
                            OnSelectedIndexChanged = i => setDynamicIdx(i),
                            OnTabCloseRequested = i =>
                            {
                                var remaining = dynamicTabs.Where((_, n) => n != i).ToArray();
                                setDynamicTabs(remaining);
                                setDynamicIdx(SelectionAfterClose(dynamicIdx, i, remaining.Length));
                            },
                        }).Height(180),
                        HStack(8,
                            Button("Add Tab", () =>
                            {
                                // A fresh id rather than a count, so titles stay unique
                                // after tabs in the middle have been closed.
                                setDynamicTabs(dynamicTabs.Append($"Tab {nextTabId}").ToArray());
                                setNextTabId(nextTabId + 1);
                            }),
                            Button("Remove Tab", () =>
                            {
                                if (dynamicTabs.Length <= 1) return;
                                var remaining = dynamicTabs[..^1];
                                setDynamicTabs(remaining);
                                setDynamicIdx(SelectionAfterClose(dynamicIdx, remaining.Length, remaining.Length));
                            })
                        )
                    ),
                    @"var (tabs, setTabs) = UseState(new[] { ""Tab 1"", ""Tab 2"", ""Tab 3"" });
var (idx, setIdx) = UseState(0);
var (nextId, setNextId) = UseState(4);   // ids, not a count, so titles stay unique

TabView(tabs.Select(t => Tab(t, TextBlock($""Content of {t}""))).ToArray()) with
{
    SelectedIndex = idx,
    OnSelectedIndexChanged = i => setIdx(i),
    OnTabCloseRequested = i => setTabs(tabs.Where((_, n) => n != i).ToArray()),
}

Button(""Add Tab"", () =>
{
    setTabs(tabs.Append($""Tab {nextId}"").ToArray());
    setNextId(nextId + 1);
})")
            ).Margin(36, 24, 36, 36)
        );
    }
}

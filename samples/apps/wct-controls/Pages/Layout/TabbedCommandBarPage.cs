namespace WctControls;

internal sealed class TabbedCommandBarPage : Component
{
    public override Element Render() =>
        Gallery.Page(
            "TabbedCommandBar",
            "A ribbon-style NavigationView whose tabs are TabbedCommandBarItem (a CommandBar) objects in MenuItems — a WinUI control subtree the prop/event wrapper can't express declaratively, so the tabs are built once through the imperative Setters escape hatch (legitimate here: it's controls, not data). The document body is the declarative content slot.",
            (TabbedCommandBar(
                content: Border(TextBlock("Document body — pick a ribbon tab above.").Center())
                    .Background("AliceBlue").CornerRadius(8).Height(150))
                .Set(BuildTabs))
                .Height(260));

    private static void BuildTabs(CommunityToolkit.WinUI.Controls.TabbedCommandBar tcb)
    {
        if (tcb.MenuItems.Count > 0) return;

        var home = new CommunityToolkit.WinUI.Controls.TabbedCommandBarItem { Header = "Home" };
        home.PrimaryCommands.Add(Cmd("Add", Microsoft.UI.Xaml.Controls.Symbol.Add));
        home.PrimaryCommands.Add(Cmd("Edit", Microsoft.UI.Xaml.Controls.Symbol.Edit));
        home.PrimaryCommands.Add(new Microsoft.UI.Xaml.Controls.AppBarSeparator());
        home.PrimaryCommands.Add(Cmd("Share", Microsoft.UI.Xaml.Controls.Symbol.Share));

        var view = new CommunityToolkit.WinUI.Controls.TabbedCommandBarItem { Header = "View" };
        view.PrimaryCommands.Add(Cmd("Zoom", Microsoft.UI.Xaml.Controls.Symbol.Zoom));
        view.PrimaryCommands.Add(Cmd("Refresh", Microsoft.UI.Xaml.Controls.Symbol.Refresh));

        tcb.MenuItems.Add(home);
        tcb.MenuItems.Add(view);
    }

    private static Microsoft.UI.Xaml.Controls.AppBarButton Cmd(string label, Microsoft.UI.Xaml.Controls.Symbol symbol) =>
        new() { Label = label, Icon = new Microsoft.UI.Xaml.Controls.SymbolIcon(symbol) };
}

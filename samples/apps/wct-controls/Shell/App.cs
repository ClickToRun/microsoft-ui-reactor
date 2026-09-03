namespace WctControls;

internal sealed class App : Component
{
    public override Element Render()
    {
        var nav = UseNavigation(AppRoute.Cards);

        var menu = new[]
        {
            NavItemHeader("Input"),
            NavItem("Segmented",            "Bullets",   AppRouteMap.Tag(AppRoute.Segmented)),
            NavItem("Range selector",       "Volume",    AppRouteMap.Tag(AppRoute.Range)),
            NavItem("Radial gauge",         "Volume",    AppRouteMap.Tag(AppRoute.Gauge)),
            NavItem("Color picker",         "Highlight", AppRouteMap.Tag(AppRoute.Color)),

            NavItemHeader("Layout"),
            NavItem("Settings card",        "Setting",   AppRouteMap.Tag(AppRoute.Cards)),
            NavItem("Settings expander",    "AllApps",   AppRouteMap.Tag(AppRoute.Expander)),
            NavItem("Headered content",     "GroupList", AppRouteMap.Tag(AppRoute.HeaderedContent)),
            NavItem("Headered items",       "List",      AppRouteMap.Tag(AppRoute.HeaderedItems)),
            NavItem("Headered tree",        "Library",   AppRouteMap.Tag(AppRoute.HeaderedTree)),
            NavItem("Layout transform",     "Rotate",    AppRouteMap.Tag(AppRoute.LayoutTransform)),
            NavItem("Tabbed command bar",   "Tab",       AppRouteMap.Tag(AppRoute.TabbedCommandBar)),

            NavItemHeader("Media"),
            NavItem("Camera preview",       "Camera",    AppRouteMap.Tag(AppRoute.Camera)),
            NavItem("Image cropper",        "Crop",      AppRouteMap.Tag(AppRoute.Cropper)),

            NavItemHeader("Sizers"),
            NavItem("Grid splitter",        "Page2",     AppRouteMap.Tag(AppRoute.Splitter)),
            NavItem("Content sizer",        "Page2",     AppRouteMap.Tag(AppRoute.ContentSizer)),
            NavItem("Property sizer",       "Page2",     AppRouteMap.Tag(AppRoute.PropertySizer)),

            NavItemHeader("Status & info"),
            NavItem("Metadata",             "Tag",       AppRouteMap.Tag(AppRoute.Metadata)),

            NavItemHeader("Text"),
            NavItem("Tokenizing box",       "Tag",       AppRouteMap.Tag(AppRoute.Tokens)),
            NavItem("Rich suggest box",     "Edit",      AppRouteMap.Tag(AppRoute.RichSuggest)),

            NavItemHeader("Layouts (panels)"),
            NavItem("Dock panel",           "ViewAll",   AppRouteMap.Tag(AppRoute.Dock)),
            NavItem("Uniform grid",         "ViewAll",   AppRouteMap.Tag(AppRoute.UniformGrid)),
            NavItem("Wrap panel",           "ViewAll",   AppRouteMap.Tag(AppRoute.Wrap)),
            NavItem("Staggered panel",      "ViewAll",   AppRouteMap.Tag(AppRoute.Staggered)),
            NavItem("Constrained box",      "FitPage",   AppRouteMap.Tag(AppRoute.Constrained)),
            NavItem("Switch presenter",     "Switch",    AppRouteMap.Tag(AppRoute.SwitchPresenter)),
        };

        return NavigationView(menu, content: NavigationHost(nav, PageRouter.Route)) with
        {
            SelectedTag = AppRouteMap.Tag(nav.CurrentRoute),
            OnSelectedTagChanged = t =>
            {
                if (t is null || !AppRouteMap.TryParse(t, out var next) || next == nav.CurrentRoute)
                    return;
                nav.Navigate(next);
            },
            PaneTitle = "Controls",
            IsSettingsVisible = false,
        };
    }
}

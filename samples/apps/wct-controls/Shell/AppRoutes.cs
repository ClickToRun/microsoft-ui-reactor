namespace WctControls;

internal enum AppRoute
{
    Cards,
    Expander,
    Segmented,
    Range,
    Gauge,
    Camera,
    Color,
    Cropper,
    Splitter,
    ContentSizer,
    PropertySizer,
    Tokens,
    RichSuggest,
    HeaderedContent,
    HeaderedItems,
    HeaderedTree,
    LayoutTransform,
    TabbedCommandBar,
    Metadata,
    Dock,
    UniformGrid,
    Wrap,
    Staggered,
    Constrained,
    SwitchPresenter,
}

internal static class AppRouteMap
{
    private static readonly Dictionary<string, AppRoute> _fromTag = new()
    {
        ["cards"] = AppRoute.Cards,
        ["expander"] = AppRoute.Expander,
        ["segmented"] = AppRoute.Segmented,
        ["range"] = AppRoute.Range,
        ["gauge"] = AppRoute.Gauge,
        ["camera"] = AppRoute.Camera,
        ["color"] = AppRoute.Color,
        ["cropper"] = AppRoute.Cropper,
        ["splitter"] = AppRoute.Splitter,
        ["csz"] = AppRoute.ContentSizer,
        ["psz"] = AppRoute.PropertySizer,
        ["tokens"] = AppRoute.Tokens,
        ["rsb"] = AppRoute.RichSuggest,
        ["hcc"] = AppRoute.HeaderedContent,
        ["hic"] = AppRoute.HeaderedItems,
        ["htv"] = AppRoute.HeaderedTree,
        ["ltc"] = AppRoute.LayoutTransform,
        ["tcb"] = AppRoute.TabbedCommandBar,
        ["meta"] = AppRoute.Metadata,
        ["dock"] = AppRoute.Dock,
        ["ug"] = AppRoute.UniformGrid,
        ["wrap"] = AppRoute.Wrap,
        ["stag"] = AppRoute.Staggered,
        ["cbox"] = AppRoute.Constrained,
        ["swp"] = AppRoute.SwitchPresenter,
    };

    public static bool TryParse(string tag, out AppRoute route) => _fromTag.TryGetValue(tag, out route);

    public static string Tag(AppRoute route) => route switch
    {
        AppRoute.Cards => "cards",
        AppRoute.Expander => "expander",
        AppRoute.Segmented => "segmented",
        AppRoute.Range => "range",
        AppRoute.Gauge => "gauge",
        AppRoute.Camera => "camera",
        AppRoute.Color => "color",
        AppRoute.Cropper => "cropper",
        AppRoute.Splitter => "splitter",
        AppRoute.ContentSizer => "csz",
        AppRoute.PropertySizer => "psz",
        AppRoute.Tokens => "tokens",
        AppRoute.RichSuggest => "rsb",
        AppRoute.HeaderedContent => "hcc",
        AppRoute.HeaderedItems => "hic",
        AppRoute.HeaderedTree => "htv",
        AppRoute.LayoutTransform => "ltc",
        AppRoute.TabbedCommandBar => "tcb",
        AppRoute.Metadata => "meta",
        AppRoute.Dock => "dock",
        AppRoute.UniformGrid => "ug",
        AppRoute.Wrap => "wrap",
        AppRoute.Staggered => "stag",
        AppRoute.Constrained => "cbox",
        AppRoute.SwitchPresenter => "swp",
        _ => "cards"
    };
}

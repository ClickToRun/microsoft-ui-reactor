namespace WctControls;

internal static class PageRouter
{
    public static Element Route(AppRoute route) => route switch
    {
        AppRoute.Expander => Component<ExpanderPage>(),
        AppRoute.Segmented => Component<SegmentedPage>(),
        AppRoute.Range => Component<RangeSelectorPage>(),
        AppRoute.Gauge => Component<GaugePage>(),
        AppRoute.Camera => Component<CameraPage>(),
        AppRoute.Color => Component<ColorPickerPage>(),
        AppRoute.Cropper => Component<ImageCropperPage>(),
        AppRoute.Splitter => Component<GridSplitterPage>(),
        AppRoute.ContentSizer => Component<ContentSizerPage>(),
        AppRoute.PropertySizer => Component<PropertySizerPage>(),
        AppRoute.Tokens => Component<TokenizingPage>(),
        AppRoute.RichSuggest => Component<RichSuggestBoxPage>(),
        AppRoute.HeaderedContent => Component<HeaderedContentPage>(),
        AppRoute.HeaderedItems => Component<HeaderedItemsPage>(),
        AppRoute.HeaderedTree => Component<HeaderedTreePage>(),
        AppRoute.LayoutTransform => Component<LayoutTransformPage>(),
        AppRoute.TabbedCommandBar => Component<TabbedCommandBarPage>(),
        AppRoute.Metadata => Component<MetadataPage>(),
        AppRoute.Dock => Component<DockPanelPage>(),
        AppRoute.UniformGrid => Component<UniformGridPage>(),
        AppRoute.Wrap => Component<WrapPanelPage>(),
        AppRoute.Staggered => Component<StaggeredPanelPage>(),
        AppRoute.Constrained => Component<ConstrainedBoxPage>(),
        AppRoute.SwitchPresenter => Component<SwitchPresenterPage>(),
        _ => Component<CardsPage>(),
    };
}

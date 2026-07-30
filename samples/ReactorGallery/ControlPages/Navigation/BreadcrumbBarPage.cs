using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using static WinUIGalleryReactor.SamplePageHost;

namespace WinUIGalleryReactor.ControlPages.Navigation;

class BreadcrumbBarPage : Component
{
    public override Element Render()
    {
        var (path, setPath) = UseState(new[] { "Home", "Documents", "Reports" });
        var (clicked, setClicked) = UseState("(none)");

        // The dynamic card owns its own trail — sharing `path` with the basic card
        // would make navigating here silently rewrite the card above.
        var (dynamicPath, setDynamicPath) = UseState(new[] { "Home", "Documents", "Reports" });

        return ScrollView(
            VStack(16,
                PageHeader("BreadcrumbBar",
                    "A trail of links showing the user's navigation path."),

                SampleCard("Basic BreadcrumbBar",
                    VStack(8,
                        BreadcrumbBar(
                            path.Select(p => Breadcrumb(p)).ToArray(),
                            item => setClicked(item.Label)),
                        TextBlock($"Last clicked: {clicked}").Foreground(Theme.SecondaryText)
                    ),
                    @"BreadcrumbBar(
    new[] { Breadcrumb(""Home""), Breadcrumb(""Documents""), Breadcrumb(""Reports"") },
    item => setClicked(item.Label))"),

                SampleCard("Dynamic Breadcrumb",
                    VStack(8,
                        BreadcrumbBar(
                            dynamicPath.Select(p => Breadcrumb(p)).ToArray(),
                            item =>
                            {
                                var idx = Array.IndexOf(dynamicPath, item.Label);
                                if (idx >= 0)
                                    setDynamicPath(dynamicPath.Take(idx + 1).ToArray());
                            }),
                        HStack(8,
                            Button("Add Level", () =>
                                setDynamicPath(dynamicPath.Append($"Level {dynamicPath.Length}").ToArray())),
                            Button("Reset", () =>
                                setDynamicPath(new[] { "Home", "Documents", "Reports" }))
                        )
                    ),
                    @"// Each sample owns its own state — sharing one `path` between the two
// cards would make navigating here rewrite the card above.
var (dynamicPath, setDynamicPath) = UseState(new[] { ""Home"", ""Documents"", ""Reports"" });

BreadcrumbBar(items, item => {
    var idx = Array.IndexOf(dynamicPath, item.Label);
    if (idx >= 0) setDynamicPath(dynamicPath.Take(idx + 1).ToArray());
})")
            ).Margin(36, 24, 36, 36)
        );
    }
}

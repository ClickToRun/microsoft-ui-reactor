using System;
using Microsoft.UI.Reactor.Core;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.DescriptorOptionalCoverage;

public class TabViewOptionalTests
{
    [Fact]
    public void ControlledEntry_UsesOptionalGateTransitions() =>
        DescriptorOptionalHarness.AssertOptionalGate<int>(
            TabViewElement.Descriptor,
            new TabViewElement(Array.Empty<TabViewItemData>()),
            new TabViewElement(Array.Empty<TabViewItemData>()) { SelectedIndex = 1 },
            new TabViewElement(Array.Empty<TabViewItemData>()) { SelectedIndex = 1 },
            new TabViewElement(Array.Empty<TabViewItemData>()));
}


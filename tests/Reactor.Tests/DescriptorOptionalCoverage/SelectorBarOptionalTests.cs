using System;
using Microsoft.UI.Reactor.Core;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.DescriptorOptionalCoverage;

public class SelectorBarOptionalTests
{
    [Fact]
    public void ControlledEntry_UsesOptionalGateTransitions() =>
        DescriptorOptionalHarness.AssertOptionalGate<int>(
            SelectorBarElement.Descriptor,
            new SelectorBarElement(Array.Empty<SelectorBarItemData>()),
            new SelectorBarElement(Array.Empty<SelectorBarItemData>()) { SelectedIndex = 1 },
            new SelectorBarElement(Array.Empty<SelectorBarItemData>()) { SelectedIndex = 1 },
            new SelectorBarElement(Array.Empty<SelectorBarItemData>()));
}


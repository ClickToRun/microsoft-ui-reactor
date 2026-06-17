using System;
using Microsoft.UI.Reactor.Core;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.DescriptorOptionalCoverage;

public class FlipViewOptionalTests
{
    [Fact]
    public void ControlledEntry_UsesOptionalGateTransitions() =>
        DescriptorOptionalHarness.AssertOptionalGate<int>(
            FlipViewElement.Descriptor,
            new FlipViewElement(Array.Empty<Element>()),
            new FlipViewElement(Array.Empty<Element>()) { SelectedIndex = 2 },
            new FlipViewElement(Array.Empty<Element>()) { SelectedIndex = 2 },
            new FlipViewElement(Array.Empty<Element>()));
}


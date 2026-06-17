using System;
using Microsoft.UI.Reactor.Core;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.DescriptorOptionalCoverage;

public class SliderOptionalTests
{
    [Fact]
    public void ControlledEntry_UsesOptionalGateTransitions() =>
        DescriptorOptionalHarness.AssertOptionalGate<double>(
            SliderElement.Descriptor,
            new SliderElement(),
            new SliderElement(5.0),
            new SliderElement(5.0),
            new SliderElement());
}


using System;
using Microsoft.UI.Reactor.Core;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.DescriptorOptionalCoverage;

public class RadioButtonOptionalTests
{
    [Fact]
    public void ControlledEntry_UsesOptionalGateTransitions() =>
        DescriptorOptionalHarness.AssertOptionalGate<bool>(
            RadioButtonElement.Descriptor,
            new RadioButtonElement("r"),
            new RadioButtonElement("r", true),
            new RadioButtonElement("r", true),
            new RadioButtonElement("r"));
}


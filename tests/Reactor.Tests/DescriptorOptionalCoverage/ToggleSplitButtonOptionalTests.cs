using System;
using Microsoft.UI.Reactor.Core;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.DescriptorOptionalCoverage;

public class ToggleSplitButtonOptionalTests
{
    [Fact]
    public void ControlledEntry_UsesOptionalGateTransitions() =>
        DescriptorOptionalHarness.AssertOptionalGate<bool>(
            ToggleSplitButtonElement.Descriptor,
            new ToggleSplitButtonElement("t"),
            new ToggleSplitButtonElement("t", true),
            new ToggleSplitButtonElement("t", true),
            new ToggleSplitButtonElement("t"));
}


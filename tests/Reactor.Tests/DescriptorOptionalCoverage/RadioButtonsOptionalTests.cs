using System;
using Microsoft.UI.Reactor.Core;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.DescriptorOptionalCoverage;

public class RadioButtonsOptionalTests
{
    [Fact]
    public void ControlledEntry_UsesOptionalGateTransitions() =>
        DescriptorOptionalHarness.AssertOptionalGate<int>(
            RadioButtonsElement.Descriptor,
            new RadioButtonsElement(Array.Empty<string>()),
            new RadioButtonsElement(Array.Empty<string>(), 1),
            new RadioButtonsElement(Array.Empty<string>(), 1),
            new RadioButtonsElement(Array.Empty<string>()));
}


using System;
using Microsoft.UI.Reactor.Core;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.DescriptorOptionalCoverage;

public class TextBoxOptionalTests
{
    [Fact]
    public void ControlledEntry_UsesOptionalGateTransitions() =>
        DescriptorOptionalHarness.AssertOptionalGate<string>(
            TextBoxElement.Descriptor,
            new TextBoxElement(),
            new TextBoxElement("abc"),
            new TextBoxElement("abc"),
            new TextBoxElement());
}


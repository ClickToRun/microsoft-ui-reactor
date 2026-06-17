using System;
using Microsoft.UI.Reactor.Core;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.DescriptorOptionalCoverage;

public class RichEditBoxOptionalTests
{
    [Fact]
    public void ControlledEntry_UsesOptionalGateTransitions() =>
        DescriptorOptionalHarness.AssertOptionalGate<string>(
            RichEditBoxElement.Descriptor,
            new RichEditBoxElement(),
            new RichEditBoxElement("abc"),
            new RichEditBoxElement("abc"),
            new RichEditBoxElement());
}


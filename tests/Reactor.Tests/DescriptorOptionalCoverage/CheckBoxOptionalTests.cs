using System;
using Microsoft.UI.Reactor.Core;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.DescriptorOptionalCoverage;

public class CheckBoxOptionalTests
{
    [Fact]
    public void ControlledEntry_UsesOptionalGateTransitions() =>
        DescriptorOptionalHarness.AssertOptionalGate<bool?>(
            CheckBoxElement.Descriptor,
            new CheckBoxElement(),
            new CheckBoxElement((bool?)true),
            new CheckBoxElement((bool?)true),
            new CheckBoxElement());
}


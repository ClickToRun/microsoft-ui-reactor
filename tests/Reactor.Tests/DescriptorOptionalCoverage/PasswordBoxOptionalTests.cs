using System;
using Microsoft.UI.Reactor.Core;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.DescriptorOptionalCoverage;

public class PasswordBoxOptionalTests
{
    [Fact]
    public void ControlledEntry_UsesOptionalGateTransitions() =>
        DescriptorOptionalHarness.AssertOptionalGate<string>(
            PasswordBoxElement.Descriptor,
            new PasswordBoxElement(),
            new PasswordBoxElement("secret"),
            new PasswordBoxElement("secret"),
            new PasswordBoxElement());
}


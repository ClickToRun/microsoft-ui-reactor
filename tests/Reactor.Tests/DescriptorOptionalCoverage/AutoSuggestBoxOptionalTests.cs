using System;
using Microsoft.UI.Reactor.Core;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.DescriptorOptionalCoverage;

public class AutoSuggestBoxOptionalTests
{
    [Fact]
    public void ControlledEntry_UsesOptionalGateTransitions() =>
        DescriptorOptionalHarness.AssertOptionalGate<string>(
            AutoSuggestBoxElement.Descriptor,
            new AutoSuggestBoxElement(),
            new AutoSuggestBoxElement("abc"),
            new AutoSuggestBoxElement("abc"),
            new AutoSuggestBoxElement());
}


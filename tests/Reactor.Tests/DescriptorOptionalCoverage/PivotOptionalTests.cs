using System;
using Microsoft.UI.Reactor.Core;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.DescriptorOptionalCoverage;

public class PivotOptionalTests
{
    [Fact]
    public void ControlledEntry_UsesOptionalGateTransitions() =>
        DescriptorOptionalHarness.AssertOptionalGate<int>(
            PivotElement.Descriptor,
            new PivotElement(Array.Empty<PivotItemData>()),
            new PivotElement(Array.Empty<PivotItemData>()) { SelectedIndex = 1 },
            new PivotElement(Array.Empty<PivotItemData>()) { SelectedIndex = 1 },
            new PivotElement(Array.Empty<PivotItemData>()));
}


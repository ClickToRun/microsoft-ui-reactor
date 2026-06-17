using System;
using Microsoft.UI.Reactor.Core;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.DescriptorOptionalCoverage;

public class ColorPickerOptionalTests
{
    [Fact]
    public void ControlledEntry_UsesOptionalGateTransitions() =>
        DescriptorOptionalHarness.AssertOptionalGate<global::Windows.UI.Color>(
            ColorPickerElement.Descriptor,
            new ColorPickerElement(),
            new ColorPickerElement(global::Windows.UI.Color.FromArgb(255, 1, 2, 3)),
            new ColorPickerElement(global::Windows.UI.Color.FromArgb(255, 1, 2, 3)),
            new ColorPickerElement());
}


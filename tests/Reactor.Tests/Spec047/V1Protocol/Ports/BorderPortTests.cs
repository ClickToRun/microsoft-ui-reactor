using System;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Core.V1Protocol;
using Microsoft.UI.Reactor.Core.V1Protocol.Descriptor;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.Spec047.V1Protocol.Ports;

/// <summary>
/// Spec 047 §14 Phase 1 (1.14) — Border port tests.
/// </summary>
public class BorderPortTests
{
    [Fact]
    public void BuiltIn_BorderHandler_In_Global_Registry()
    {
        // Spec 058 §15 (P5.18) — Border migrated to a generated descriptor; the
        // test-only BuiltInHandlerBootstrap fires BorderElement's Pattern-A static
        // cctor (RunClassConstructor), self-registering it in the global registry.
        Assert.True(Microsoft.UI.Reactor.Core.V1Protocol.ControlRegistry.TryResolve(
            typeof(BorderElement), out _));
    }

    [Fact]
    public void Border_Descriptor_Declares_SingleContent_Strategy()
    {
        // The generated descriptor's [WrapContent("Child")] slot is a SingleContent
        // children strategy (mirrors the deleted hand-coded BorderHandler.Children).
        var strategy = BorderElement.Descriptor.Children;
        Assert.NotNull(strategy);
        Assert.IsType<SingleContent<BorderElement, Microsoft.UI.Xaml.Controls.Border>>(strategy);
    }

    [Fact(Skip = "Requires WinUI dispatcher; covered in AppTests.Host SelfTest/Fixtures/Spec047V1ProtocolFixtures.cs (1.14)")]
    public void Border_Child_Reconciles_Through_SingleContent_Strategy()
    {
        // TODO(AppTests.Host): mount BorderElement with a TextBlock child →
        // assert ctrl.Child is the mounted UIElement.
        // Update with a different child → strategy dispatches the swap.
        // Modifier interaction: .Padding(10).Background(brush) honored.
    }
}

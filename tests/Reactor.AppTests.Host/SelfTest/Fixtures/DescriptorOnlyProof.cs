using System;
using Microsoft.UI.Reactor.Core;        // Element
using Microsoft.UI.Reactor.Core.V1Protocol.Descriptor; // ControlDescriptor (Customize hook)
using Microsoft.UI.Reactor.Wrappers;     // [GenerateReactorDescriptor]
using Microsoft.UI.Xaml;                 // FrameworkElement

namespace Microsoft.UI.Reactor.AppTests.Host.SelfTest.Fixtures;

// Spec 058 §15 (P5.1/P5.2/P5.5) — descriptor-only ("attach") generation COMPILE PROOF.
//
// The record below is hand-written (it owns its own properties + Setters, exactly
// as a migrated built-in would); the generator emits ONLY the ControlDescriptor +
// Pattern-A registration for it — no init-properties, no factory. This file's sole
// purpose is to prove the emitted descriptor-only source COMPILES against the real
// Reactor descriptor types (the generator unit tests only pin the emitted string).
//
// An isolated custom control is used (not a real WinUI control) so the proof
// targets the MODE in isolation, free of real-control event/content/items noise.
// The 1:1 string property avoids the scalar→struct conversion that P5.2 adds.

/// <summary>Isolated control for the descriptor-only compile proof.</summary>
public sealed class DescriptorOnlyProofControl : FrameworkElement
{
    public string? Label { get; set; }
    public CornerRadius Corner { get; set; }
}

[GenerateReactorDescriptor(typeof(DescriptorOnlyProofControl))]
[WrapConvert("Corner")]
[WrapManual("Caption")]
internal partial record DescriptorOnlyProofElement : Element
{
    /// <summary>Maps <c>DescriptorOnlyProofControl.Label</c> (1:1, no conversion).</summary>
    public string? Label { get; init; }

    /// <summary>Ergonomic scalar mapped to <c>DescriptorOnlyProofControl.Corner</c>
    /// (a <see cref="CornerRadius"/>) via <c>new CornerRadius(v)</c> — the P5.2
    /// [WrapConvert] path, mirroring the hand-written Border descriptor.</summary>
    public double? Corner { get; init; }

    /// <summary>A bespoke prop with no 1:1 control mapping — handled manually in
    /// <c>Customize</c> (the P5 [WrapManual] escape hatch). Here it writes a
    /// transformed value into the control's Label.</summary>
    public string? Caption { get; init; }

    /// <summary>Imperative escape hatch (the generated descriptor's GetSetters reads this).</summary>
    public Action<DescriptorOnlyProofControl>[] Setters { get; init; }
        = Array.Empty<Action<DescriptorOnlyProofControl>>();

    // Author hook the generator declares (mandatory because [WrapManual] is present):
    // chain the bespoke entry the generator can't infer.
    private static partial ControlDescriptor<DescriptorOnlyProofElement, DescriptorOnlyProofControl> Customize(
        ControlDescriptor<DescriptorOnlyProofElement, DescriptorOnlyProofControl> d)
        => d.OneWayConditional<string>(
            get:         static e => e.Caption!,
            set:         static (c, v) => c.Label = "[" + v + "]",
            shouldWrite: static e => e.Caption is not null);
}


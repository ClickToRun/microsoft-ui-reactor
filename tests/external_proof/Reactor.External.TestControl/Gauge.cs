using System;
using Microsoft.UI.Reactor.Core.V1Protocol;

namespace Reactor.External.TestControl;

/// <summary>
/// Issue #206 — public construction holder for the external
/// <see cref="GaugeElement"/>. Its static constructor registers
/// <see cref="GaugeHandler"/> against the global <see cref="ControlRegistry"/>,
/// and <see cref="Of(double)"/> is the only path to a <see cref="GaugeElement"/>
/// (the element's primary constructor is <c>internal</c>) — so dispatch and
/// trim-reachability both follow <c>Gauge → cctor → GaugeHandler →
/// GaugeControl</c>. Mirrors the <see cref="Marquee"/> shape.
/// </summary>
public static class Gauge
{
    static Gauge() =>
        ControlRegistry.Register<GaugeElement, GaugeControl>(static () => new GaugeHandler());

    /// <summary>Sole public construction path for <see cref="GaugeElement"/>.</summary>
    public static GaugeElement Of(double value) => new(value);

    /// <summary>Construct a <see cref="GaugeElement"/> with an
    /// <see cref="GaugeElement.OnValueChanged"/> callback.</summary>
    public static GaugeElement Of(double value, Action<double> onValueChanged) =>
        new(value, onValueChanged);
}

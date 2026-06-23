using System;
using Microsoft.UI.Reactor.Core;

namespace Reactor.External.TestControl;

/// <summary>
/// Issue #206 — element record for the external <see cref="GaugeControl"/>.
/// Carries the numeric value-bearing <see cref="Value"/> prop, the
/// <see cref="OnValueChanged"/> callback, and a <see cref="Setters"/> array.
/// Construction is funnelled through <see cref="Gauge.Of(double)"/> (the
/// primary constructor is <c>internal</c>) so the static cctor that registers
/// <see cref="GaugeHandler"/> always runs before the element can be mounted —
/// the same trim-reachability discipline as <see cref="MarqueeElement"/>.
/// </summary>
public sealed record GaugeElement : Element
{
    public double Value { get; init; }
    public Action<double>? OnValueChanged { get; init; }
    public Action<GaugeControl>[] Setters { get; init; } = Array.Empty<Action<GaugeControl>>();

    internal GaugeElement(double value, Action<double>? onValueChanged = null)
    {
        Value = value;
        OnValueChanged = onValueChanged;
    }
}

namespace WctControls;

internal sealed class RangeSelectorPage : Component
{
    public override Element Render()
    {
        var (lo, setLo) = UseState(20.0);
        var (hi, setHi) = UseState(80.0);

        return Gallery.Page(
            "RangeSelector",
            "A dual-thumb range slider. RangeStart/RangeEnd are one-way props (force-asserted from state) and the single ValueChanged event — which carries WHICH thumb moved — drives the matching state setter. (Two thumbs share one event, so they can't both be [WrapControlled]; this one-way + typed-event shape is the idiomatic fit.)",
            VStack(12,
                RangeSelector(
                    minimum: 0, maximum: 100, stepFrequency: 1,
                    rangeStart: lo, rangeEnd: hi,
                    onValueChanged: e =>
                    {
                        if (e.ChangedRangeProperty == CommunityToolkit.WinUI.Controls.RangeSelectorProperty.MinimumValue)
                            setLo(e.NewValue);
                        else
                            setHi(e.NewValue);
                    }).Width(380),
                Caption($"Selected range: {lo:0} – {hi:0}")));
    }
}

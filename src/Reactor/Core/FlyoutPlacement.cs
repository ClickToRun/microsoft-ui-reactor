using WinPrim = Microsoft.UI.Xaml.Controls.Primitives;

namespace Microsoft.UI.Reactor.Core;

/// <summary>
/// Single choke point for pushing a Reactor element's <c>Placement</c> onto a live WinUI
/// <see cref="WinPrim.FlyoutBase"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WinPrim.FlyoutPlacementMode.Auto"/> must never reach the WinUI DP.
/// <c>FlyoutBase::ShowAtCore</c> validates the effective placement through
/// <c>FlyoutBase::ValidateAndSetParameters</c>, whose switch only accepts values
/// <c>0..12</c> and fails everything else with <c>E_INVALIDARG</c> (<c>0x80070057</c>).
/// <c>Auto</c> is <c>13</c>, and <c>FlyoutBase::GetEffectivePlacement</c> hands the raw
/// <c>Placement</c> property straight to that validator when no per-show override is
/// cached — so a flyout left at <c>Auto</c> fails the moment it is shown, surfacing as a
/// stowed <see cref="global::System.ArgumentException"/> ("The parameter is incorrect")
/// that terminates the process. WinUI itself never puts <c>Auto</c> there: the documented
/// default of <c>FlyoutBase.Placement</c> is <see cref="WinPrim.FlyoutPlacementMode.Top"/>.
/// </para>
/// <para>
/// Provenance, since these two internals are undocumented and could change between Windows
/// App SDK versions: the <c>0..12</c> validator range and the <c>GetEffectivePlacement</c>
/// fall-through were established by disassembling <c>Microsoft.UI.Xaml.dll</c> under a
/// debugger with public symbols during the ReactorGallery bug hunt. The enum value and the
/// <c>Top</c> default come from the published API reference. The resulting crash is pinned
/// by the <c>FlyoutPlacement*</c> selftest fixtures, which open a default-placement flyout
/// for real — remove any <see cref="Apply"/> call below and they fail.
/// </para>
/// <para>
/// Reactor's element records default to <c>Auto</c>, which means "no opinion — let the
/// platform decide". That is expressed by leaving the DP untouched rather than by writing
/// <c>Auto</c> to it. Note that "untouched" is not the same as "<c>Auto</c>": the DP's own
/// default is <c>Top</c>, so skipping the write pins the flyout to <c>Top</c> and lets WinUI
/// reposition from there. That is the right outcome only for flyout types whose validator
/// rejects <c>Auto</c> — <c>Flyout</c> and <c>MenuFlyout</c>. <c>CommandBarFlyout</c>
/// resolves <c>Auto</c> itself and is deliberately <b>not</b> routed through this helper,
/// because suppressing its write would stop it auto-positioning and pin it to <c>Top</c>.
/// </para>
/// <para>
/// Consequently an update from an explicit placement back to <c>Auto</c> intentionally
/// leaves the previously written value in place; this matches the pre-existing
/// <c>MenuFlyout</c> guard that this helper generalizes.
/// </para>
/// </remarks>
internal static class FlyoutPlacement
{
    /// <summary>
    /// Whether <paramref name="placement"/> is safe to write to
    /// <see cref="WinPrim.FlyoutBase.Placement"/> — i.e. whether WinUI's
    /// <c>ValidateAndSetParameters</c> switch accepts it.
    /// </summary>
    internal static bool ShouldApply(WinPrim.FlyoutPlacementMode placement)
        => placement != WinPrim.FlyoutPlacementMode.Auto;

    /// <summary>
    /// Applies an element's placement to a live flyout, skipping
    /// <see cref="WinPrim.FlyoutPlacementMode.Auto"/> and redundant writes.
    /// </summary>
    internal static void Apply(WinPrim.FlyoutBase flyout, WinPrim.FlyoutPlacementMode placement)
    {
        if (!ShouldApply(placement)) return;
        if (flyout.Placement != placement) flyout.Placement = placement;
    }
}

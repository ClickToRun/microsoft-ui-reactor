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
/// default is <c>Top</c> (measured, see the <c>Platform_FlyoutBase_PlacementDefault</c>
/// selftest), so skipping the write pins the flyout to <c>Top</c> and lets WinUI reposition
/// from there.
/// </para>
/// <para>
/// <c>CommandBarFlyout</c>'s three placement sites are <b>not</b> routed through this helper.
/// That is a merge boundary, not an exemption on the merits: those methods are being rewritten
/// by the companion change that fixes <c>CommandBarFlyout</c> never opening, and that change
/// guards them itself. <c>CommandBarFlyout</c> is affected by the same crash — it simply never
/// reached the validator, because the flyout was installed as <c>AttachedFlyout</c> metadata
/// that nothing ever called <c>ShowAttachedFlyout</c> on. A latent crash masked by a separate
/// defect reads exactly like a working code path.
/// </para>
/// <para>
/// An update from an explicit placement back to <c>Auto</c> intentionally leaves the previously
/// written value in place; this matches the pre-existing <c>MenuFlyout</c> guard that this
/// helper generalizes. The companion change instead clears the DP, which resets to the platform
/// default — the two should converge on one behaviour once both have landed.
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

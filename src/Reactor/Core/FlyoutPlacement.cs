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
/// platform decide". That is expressed by <b>clearing</b> the DP rather than by writing
/// <c>Auto</c> to it. Note that cleared is not the same as <c>Auto</c>: the DP's own default
/// is <c>Top</c> (measured, see the <c>Platform_FlyoutBase_PlacementDefault</c> selftest), so
/// a cleared property leaves the flyout at <c>Top</c> and lets WinUI reposition from there.
/// </para>
/// <para>
/// Clearing rather than merely skipping the write matters on an update from an explicit
/// placement back to <c>Auto</c>. Skipping would leave the previous explicit value behind as a
/// <b>local</b> DP value, which outranks any <c>Style</c> setter for the same property — so the
/// element could never get its styled placement back, and "no opinion" would silently mean
/// "whatever I last said". That is the same dependency-property precedence defect tracked in
/// issue #952 for the common modifiers. Clearing also makes an update idempotent with a fresh
/// mount of the same element.
/// </para>
/// <para>
/// <c>CommandBarFlyout</c>'s three placement sites route through here too. They briefly had
/// their own equivalent helper — introduced by the change that fixed <c>CommandBarFlyout</c>
/// never opening from its target — which drifted from this one until the two were reconciled
/// and folded together. <c>CommandBarFlyout</c> is affected by the same crash: it simply never
/// reached the validator beforehand, because the flyout was installed as <c>AttachedFlyout</c>
/// metadata that nothing ever called <c>ShowAttachedFlyout</c> on. A latent crash masked by a
/// separate defect reads exactly like a working code path. <c>FlyoutPlacementGuardTests</c>
/// now holds this file to being the only one under <c>src/Reactor</c> that writes the DP, and
/// holds every flyout mount/update site to calling <see cref="Apply"/>, so a second helper
/// cannot reappear unnoticed.
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
    /// Applies an element's placement to a live flyout: clears the property for
    /// <see cref="WinPrim.FlyoutPlacementMode.Auto"/> so it falls back to the platform
    /// default, and otherwise writes the value, skipping redundant writes.
    /// </summary>
    internal static void Apply(WinPrim.FlyoutBase flyout, WinPrim.FlyoutPlacementMode placement)
    {
        if (!ShouldApply(placement))
        {
            flyout.ClearValue(WinPrim.FlyoutBase.PlacementProperty);
            return;
        }
        if (flyout.Placement != placement) flyout.Placement = placement;
    }
}

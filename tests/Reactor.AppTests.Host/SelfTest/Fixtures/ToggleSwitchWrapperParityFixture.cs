using System.Threading.Tasks;
using Microsoft.UI.Reactor;            // Optional<T>
using Microsoft.UI.Reactor.Wrappers;   // [GenerateReactorWrapper], [WrapControlled]
using WinUI = Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Microsoft.UI.Reactor.AppTests.Host.SelfTest.Fixtures;

// Spec 058 §14 — a source-generated wrapper for the WinUI ToggleSwitch. The
// [WrapControlled] override binds the controlled IsOn prop to the
// non-conventional `Toggled` event (there is no `IsOnChanged`). The generator
// fills this partial with an Optional<bool> IsOn + OnIsOnChanged + a public
// .Controlled<bool, RoutedEventArgs> descriptor entry — the same shape the
// hand-written ToggleSwitchDescriptor produces.
[GenerateReactorWrapper(typeof(WinUI.ToggleSwitch))]
[WrapControlled("IsOn", ChangedEvent = "Toggled")]
internal partial record ToggleSwitchWrapperElement;

/// <summary>
/// Spec 058 §14 — first built-in parity smoke test. Proves the
/// source-generated <see cref="ToggleSwitchWrapperElement"/> reproduces the
/// spec-050 controlled-prop authority model that the hand-written
/// <c>ToggleSwitchDescriptor</c> implements:
/// <list type="bullet">
///   <item><b>Unset ⇒ uncontrolled:</b> a user toggle survives an unrelated re-render.</item>
///   <item><b>Set ⇒ force-assert:</b> a user toggle against a set value snaps back, and the callback fires.</item>
/// </list>
/// </summary>
internal static class ToggleSwitchWrapperParityFixture
{
    internal class Execution(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            await Unset_IsUncontrolled_SurvivesRerender();
            await Set_ForceAsserts_SnapsBack_AndCallbackFires();
        }

        // Unset IsOn ⇒ control owns the value; user interaction survives an
        // unrelated re-render (the descriptor's Update sees !HasValue → skip).
        private async Task Unset_IsUncontrolled_SurvivesRerender()
        {
            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var (n, setN) = ctx.UseState(0);
                return VStack(
                    Button("TSW_Bump", () => setN(n + 1)),
                    // isOn unset, no callback ⇒ uncontrolled.
                    ToggleSwitchWrapperElement.ToggleSwitch(header: "Wi-Fi"));
            });

            await Harness.Render();
            var ts = H.FindControl<WinUI.ToggleSwitch>(_ => true);
            H.Check("ToggleSwitchWrapper_Unset_Mounted", ts is not null);
            if (ts is null) return;
            H.Check("ToggleSwitchWrapper_Unset_HeaderApplied", ts.Header as string == "Wi-Fi");

            // Simulate the user turning it on.
            ts.IsOn = true;
            await Harness.Render();

            // Unrelated re-render must NOT clobber the user's value.
            H.ClickButton("TSW_Bump");
            await Harness.Render();
            ts = H.FindControl<WinUI.ToggleSwitch>(_ => true);
            H.Check("ToggleSwitchWrapper_Unset_SurvivesRerender", ts?.IsOn == true);
        }

        // Set IsOn ⇒ force-assert. The snap-back recipe: a constant set value +
        // a callback that bumps state so a user toggle re-renders and the
        // descriptor re-asserts the controlled value.
        private async Task Set_ForceAsserts_SnapsBack_AndCallbackFires()
        {
            var fires = 0;
            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var (tick, setTick) = ctx.UseState(0);
                return VStack(
                    ToggleSwitchWrapperElement.ToggleSwitch(
                        isOn: true,                                  // implicit Optional<bool>.Of(true)
                        onIsOnChanged: _ => { fires++; setTick(tick + 1); }));
            });

            await Harness.Render();
            var ts = H.FindControl<WinUI.ToggleSwitch>(_ => true);
            H.Check("ToggleSwitchWrapper_Set_Mounted", ts is not null);
            if (ts is null) return;
            H.Check("ToggleSwitchWrapper_Set_InitialForceAssert", ts.IsOn);

            // User toggles off → callback bumps state → re-render snaps it back.
            ts.IsOn = false;
            await Harness.Render();
            var snapped = await Harness.WaitFor(() =>
            {
                var c = H.FindControl<WinUI.ToggleSwitch>(_ => true);
                return c is not null && c.IsOn;
            }, 20, 25);

            H.Check("ToggleSwitchWrapper_Set_SnapsBack", snapped);
            H.Check("ToggleSwitchWrapper_Set_CallbackFired", fires >= 1);
        }
    }
}

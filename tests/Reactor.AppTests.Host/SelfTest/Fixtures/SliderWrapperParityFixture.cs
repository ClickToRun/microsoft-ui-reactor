using System.Threading.Tasks;
using Microsoft.UI.Reactor;            // Optional<T>
using Microsoft.UI.Reactor.Wrappers;   // [GenerateReactorWrapper]
using WinUI = Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Microsoft.UI.Reactor.AppTests.Host.SelfTest.Fixtures;

// Spec 058 §14 — a second built-in parity proof. Unlike ToggleSwitch, Slider's
// change event follows the convention (Value ↔ ValueChanged), so the controlled
// prop is discovered by AUTO-PAIR with no [WrapControlled] override — and its
// TArgs (RangeBaseValueChangedEventArgs) differs from RoutedEventArgs, proving
// the generic delegate-Invoke args resolution. (Slider's Min/Max coercion is a
// future P3 capability and is intentionally not exercised here.)
[GenerateReactorWrapper(typeof(WinUI.Slider))]
internal partial record SliderWrapperElement;

/// <summary>
/// Spec 058 §14 — proves the auto-paired, source-generated
/// <see cref="SliderWrapperElement"/> reproduces the spec-050 controlled-prop
/// authority model of the hand-written <c>SliderDescriptor</c>'s
/// <c>Value ↔ ValueChanged</c> entry.
/// </summary>
internal static class SliderWrapperParityFixture
{
    internal class Execution(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            await Unset_IsUncontrolled_SurvivesRerender();
            await Set_ForceAsserts_SnapsBack_AndCallbackFires();
        }

        // Unset Value ⇒ control owns it; a user drag survives an unrelated re-render.
        private async Task Unset_IsUncontrolled_SurvivesRerender()
        {
            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var (n, setN) = ctx.UseState(0);
                return VStack(
                    Button("SLW_Bump", () => setN(n + 1)),
                    SliderWrapperElement.Slider());   // value unset, no callback ⇒ uncontrolled
            });

            await Harness.Render();
            var s = H.FindControl<WinUI.Slider>(_ => true);
            H.Check("SliderWrapper_Unset_Mounted", s is not null);
            if (s is null) return;

            // Simulate a user drag.
            s.Value = 30;
            await Harness.Render();

            // Unrelated re-render must NOT clobber the user's value.
            H.ClickButton("SLW_Bump");
            await Harness.Render();
            s = H.FindControl<WinUI.Slider>(_ => true);
            H.Check("SliderWrapper_Unset_SurvivesRerender", s?.Value == 30);
        }

        // Set Value ⇒ force-assert; a user drag snaps back and the callback fires.
        private async Task Set_ForceAsserts_SnapsBack_AndCallbackFires()
        {
            var fires = 0;
            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var (tick, setTick) = ctx.UseState(0);
                return VStack(
                    SliderWrapperElement.Slider(
                        value: 50.0,                                 // implicit Optional<double>.Of(50)
                        onValueChanged: _ => { fires++; setTick(tick + 1); }));
            });

            await Harness.Render();
            var s = H.FindControl<WinUI.Slider>(_ => true);
            H.Check("SliderWrapper_Set_Mounted", s is not null);
            if (s is null) return;
            H.Check("SliderWrapper_Set_InitialForceAssert", s.Value == 50);

            // User drags to 20 → callback bumps state → re-render snaps back to 50.
            s.Value = 20;
            await Harness.Render();
            var snapped = await Harness.WaitFor(() =>
            {
                var c = H.FindControl<WinUI.Slider>(_ => true);
                return c is not null && c.Value == 50;
            }, 20, 25);

            H.Check("SliderWrapper_Set_SnapsBack", snapped);
            H.Check("SliderWrapper_Set_CallbackFired", fires >= 1);
        }
    }
}

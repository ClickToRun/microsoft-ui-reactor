using System.Threading.Tasks;
using Microsoft.UI.Reactor;            // Optional<T>
using Microsoft.UI.Reactor.Wrappers;   // [GenerateReactorWrapper], [WrapControlled], [WrapAlias]
using WinUI = Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Microsoft.UI.Reactor.AppTests.Host.SelfTest.Fixtures;

// Spec 058 §14 (P3) — multi-event controlled parity proof. RadioButton's
// two-way IsChecked is signalled by TWO events (Checked + Unchecked), not a
// single {Prop}Changed; [WrapControlled(Events = …)] wires both to the shared
// handler and reads the value back from the control. This is the generated
// analogue of the hand-written RadioButtonDescriptor's
// `.Controlled<…>(subscribe: rb.Checked += …; rb.Unchecked += …)` entry. The
// control's bool? IsChecked is surfaced faithfully as Optional<bool?>.
[GenerateReactorWrapper(typeof(WinUI.RadioButton))]
[WrapControlled("IsChecked", Events = new[] { "Checked", "Unchecked" })]
[WrapAlias("Label", "Content")]
internal partial record RadioButtonWrapperElement;

/// <summary>
/// Spec 058 §14 — proves the source-generated <see cref="RadioButtonWrapperElement"/>
/// reproduces the spec-050 controlled-prop authority model across MULTIPLE
/// change events (Checked + Unchecked), matching the hand-written
/// <c>RadioButtonDescriptor</c>.
/// </summary>
internal static class RadioButtonWrapperParityFixture
{
    internal class Execution(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            await Unset_IsUncontrolled_SurvivesRerender();
            await Set_ForceAsserts_SnapsBack_OnUncheck_AndCallbackFires();
        }

        // Unset IsChecked ⇒ control owns it; a user check survives an unrelated re-render.
        private async Task Unset_IsUncontrolled_SurvivesRerender()
        {
            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var (n, setN) = ctx.UseState(0);
                return VStack(
                    Button("RBW_Bump", () => setN(n + 1)),
                    RadioButtonWrapperElement.RadioButton(label: "opt"));   // unset ⇒ uncontrolled
            });

            await Harness.Render();
            var rb = H.FindControl<WinUI.RadioButton>(_ => true);
            H.Check("RadioButtonWrapper_Unset_Mounted", rb is not null);
            if (rb is null) return;

            // Simulate a user check (fires Checked).
            rb.IsChecked = true;
            await Harness.Render();

            // Unrelated re-render must NOT clobber the user's value.
            H.ClickButton("RBW_Bump");
            await Harness.Render();
            rb = H.FindControl<WinUI.RadioButton>(_ => true);
            H.Check("RadioButtonWrapper_Unset_SurvivesRerender", rb?.IsChecked == true);
        }

        // Set IsChecked=true ⇒ force-assert; a user UNCHECK (Unchecked event)
        // snaps back to true and fires the callback — exercising the second of
        // the two wired events.
        private async Task Set_ForceAsserts_SnapsBack_OnUncheck_AndCallbackFires()
        {
            var fires = 0;
            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var (tick, setTick) = ctx.UseState(0);
                return VStack(
                    RadioButtonWrapperElement.RadioButton(
                        label: "opt",
                        isChecked: (bool?)true,                          // implicit Optional<bool?>.Of(true)
                        onIsCheckedChanged: _ => { fires++; setTick(tick + 1); }));
            });

            await Harness.Render();
            var rb = H.FindControl<WinUI.RadioButton>(_ => true);
            H.Check("RadioButtonWrapper_Set_Mounted", rb is not null);
            if (rb is null) return;
            H.Check("RadioButtonWrapper_Set_InitialForceAssert", rb.IsChecked == true);

            // User unchecks → Unchecked fires → callback bumps state → re-render snaps back to true.
            rb.IsChecked = false;
            await Harness.Render();
            var snapped = await Harness.WaitFor(() =>
            {
                var c = H.FindControl<WinUI.RadioButton>(_ => true);
                return c is not null && c.IsChecked == true;
            }, 20, 25);

            H.Check("RadioButtonWrapper_Set_SnapsBack", snapped);
            H.Check("RadioButtonWrapper_Set_CallbackFired", fires >= 1);
        }
    }
}

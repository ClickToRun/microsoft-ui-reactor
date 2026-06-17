using System.Threading.Tasks;
using WinUI = Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Microsoft.UI.Reactor.AppTests.Host.SelfTest.Fixtures;

// Spec 058 §15 (P5.4) — runtime guard for the descriptor-only "demote-to-one-way"
// fix. ProgressBar (RangeBase) exposes ValueChanged, so the generator auto-paired
// Value to a controlled prop; because ProgressElement declares no OnValueChanged,
// the descriptor-only pass demotes it to a one-way write. Without the fix Value was
// dropped (silently never written) — and NO existing test mounted the ProgressBar
// control to catch it (the "Progress.Value" tests are all taskbar progress).
internal static class ProgressBarValueFixture
{
    internal class Execution(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var host = H.CreateHost();
            host.Mount(_ => Progress(0.6) with { Minimum = 0, Maximum = 1 });
            await Harness.Render();

            var bar = H.FindControl<WinUI.ProgressBar>(_ => true);
            H.Check("ProgressBarValue_Mounted", bar is not null);
            if (bar is null) return;

            // The generated one-way descriptor must actually write Value (the
            // demote-to-one-way fix; a regression would leave it at 0).
            H.Check("ProgressBarValue_Written", global::System.Math.Abs(bar.Value - 0.6) < 1e-9);
            H.Check("ProgressBarValue_NotIndeterminate", !bar.IsIndeterminate);
        }
    }
}

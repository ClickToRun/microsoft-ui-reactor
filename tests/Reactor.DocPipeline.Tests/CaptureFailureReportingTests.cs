using System;
using System.IO;
using Microsoft.UI.Reactor.Cli.Docs;
using Xunit;

namespace Microsoft.UI.Reactor.Cli.Docs.Tests;

/// <summary>
/// Pins that a per-screenshot capture failure is legible on <em>each stream on
/// its own</em>, rather than only when stdout and stderr are interleaved.
/// </summary>
/// <remarks>
/// <para>
/// <c>CaptureAsync</c> writes <c>"    Capturing &lt;id&gt;..."</c> to stdout with no
/// newline. The success path completes that line with <c>" ✓"</c> on the same
/// stream; the four failure paths used to complete it with <c>" ✗ ..."</c> on
/// <strong>stderr</strong>. Since the id is only ever written to stdout, a reader
/// of stderr alone saw <c>✗ no frame produced within deadline</c> with nothing
/// naming the screenshot — and a reader of stdout alone saw a
/// <c>Capturing &lt;id&gt;...</c> that never resolved.
/// </para>
/// <para>
/// The load-bearing assertion here is the <em>id on stderr</em>. Reverting
/// <see cref="ScreenshotCapture.ReportCaptureFailure"/> to the old
/// <c>Console.Error.WriteLine($" ✗ {detail}")</c> leaves the detail, the marker and
/// the failure count all intact and fails only that assertion — which is the whole
/// defect, so the test can come out the other way for exactly the right reason.
/// </para>
/// <para>
/// <see cref="Console"/> redirection is process-global, hence the shared
/// console-isolation collection.
/// </para>
/// </remarks>
[Collection("ConsoleTests")]
public class CaptureFailureReportingTests
{
    [Fact]
    public void Capture_failure_names_its_screenshot_on_stderr_and_closes_the_stdout_line()
    {
        var (stdout, stderr) = CaptureStreams(() =>
        {
            // Reproduces the real sequence: the progress line is opened on stdout,
            // then the failure is reported.
            Console.Write("    Capturing hero-shot...");
            ScreenshotCapture.ReportCaptureFailure("hero-shot", "no frame produced within deadline");
        });

        // stdout: the in-progress line is completed, so the next screenshot's
        // progress text cannot land on the same visual line.
        Assert.Contains("Capturing hero-shot...", stdout);
        Assert.EndsWith(Environment.NewLine, stdout);

        // stderr: self-contained. This is the assertion the old form fails —
        // it carried the detail but never the id.
        Assert.Contains("hero-shot", stderr);
        Assert.Contains("no frame produced within deadline", stderr);
    }

    /// <summary>
    /// Guards the premise of the test above: the id must reach stderr because
    /// <see cref="ScreenshotCapture.ReportCaptureFailure"/> puts it there, not
    /// because the stdout progress line leaked into the same buffer. Without this,
    /// a harness that merged the two streams would satisfy the assertions above
    /// while the defect was fully present.
    /// </summary>
    [Fact]
    public void The_id_reaches_stderr_even_with_no_stdout_progress_line()
    {
        var (stdout, stderr) = CaptureStreams(() =>
            ScreenshotCapture.ReportCaptureFailure("widget-thumb", "boom"));

        Assert.DoesNotContain("widget-thumb", stdout);
        Assert.Contains("widget-thumb", stderr);
        Assert.Contains("boom", stderr);
    }

    private static (string Stdout, string Stderr) CaptureStreams(Action body)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();
        try
        {
            Console.SetOut(outWriter);
            Console.SetError(errWriter);
            body();
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        return (outWriter.ToString(), errWriter.ToString());
    }
}

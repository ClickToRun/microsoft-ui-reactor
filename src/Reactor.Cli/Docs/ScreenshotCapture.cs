using System.Diagnostics;

namespace Microsoft.UI.Reactor.Cli.Docs;

/// <summary>
/// Captures screenshots from a running Reactor doc app via the PreviewCaptureServer HTTP API.
/// Launches the app with <c>--preview --vscode</c> to enable the capture endpoint,
/// waits for the startup delay, then captures frames via <c>GET /frame</c>.
/// </summary>
internal static class ScreenshotCapture
{
    /// <summary>
    /// Outcome of one topic's capture pass. <see cref="Failed"/> counts
    /// screenshots that were requested but produced no written file — the
    /// caller turns a non-zero count into a compile error rather than letting
    /// a silent mass-failure look like a clean run.
    /// </summary>
    internal sealed record CaptureResult(int Written, int Failed)
    {
        public int Requested => Written + Failed;
    }

    /// <summary>
    /// Processes a captured frame and writes it to <paramref name="outputPath"/>.
    /// </summary>
    /// <remarks>
    /// The ordering here is the fix for issue #989 and is load-bearing:
    /// <see cref="ImageProcessor"/> throws <see cref="BlankFrameException"/> for a
    /// contentless frame <em>before</em> this method touches the filesystem, so a
    /// doc app that never painted can no longer replace a good committed
    /// screenshot with a solid-white stub. Any refactor that opens the output
    /// file first — or that catches the exception in here and writes anyway —
    /// reintroduces the bug, which is why this seam is tested directly rather
    /// than only through the (desktop-bound) capture loop.
    /// </remarks>
    /// <exception cref="BlankFrameException">
    /// The frame has no visible content. Nothing is written; the existing file,
    /// if any, is left exactly as it was.
    /// </exception>
    internal static void ProcessAndWrite(byte[] frameBytes, string outputPath, ScreenshotConfig screenshot)
    {
        var isThumb = string.Equals(screenshot.Kind, "catalog-thumb", StringComparison.OrdinalIgnoreCase);
        var processed = isThumb
            ? ImageProcessor.ProcessThumb(frameBytes, screenshot.ThumbWidth, screenshot.ThumbHeight)
            : ImageProcessor.Process(frameBytes, ImageProcessor.ParseCropMode(screenshot.Crop));
        File.WriteAllBytes(outputPath, processed);
    }

    public static async Task<CaptureResult> CaptureAsync(
        string appDir,
        string topicId,
        DocManifest manifest,
        string outputImagesDir,
        IReadOnlySet<string>? screenshotFilter = null)
    {
        var screenshots = manifest.Screenshots
            .Where(s => screenshotFilter is null || screenshotFilter.Contains($"{topicId}/{s.Id}"))
            .ToList();

        if (screenshots.Count == 0)
        {
            Console.WriteLine("    No matching screenshots.");
            return new CaptureResult(0, 0);
        }

        var csprojFiles = Directory.GetFiles(appDir, "*.csproj");
        if (csprojFiles.Length == 0)
        {
            Console.Error.WriteLine($"    ✗ No .csproj found in {appDir}");
            return new CaptureResult(0, screenshots.Count);
        }

        var csproj = csprojFiles[0];
        Console.WriteLine($"    Launching {Path.GetFileName(csproj)} for capture...");

        // WindowsAppSDK self-contained run requires an explicit architecture;
        // match the host so dotnet run picks up the matching build output.
        var platform = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "ARM64",
            _ => "x64",
        };

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{csproj}\" -p:Platform={platform} -- --preview --vscode --fps 5",
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = false,
        };

        using var process = Process.Start(psi);
        if (process == null)
        {
            Console.Error.WriteLine("    ✗ Failed to start process");
            return new CaptureResult(0, screenshots.Count);
        }

        int written = 0, failed = 0;
        try
        {
            var (port, token) = await WaitForCaptureHandshake(process, TimeSpan.FromSeconds(30));
            if (port < 0 || token is null)
            {
                Console.Error.WriteLine("    ✗ Timed out waiting for capture port");
                return new CaptureResult(0, screenshots.Count);
            }

            Console.WriteLine($"    Capture server on port {port}");

            var delay = manifest.App.StartupDelay;
            Console.WriteLine($"    Waiting {delay}ms for app startup...");
            await Task.Delay(delay);

            var topicDir = Path.Combine(outputImagesDir, topicId);
            Directory.CreateDirectory(topicDir);

            using var http = new HttpClient();
            // SECURITY (TASK-018): the capture server requires a per-launch
            // bearer token on every request. We read it from the app's stdout
            // alongside CAPTURE_PORT.
            http.DefaultRequestHeaders.Authorization =
                new global::System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            // Warm-up: the capture server starts its capture timer lazily on the
            // first /frame call. Kick it now and wait for the first frame so the
            // first manifest entry doesn't pay the timer-startup latency.
            // Best-effort: a warm-up that throws must not escape and abort the
            // whole pass, because CaptureAsync's contract is that every
            // requested screenshot comes back counted in Written or Failed.
            try
            {
                await PollForFrame(http, port, TimeSpan.FromSeconds(10));
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                Console.Error.WriteLine($"    ⚠ warm-up frame request failed ({ex.GetType().Name}); continuing");
            }

            foreach (var screenshot in screenshots)
            {
                Console.Write($"    Capturing {screenshot.Id}...");
                string? outputPath = null;
                try
                {
                    // Switch to the target component if specified
                    if (!string.IsNullOrEmpty(screenshot.Component))
                    {
                        var json = $"{{\"component\":\"{screenshot.Component}\"}}";
                        var content = new StringContent(json, global::System.Text.Encoding.UTF8, "application/json");
                        var switchResp = await http.PostAsync($"http://localhost:{port}/preview", content);
                        if (!switchResp.IsSuccessStatusCode)
                        {
                            Console.Error.WriteLine($" ✗ Failed to switch to component '{screenshot.Component}' ({switchResp.StatusCode})");
                            failed++;
                            continue;
                        }
                        // Wait for the component to render and a new frame to be captured
                        // At 5 fps, frames arrive every 200ms; wait long enough for
                        // the switch + layout + at least one fresh capture cycle.
                        await Task.Delay(1000);
                    }

                    // The capture timer only starts once a reader hits /frame
                    // (TASK-025), so the first call returns 204 with no body.
                    // Poll until a frame is ready or we exceed the deadline.
                    var frameBytes = await PollForFrame(http, port, TimeSpan.FromSeconds(5), requireContent: true);
                    if (frameBytes.Length == 0)
                    {
                        Console.Error.WriteLine($" ✗ no frame produced within deadline");
                        failed++;
                        continue;
                    }
                    // Catalog-thumb captures land at `<id>-thumb.<format>` so the
                    // controls-catalog index can refer to them without colliding with
                    // a full-size screenshot of the same id (spec 041 §6.3 + §12 Q7).
                    var isThumb = string.Equals(screenshot.Kind, "catalog-thumb", StringComparison.OrdinalIgnoreCase);
                    var fileBase = isThumb ? $"{screenshot.Id}{ImageProcessor.ThumbSuffix}" : screenshot.Id;
                    outputPath = Path.GetFullPath(Path.Combine(topicDir, $"{fileBase}.{screenshot.Format}"));
                    if (!outputPath.StartsWith(Path.GetFullPath(topicDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Screenshot id '{screenshot.Id}' would escape output directory");
                    ProcessAndWrite(frameBytes, outputPath, screenshot);
                    written++;
                    Console.WriteLine(" ✓");
                }
                catch (BlankFrameException ex)
                {
                    var existing = outputPath is not null && File.Exists(outputPath)
                        ? " — existing screenshot left untouched"
                        : "";
                    Console.Error.WriteLine($" ✗ {ex.Message}{existing}");
                    failed++;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException
                                             or InvalidOperationException or ArgumentException
                                             or TaskCanceledException)
                {
                    // ArgumentException covers a malformed manifest (unknown
                    // crop mode) and a frame the processor rejects (non-image
                    // bytes, over the size/dimension cap). Those used to escape
                    // CaptureAsync entirely, aborting the pass mid-topic and
                    // leaving the remaining screenshots uncounted.
                    Console.Error.WriteLine($" ✗ {ex}");
                    failed++;
                }
            }

            return new CaptureResult(written, failed);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Polls <c>/frame</c> until the server returns a body the caller can use,
    /// or the deadline expires. The capture timer starts lazily on first
    /// reader, so early calls return HTTP 204 with no content.
    /// </summary>
    /// <param name="requireContent">
    /// When true, a decoded frame with no visible content is treated as
    /// "not ready yet" and polling continues. A cold window's first painted
    /// frame is often still blank; holding out for a real one turns what used
    /// to be a corrupt overwrite into a correct capture. The last blank frame
    /// is still returned when the deadline expires, so the caller sees the
    /// same <see cref="BlankFrameException"/> it would have seen anyway rather
    /// than a misleading "no frame produced".
    /// </param>
    internal static async Task<byte[]> PollForFrame(
        HttpClient http, int port, TimeSpan deadline, bool requireContent = false)
    {
        var sw = global::System.Diagnostics.Stopwatch.StartNew();
        var lastBytes = Array.Empty<byte>();
        while (sw.Elapsed < deadline)
        {
            using var resp = await http.GetAsync($"http://localhost:{port}/frame");
            if (resp.StatusCode == global::System.Net.HttpStatusCode.OK)
            {
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                if (bytes.Length > 0)
                {
                    if (!requireContent) return bytes;
                    lastBytes = bytes;
                    if (ImageProcessor.FrameHasContent(bytes)) return bytes;
                }
            }
            await Task.Delay(100);
        }
        return lastBytes;
    }

    /// <summary>
    /// Reads the app's stdout for the <c>CAPTURE_PORT=</c> and <c>CAPTURE_TOKEN=</c>
    /// handshake lines emitted by <see cref="Reactor.Hosting.PreviewCaptureServer.Start"/>.
    /// Both must arrive within <paramref name="timeout"/> for the capture client to
    /// authenticate. Returns <c>(-1, null)</c> on timeout.
    /// </summary>
    private static async Task<(int Port, string? Token)> WaitForCaptureHandshake(Process process, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        int port = -1;
        string? token = null;
        try
        {
            while (!cts.Token.IsCancellationRequested && (port < 0 || token is null))
            {
                var line = await process.StandardOutput.ReadLineAsync(cts.Token);
                if (line == null) break;

                if (port < 0 && line.StartsWith("CAPTURE_PORT=") &&
                    int.TryParse(line.AsSpan("CAPTURE_PORT=".Length), out var parsed))
                {
                    port = parsed;
                }
                else if (token is null && line.StartsWith("CAPTURE_TOKEN="))
                {
                    token = line.Substring("CAPTURE_TOKEN=".Length);
                }
            }
        }
        catch (OperationCanceledException) { }

        if (port >= 0 && token is not null)
        {
            // Drain stdout in background to prevent buffer deadlock
            _ = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync() != null) { }
            });
            return (port, token);
        }
        return (-1, null);
    }
}

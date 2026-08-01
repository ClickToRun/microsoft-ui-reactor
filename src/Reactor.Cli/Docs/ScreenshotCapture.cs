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

        // Write to a sibling temp file and move it into place, rather than
        // File.WriteAllBytes straight onto the destination.
        //
        // WriteAllBytes opens the destination with truncation, so a fault
        // *during* the write (disk full, a transient IO error, an antivirus
        // lock) leaves the committed screenshot destroyed and partially
        // rewritten — and the caller then reports a failed capture, which this
        // command advertises as leaving the existing file untouched. That
        // contradiction is the exact failure this whole change exists to
        // prevent, reached by a different door: a guard that refuses to write a
        // bad frame is worth little if the write itself can shred a good file.
        //
        // The temp file is a sibling so the move stays within one volume and is
        // a rename rather than a copy; a mid-write fault now destroys only the
        // temp, and the destination is either fully old or fully new.
        var dir = Path.GetDirectoryName(outputPath)!;
        var temp = Path.Join(dir, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, processed);
            File.Move(temp, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A leaked temp is untidy, not harmful, and must never mask
                    // the write fault that put us here.
                }
            }
        }
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

            // The file-level guard below decides whether a screenshot lands
            // inside topicDir — but that is only meaningful if topicDir is
            // itself inside outputImagesDir, and this line is what establishes
            // it. With Path.Combine a rooted topicId would relocate topicDir,
            // and the file guard would then compare an escaped path against an
            // escaped root and pass: the guard would run, be correct, and
            // answer the wrong question.
            //
            // Today topicId comes from Path.GetFileName over a directory
            // enumeration (CompileCommand.DiscoverApps), so it can be neither
            // rooted nor a traversal and this changes nothing at runtime. That
            // safety is a property of a function three call levels away, which
            // is exactly the coupling that holds until someone changes the other
            // end. Resolving it here costs one call and removes the dependency.
            // Note the helper *contains* a rooted segment rather than rejecting
            // it — Join keeps the base, so there is nothing left to reject — and
            // throws only for a traversal that walks back out.
            var topicDir = DocPaths.ResolveContained(outputImagesDir, topicId, $"Topic id '{topicId}'");
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
                    // A manifest-authored id reaches the filesystem here, so the
                    // join and the containment test must happen together — see
                    // DocPaths.ResolveContained for why either alone is
                    // defeatable.
                    outputPath = DocPaths.ResolveContained(
                        topicDir, $"{fileBase}.{screenshot.Format}", $"Screenshot id '{screenshot.Id}'");
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
                                             or TaskCanceledException or OutOfMemoryException
                                             or UnauthorizedAccessException
                                             or global::System.Runtime.InteropServices.ExternalException)
                {
                    // ArgumentException covers a malformed manifest (unknown
                    // crop mode) and a frame the processor rejects (non-image
                    // bytes, over the size/dimension cap). Those used to escape
                    // CaptureAsync entirely, aborting the pass mid-topic and
                    // leaving the remaining screenshots uncounted.
                    //
                    // ExternalException/OutOfMemoryException are the other two
                    // faces GDI+ shows for a corrupt frame — Bitmap.Save and the
                    // Graphics operations in ProcessAndWrite raise them, and the
                    // OOM one carries no memory-pressure meaning. Without them
                    // the Written/Failed contract this method now advertises
                    // would silently not hold for exactly the malformed input
                    // this PR exists to handle.
                    //
                    // UnauthorizedAccessException is the shape a blocked
                    // *replace* takes: ProcessAndWrite moves a temp file onto
                    // the destination, and File.Move raises it (not IOException)
                    // when the destination is locked or read-only. Omitting it
                    // would let one undeletable file abort the whole capture
                    // pass — which is the same silent-mass-failure shape the
                    // Written/Failed counters exist to prevent.
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
    /// to be a corrupt overwrite into a correct capture.
    /// <para>
    /// If the deadline expires after at least one blank frame was seen, that
    /// frame is returned, so the caller gets the same
    /// <see cref="BlankFrameException"/> it would have seen without this flag
    /// rather than a different error for the same underlying problem. If no
    /// frame ever arrived — the server only ever answered 204, or with an empty
    /// body — the result is empty and the caller reports "no frame produced",
    /// which is the accurate answer for that case and not a regression this
    /// flag introduces. The two are distinct failures and neither is masked:
    /// setting this flag never converts a produced frame into no frame.
    /// </para>
    /// </param>
    /// <remarks>
    /// The deadline bounds the whole call, not just the gaps between polls.
    /// Each request carries a token cancelled by whatever time is left, because
    /// <see cref="HttpClient"/> otherwise applies its own 100-second default —
    /// twenty times the 5-second deadline this is called with. A server that
    /// accepts the connection and never answers would then spend the entire
    /// budget inside a single request, so the loop that exists to outlast a
    /// cold window's blank first frame would run exactly once and give up.
    /// The failure direction is safe (no frame, so nothing is written) but the
    /// retry it silently loses is the thing that turns a blank first frame into
    /// a correct capture.
    /// </remarks>
    internal static async Task<byte[]> PollForFrame(
        HttpClient http, int port, TimeSpan deadline, bool requireContent = false)
    {
        var sw = global::System.Diagnostics.Stopwatch.StartNew();
        var lastBytes = Array.Empty<byte>();
        while (true)
        {
            var remaining = deadline - sw.Elapsed;
            if (remaining <= TimeSpan.Zero) break;

            // Filtered on our own token: a cancellation we asked for is the
            // deadline working, and returning lastBytes is the documented
            // answer for it. Anything else — a transport fault, or
            // HttpClient's own timeout if it ever won the race — still
            // propagates, because a swallowed fault here would surface only as
            // a capture that is mysteriously short of frames.
            using var cts = new CancellationTokenSource(remaining);
            try
            {
                using var resp = await http.GetAsync($"http://localhost:{port}/frame", cts.Token);
                if (resp.StatusCode == global::System.Net.HttpStatusCode.OK)
                {
                    var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
                    if (bytes.Length > 0)
                    {
                        if (!requireContent) return bytes;
                        lastBytes = bytes;
                        if (ImageProcessor.FrameHasContent(bytes)) return bytes;
                    }
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                break;
            }

            var left = deadline - sw.Elapsed;
            if (left <= TimeSpan.Zero) break;
            await Task.Delay(left < PollInterval ? left : PollInterval);
        }
        return lastBytes;
    }

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

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

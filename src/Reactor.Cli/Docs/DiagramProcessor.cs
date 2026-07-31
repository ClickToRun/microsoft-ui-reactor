using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Microsoft.UI.Reactor.Cli.Docs;

/// <summary>
/// Indirection over the Mermaid CLI binary (<c>mmdc</c>) so unit tests can
/// stub the renderer without requiring it on PATH.
/// </summary>
internal interface IMermaidRunner
{
    /// <summary>True when an <c>mmdc</c> binary is on PATH.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Render <paramref name="inputPath"/> (<c>.mmd</c>) to
    /// <paramref name="outputPath"/> (<c>.svg</c>). Returns true on success;
    /// errors should be written to <paramref name="error"/>.
    /// </summary>
    bool Render(string inputPath, string outputPath, out string error);

    /// <summary>
    /// Command line the runner would invoke for the given paths. Exposed
    /// for unit tests to assert the assembled invocation.
    /// </summary>
    string CommandLine(string inputPath, string outputPath);
}

/// <summary>
/// Real <c>mmdc</c> runner. PATH-detection is cached for the process
/// lifetime so subsequent diagrams don't re-shell-out to <c>where</c>.
/// </summary>
internal sealed class MmdcRunner : IMermaidRunner
{
    private bool? _available;

    public bool IsAvailable
    {
        get
        {
            _available ??= DetectMmdc();
            return _available.Value;
        }
    }

    public string CommandLine(string inputPath, string outputPath) =>
        $"mmdc -i \"{inputPath}\" -o \"{outputPath}\"";

    public bool Render(string inputPath, string outputPath, out string error)
    {
        error = "";
        if (!IsAvailable)
        {
            error = "mmdc not on PATH";
            return false;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "mmdc",
            Arguments = $"-i \"{inputPath}\" -o \"{outputPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) { error = "failed to start mmdc"; return false; }
            error = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool DetectMmdc()
    {
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "where" : "which",
            Arguments = "mmdc",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit();
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Spec §10.3 diagram pipeline:
/// <list type="bullet">
///   <item>Copies <c>*.svg</c> from <c>docs/_pipeline/diagrams/&lt;topic&gt;/</c>
///         to <c>docs/guide/images/&lt;topic&gt;/</c> (idempotent — SHA-256
///         hash compare).</item>
///   <item>Invokes <c>mmdc</c> for each <c>*.mmd</c> with content-hash
///         caching so unchanged diagrams don't re-render.</item>
///   <item>Validates <c>![..](images/&lt;topic&gt;/...)</c> references in
///         compiled output.</item>
/// </list>
/// </summary>
internal static class DiagramProcessor
{
    /// <summary>
    /// Aggregate of files processed during one diagram pass; surfaced so
    /// callers can write a clean summary line.
    /// </summary>
    internal sealed class DiagramResult
    {
        public List<string> CopiedSvgs { get; } = [];
        public List<string> SkippedSvgs { get; } = [];
        public List<string> RenderedMermaid { get; } = [];
        public List<string> CachedMermaid { get; } = [];
        public List<TierLintFinding> Findings { get; } = [];
    }

    /// <summary>
    /// Process all diagrams in <paramref name="diagramsRoot"/>. When
    /// <paramref name="topic"/> is non-null only that subdirectory is
    /// processed.
    /// </summary>
    public static DiagramResult Process(
        string diagramsRoot,
        string outputImagesRoot,
        IMermaidRunner mermaid,
        string? topic = null)
    {
        var result = new DiagramResult();
        if (!Directory.Exists(diagramsRoot)) return result;

        var topics = topic is null
            ? Directory.GetDirectories(diagramsRoot)
            : new[] { Path.Combine(diagramsRoot, topic) }.Where(Directory.Exists).ToArray();

        foreach (var topicDir in topics)
        {
            var topicId = Path.GetFileName(topicDir);
            var outDir = Path.Combine(outputImagesRoot, topicId);
            Directory.CreateDirectory(outDir);

            // SVG passthrough — hash-compare so identical content is skipped.
            foreach (var svg in Directory.GetFiles(topicDir, "*.svg"))
            {
                var dest = Path.Combine(outDir, Path.GetFileName(svg));
                if (File.Exists(dest) && FilesIdentical(svg, dest))
                {
                    result.SkippedSvgs.Add(Path.GetFileName(svg));
                    continue;
                }
                File.Copy(svg, dest, overwrite: true);
                result.CopiedSvgs.Add(Path.GetFileName(svg));
            }

            // Mermaid render — cache-hash → only re-render on change.
            var mmds = Directory.GetFiles(topicDir, "*.mmd");
            if (mmds.Length > 0 && !mermaid.IsAvailable)
            {
                result.Findings.Add(new TierLintFinding(
                    "REACTOR_DOC_DIAGRAM_001",
                    "mermaid-cli not installed; see docs/contributing/doc-pipeline.md",
                    topicDir, 1, TierLintSeverity.Error));
                continue;
            }

            foreach (var mmd in mmds)
            {
                var name = Path.GetFileNameWithoutExtension(mmd);
                var dest = Path.Combine(outDir, name + ".svg");
                var hashFile = Path.Combine(outDir, "." + name + ".mmd.sha256");
                var currentHash = HashFile(mmd);

                if (File.Exists(dest) && File.Exists(hashFile) &&
                    File.ReadAllText(hashFile).Trim() == currentHash)
                {
                    result.CachedMermaid.Add(name);
                    continue;
                }

                if (!mermaid.Render(mmd, dest, out var err))
                {
                    result.Findings.Add(new TierLintFinding(
                        "REACTOR_DOC_DIAGRAM_001",
                        $"mmdc render failed for {Path.GetFileName(mmd)}: {err}",
                        mmd, 1, TierLintSeverity.Error));
                    continue;
                }

                File.WriteAllText(hashFile, currentHash);
                result.RenderedMermaid.Add(name);
            }
        }

        return result;
    }

    /// <summary>
    /// Validate every <c>![...](images/&lt;topic&gt;/...)</c> reference in
    /// <paramref name="body"/> resolves to a file under
    /// <paramref name="imagesRoot"/>. Missing files raise
    /// <c>REACTOR_DOC_IMAGE_001</c>; a raster image whose interior is blank
    /// raises <c>REACTOR_DOC_IMAGE_002</c>.
    /// </summary>
    /// <param name="blankCache">
    /// Optional path-keyed cache of the blank verdict. The same screenshot is
    /// referenced from several pages, and this runs once per compiled page, so
    /// without a cache the corpus would be decoded many times over. Pass the
    /// same instance across every call in one compile.
    /// </param>
    public static List<TierLintFinding> ValidateImageRefs(
        string filePath, string body, string imagesRoot,
        Dictionary<string, bool>? blankCache = null)
    {
        var findings = new List<TierLintFinding>();
        var imagesFull = Path.GetFullPath(imagesRoot);
        foreach (Match m in ImagePattern.Matches(body))
        {
            var rel = m.Groups[1].Value;
            var line = body[..m.Index].Count(c => c == '\n') + 1;

            // The leading "../" run is page-relative escaping emitted by
            // DocAssembler for nested topics; every ref lands in the same
            // images tree regardless of depth, so strip it and resolve against
            // that tree rather than replaying the traversal.
            var normalized = rel;
            while (normalized.StartsWith("../", StringComparison.Ordinal))
                normalized = normalized["../".Length..];

            // Path.Join rather than Path.Combine: Combine drops everything before
            // a rooted segment, which would hand IsUnder an absolute path derived
            // entirely from page content. Join keeps the base, so the containment
            // check below stays the thing that decides.
            var full = Path.GetFullPath(Path.Join(
                imagesFull, "..", normalized.Replace('/', Path.DirectorySeparatorChar)));

            // Anything that still resolves outside the images tree is a
            // malformed reference, not a file to go probing for.
            if (!IsUnder(full, imagesFull) || !File.Exists(full))
            {
                findings.Add(new TierLintFinding(
                    "REACTOR_DOC_IMAGE_001",
                    $"broken image reference: {rel}",
                    filePath, line, TierLintSeverity.Error));
                continue;
            }

            if (IsBlankRaster(full, blankCache))
            {
                findings.Add(new TierLintFinding(
                    "REACTOR_DOC_IMAGE_002",
                    $"blank screenshot: {rel} has no visible content — it was most likely " +
                    "overwritten by a capture whose doc-app window never painted. Restore it " +
                    "from git and re-capture on an interactive desktop.",
                    filePath, line, TierLintSeverity.Error));
            }
        }
        return findings;
    }

    private static bool IsUnder(string candidate, string root)
    {
        var rooted = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rooted, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="path"/> is a raster image whose interior
    /// (excluding the border + drop shadow the capture pipeline draws) contains
    /// no content at all. Non-raster references (SVG) and undecodable files are
    /// never reported — this gate exists to catch the specific solid-white stub
    /// a failed capture produces, not to second-guess authored assets.
    /// </summary>
    /// <remarks>
    /// The predicate is strictly "zero content pixels", never "small". The
    /// committed corpus contains legitimately tiny screenshots — the smallest
    /// is 89×40 / 2127&#160;B — so any byte-size floor able to catch a ~3&#160;KB stub
    /// would also condemn real assets. Measured margin: across all 227
    /// committed PNGs the *sparsest* interior is 0.6084&#160;% content pixels
    /// (<c>navigation/navigation-view.png</c>), i.e. ~600× the zero threshold.
    /// <c>DocImageIntegrityTests.Committed_screenshot_corpus_has_no_blank_images</c>
    /// re-measures this on every run and logs it.
    /// </remarks>
    private static bool IsBlankRaster(string path, Dictionary<string, bool>? cache)
    {
        if (cache is not null && cache.TryGetValue(path, out var cached)) return cached;

        var blank = ComputeIsBlankRaster(path);
        cache?[path] = blank;
        return blank;
    }

    private static bool ComputeIsBlankRaster(string path)
    {
        var ext = Path.GetExtension(path);
        if (!ext.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            // Pre-decode guards, mirroring ImageProcessor.Process (TASK-044).
            // This walks every referenced file in the committed corpus and hands
            // it to GDI+, so a hostile or corrupt image must be rejected on
            // cheap metadata before a decoder ever sees it.
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > ImageProcessor.MaxImageBytes) return false;
            if (!HasRasterMagic(path)) return false;

            using var bmp = new global::System.Drawing.Bitmap(path);
            if (bmp.Width > ImageProcessor.MaxImageDimension ||
                bmp.Height > ImageProcessor.MaxImageDimension)
            {
                return false;
            }

            var region = ImageProcessor.ContentRegionFor(path, bmp.Width, bmp.Height);
            return !ImageProcessor.HasContentPixel(bmp, region);
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or IOException
                                      or UnauthorizedAccessException
                                      or global::System.Runtime.InteropServices.ExternalException)
        {
            // Undecodable or unreadable — REACTOR_DOC_IMAGE_001 already proved
            // the file exists, and misreporting a decode failure as "blank"
            // would send an author chasing the wrong problem. GDI+ surfaces
            // decode faults as ArgumentException *or* ExternalException
            // depending on the fault, so both are caught here; a compile must
            // not die on one bad byte in one image.
            return false;
        }
    }

    /// <summary>
    /// Reads only the leading signature bytes to confirm a file really is a PNG
    /// or JPEG, so a mislabelled or crafted <c>.png</c> is never handed to GDI+.
    /// </summary>
    private static bool HasRasterMagic(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var head = new byte[8];
            var read = fs.Read(head, 0, head.Length);
            return read == head.Length && ImageProcessor.HasKnownImageMagic(head);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Create a starter Mermaid flowchart file at
    /// <c>docs/_pipeline/diagrams/&lt;topic&gt;/&lt;id&gt;.mmd</c>. Returns
    /// the absolute path written.
    /// </summary>
    public static string ScaffoldDiagram(string diagramsRoot, string topic, string id)
    {
        var topicDir = Path.Combine(diagramsRoot, topic);
        Directory.CreateDirectory(topicDir);
        var path = Path.Combine(topicDir, id + ".mmd");
        if (File.Exists(path))
            throw new DocPipelineException(
                "REACTOR_DOC_DIAGRAM_002",
                $"diagram already exists: {path}");
        File.WriteAllText(path, StarterTemplate);
        return path;
    }

    private const string StarterTemplate = """
        %% Replace with your diagram. Author light + dark themes by keeping
        %% palette-neutral colors (GitHub renders SVG with its own theme).
        flowchart LR
            A[Start] --> B{Decision}
            B -- yes --> C[Do thing]
            B -- no  --> D[Other thing]
            C --> E[End]
            D --> E
        """;

    /// <summary>
    /// Matches a markdown image reference into the guide's image tree.
    /// </summary>
    /// <remarks>
    /// The <c>(\.\./)*</c> prefix is load-bearing. <c>DocAssembler</c> prepends
    /// one <c>../</c> per level of nesting for topic ids containing <c>/</c>
    /// (e.g. <c>recipes/login</c>), and validation runs on the <em>assembled</em>
    /// output, not the template. Anchoring on a bare <c>images/</c> therefore
    /// skipped every nested page — 10 of them today — so neither the broken-link
    /// check nor the blank-screenshot gate ever saw their images. A gate with a
    /// silent blind spot is worse than no gate, because its clean run reads as
    /// coverage.
    /// </remarks>
    private static readonly Regex ImagePattern =
        new(@"!\[[^\]]*\]\(((?:\.\./)*images/[^)]+)\)", RegexOptions.Compiled);

    private static bool FilesIdentical(string a, string b)
    {
        try
        {
            if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
            return HashFile(a) == HashFile(b);
        }
        catch
        {
            return false;
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(stream, hash);
        var sb = new StringBuilder(64);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}

// Repository-content gate for the spec 044 swallowed-error audit ledger
// (docs/specs/044/swallowed-error-audit.md). Resolves issue #959.
//
// The problem these lock in: the audit's verdict-distribution table used to be
// hand-maintained prose. Its counts drifted until they were unreproducible from
// the document — `Narrow` claimed 37 against 9 verdict-bearing rows while `Keep`
// claimed 8 against 16, i.e. the same table was wrong in both directions at once.
// Three mechanisms caused it: two incompatible per-file table schemas (so a
// fixed-column reading silently dropped a whole section), rows collapsing an
// undeclared number of sites, and verdicts recorded in the sections that had no
// row in the summary table at all.
//
// The fix makes the summary a *derived* artifact: per-file rows declare their
// site count in a single canonical schema, and the table is the sum. These tests
// are what keep it derived. Every assertion below fails if its target is deleted,
// renamed, or silently emptied — including the parser itself, which is pinned by
// a non-vacuity floor so a regex that stops matching fails instead of passing.
//
// Namespace note: in Microsoft.UI.Reactor.Tests, `Microsoft.UI.System` shadows
// `System`, so any `System.`-qualified path must be written `global::System.`.
// Bare type names (Path, File, Directory) come in via ImplicitUsings and are fine.

using Microsoft.UI.Reactor.Cli.Pack;
using Xunit;

using Regex = global::System.Text.RegularExpressions.Regex;
using Ordinal = global::System.StringComparison;

namespace Microsoft.UI.Reactor.Tests.Docs;

public sealed class SwallowedErrorAuditTests
{
    const string AuditPath = "docs/specs/044/swallowed-error-audit.md";

    const string LedgerBegin = "<!-- ledger:begin -->";
    const string LedgerEnd = "<!-- ledger:end -->";
    const string DistributionBegin = "<!-- distribution:begin -->";
    const string DistributionEnd = "<!-- distribution:end -->";

    const string LedgerHeader = "| Site(s) | Sites | Verdict | Status | Notes |";
    const string DistributionHeader = "| Verdict | Sites | Shipped | Deferred |";

    const string Shipped = "shipped";
    const string Deferred = "deferred";

    // Floor, not the exact list: the vocabulary lives in the document and may
    // grow (it already grew by `TryFinally` and `Trace`, which is what issue #959
    // exposed). These eight must remain present, so "close the vocabulary by
    // deleting tokens until nothing is left over" is not a way to pass.
    static readonly string[] RequiredVocabulary =
    [
        "Keep", "Narrow", "Propagate", "TryFinally",
        "TryXxx", "PromoteEvent", "Deleted", "Trace",
    ];

    static readonly string[] RequiredKeepJustifications =
    [
        "sibling-independence", "user-callback isolation",
        "fail-safe-to-default", "framework-internal",
    ];

    // ── Assertion 1: the summary table is the sum of the ledger rows ────────
    //
    // The headline guard. If any row's Sites/Verdict/Status changes and the
    // table isn't re-derived, this fails and prints the correct table verbatim
    // so fixing it is a paste.

    [Fact]
    public void Distribution_table_is_derived_from_the_ledger_rows()
    {
        var audit = Load();
        var derived = Derive(audit.Rows);
        var published = audit.Distribution;

        var mismatch = derived.Keys
            .Union(published.Keys)
            .Order(global::System.StringComparer.Ordinal)
            .Where(v => !derived.GetValueOrDefault(v).Equals(published.GetValueOrDefault(v)))
            .ToList();

        Assert.True(
            mismatch.Count == 0,
            $"""
             The verdict-distribution table in {AuditPath} does not match the sum of
             its ledger rows. Mismatched verdicts: {string.Join(", ", mismatch)}.

             Do NOT hand-edit a cell to make this pass — replace the whole table
             between the {DistributionBegin} / {DistributionEnd} markers with:

             {RenderTable(derived)}
             """);
    }

    // ── Assertion 2: the verdict vocabulary is closed, both directions ──────
    //
    // Direction A catches a row inventing a token. Direction B catches the
    // original #959 defect: sections recorded `try/finally` and pure-trace
    // dispositions that the summary table had no row for, so those sites were
    // uncountable by construction.

    [Fact]
    public void Verdict_vocabulary_is_closed_in_both_directions()
    {
        var audit = Load();

        foreach (var required in RequiredVocabulary)
        {
            Assert.True(
                audit.Vocabulary.Contains(required),
                $"The verdict-vocabulary table in {AuditPath} no longer defines `{required}`.");
        }

        foreach (var row in audit.Rows)
        {
            Assert.True(
                audit.Vocabulary.Contains(row.Verdict),
                $"{row.Where}: verdict `{row.Verdict}` is not in the verdict-vocabulary table. "
                + $"Known tokens: {string.Join(", ", audit.Vocabulary.Order(global::System.StringComparer.Ordinal))}.");
        }

        var derived = Derive(audit.Rows);

        foreach (var (verdict, tally) in derived)
        {
            Assert.True(
                audit.Distribution.ContainsKey(verdict),
                $"`{verdict}` has {tally.Sites} site(s) in the ledger but no row in the "
                + $"verdict-distribution table, so those sites are uncountable from the summary "
                + $"— the exact defect issue #959 reported. Add a row for it.");
        }

        foreach (var verdict in audit.Distribution.Keys)
        {
            Assert.True(
                audit.Vocabulary.Contains(verdict),
                $"The verdict-distribution table has a row for `{verdict}`, which is not a "
                + $"token in the verdict-vocabulary table.");
        }
    }

    // ── Assertion 3: one schema, forever ───────────────────────────────────
    //
    // `ReactorWindow.cs` used to carry `| Group | Sites | Verdict | After |`
    // while every other section used `| Site | Verdict | Notes |`. Any single
    // column rule silently dropped one of them — and it was the section that
    // collapsed the most sites.

    [Fact]
    public void Every_ledger_table_uses_the_canonical_schema()
    {
        var audit = Load();

        Assert.NotEmpty(audit.Tables);

        foreach (var table in audit.Tables)
        {
            Assert.True(
                Normalize(table.Header) == Normalize(LedgerHeader),
                $"{AuditPath}:{table.LineNumber}: table under '{table.Section}' has header "
                + $"'{table.Header}' but the ledger schema is '{LedgerHeader}'. A second schema "
                + $"is what made the old counts unreproducible — qualifiers go in Notes.");
        }
    }

    // ── Assertion 4: non-vacuity floor ─────────────────────────────────────
    //
    // Without this, breaking the parser turns every other assertion in this
    // file green (nothing parsed, so nothing disagrees). These floors are well
    // under the current content and encode the ledger's cumulative definition:
    // it never shrinks.

    [Fact]
    public void Parser_sees_a_substantial_ledger()
    {
        var audit = Load();

        Assert.True(audit.Sections.Count >= 15, $"Parsed only {audit.Sections.Count} ledger sections.");
        Assert.True(audit.Tables.Count >= 15, $"Parsed only {audit.Tables.Count} ledger tables.");
        Assert.True(audit.Rows.Count >= 35, $"Parsed only {audit.Rows.Count} ledger rows.");
        Assert.True(audit.Distribution.Count >= 6, $"Parsed only {audit.Distribution.Count} distribution rows.");
        Assert.True(audit.Vocabulary.Count >= 8, $"Parsed only {audit.Vocabulary.Count} vocabulary tokens.");
        Assert.True(audit.KeepJustifications.Count >= 4, $"Parsed only {audit.KeepJustifications.Count} Keep justifications.");

        var distinctVerdicts = audit.Rows.Select(r => r.Verdict).Distinct(global::System.StringComparer.Ordinal).Count();
        Assert.True(distinctVerdicts >= 4, $"Parsed only {distinctVerdicts} distinct verdicts across all rows.");

        // The ledger is cumulative — the total is monotonic by definition, so a
        // floor here is safe and catches wholesale deletion of a section.
        var total = audit.Rows.Sum(r => r.Sites);
        Assert.True(total >= 100, $"Ledger totals {total} sites; the cumulative ledger cannot shrink below its recorded history.");
    }

    // ── Assertion 5: Status is binary ──────────────────────────────────────

    [Fact]
    public void Every_status_cell_is_shipped_or_deferred()
    {
        foreach (var row in Load().Rows)
        {
            Assert.True(
                row.Status is Shipped or Deferred,
                $"{row.Where}: Status is '{row.Status}'; it must be '{Shipped}' or '{Deferred}'. "
                + $"A partial delivery is '{Deferred}', with the shipped part described in Notes.");
        }
    }

    // ── Assertion 6: Sites is a declared positive integer ──────────────────
    //
    // The collapse factor used to hide in prose ("(6 sites)", "×2 collapsed
    // into 1", "5 pure-trace"), so summing rows and summing sites gave
    // different answers with no way to tell which the table meant.

    [Fact]
    public void Every_sites_cell_is_a_positive_integer()
    {
        var audit = Load();

        foreach (var row in audit.Rows)
        {
            Assert.True(row.Sites > 0, $"{row.Where}: Sites is {row.Sites}; it must be a positive integer.");
        }

        // Rows whose Sites cell failed to parse never make it into Rows, so
        // assert none were dropped: every data line of every ledger table is a
        // parsed row.
        var dataLines = audit.Tables.Sum(t => t.DataLineCount);
        Assert.True(
            dataLines == audit.Rows.Count,
            $"{dataLines - audit.Rows.Count} ledger row(s) were skipped by the parser — "
            + $"most likely a Sites cell that is not a positive integer, or a row with the "
            + $"wrong number of cells. Every row must be countable.");
    }

    // ── Assertion 7: section headings name files that exist ────────────────
    //
    // Five of sixteen headings named nonexistent paths when this gate was
    // written (three Persistence files were under Hosting/ not Core/, one was
    // in Reactor.Advanced, and one file had been deleted from the repo
    // outright). `(retired` is the single, explicit escape hatch, so a silently
    // stale path still fails.

    [Fact]
    public void Every_section_heading_names_an_existing_file_or_is_marked_retired()
    {
        var root = RepoRoot();

        foreach (var section in Load().Sections)
        {
            if (section.Heading.Contains("(retired", Ordinal.Ordinal))
            {
                continue;
            }

            var full = Path.Combine(root, section.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(
                File.Exists(full),
                $"{AuditPath}:{section.LineNumber}: section names '{section.RelativePath}', which does not "
                + $"exist. Correct the path, or — if the file was deleted — add a '(retired: …)' marker to "
                + $"the heading so the entry stays counted under the cumulative-ledger definition.");
        }
    }

    // ── Assertion 8: every Keep row names which Keep justification applies ──
    //
    // `Keep` was defined three incompatible ways (the table label said
    // "iteration sibling-independence", the Method section said every Keep entry
    // shared one justification, and the second-pass prose added another). Four
    // are actually in use. Tagging each row is what makes the aggregate row
    // honest.

    [Fact]
    public void Every_keep_row_opens_its_notes_with_a_named_justification()
    {
        var audit = Load();

        foreach (var required in RequiredKeepJustifications)
        {
            Assert.True(
                audit.KeepJustifications.Contains(required),
                $"The `Keep` justifications table in {AuditPath} no longer defines '{required}'.");
        }

        var keepRows = audit.Rows.Where(r => r.Verdict == "Keep").ToList();
        Assert.NotEmpty(keepRows);

        foreach (var row in keepRows)
        {
            var tag = LeadingBoldTag(row.Notes);

            Assert.True(
                tag is not null && audit.KeepJustifications.Contains(tag),
                $"{row.Where}: a `Keep` row's Notes must open with one of the named justifications "
                + $"in bold ({string.Join(", ", audit.KeepJustifications.Order(global::System.StringComparer.Ordinal))}); "
                + $"found {(tag is null ? "no leading bold tag" : $"'{tag}'")}.");
        }
    }

    // ── Assertion 9: the live figure quoted in prose tracks the derivation ──
    //
    // Numbers restated in sentences are how the table drifted in the first
    // place. This is the only live figure quoted outside the table; historical
    // snapshots are quarantined under their own heading and deliberately not
    // checked.

    [Fact]
    public void Worry_threshold_sentence_quotes_the_derived_propagate_total()
    {
        var audit = Load();
        var derived = Derive(audit.Rows);

        var match = Regex.Match(
            audit.Text,
            @"worry-threshold for `Propagate` is (?<threshold>\d+); we're at (?<actual>\d+)\.");

        Assert.True(match.Success, $"The §6.7.4 worry-threshold sentence is missing from {AuditPath}.");

        var actual = int.Parse(match.Groups["actual"].Value);
        var threshold = int.Parse(match.Groups["threshold"].Value);
        var propagate = derived.GetValueOrDefault("Propagate").Sites;

        Assert.True(
            actual == propagate,
            $"The worry-threshold sentence says we're at {actual} `Propagate` sites, but the ledger "
            + $"derives {propagate}.");

        Assert.True(
            propagate < threshold,
            $"`Propagate` is at {propagate}, at or over the spec §6.7.4 worry threshold of {threshold}. "
            + $"That is a design signal, not a doc bug — re-read §6.7.4 before changing this test.");
    }

    // ── Assertion 10: the machine-readable markers are load-bearing ─────────
    //
    // Deleting a marker would zero the parse. Load() throws on a missing
    // marker, but assert it explicitly so the failure names the cause.

    [Fact]
    public void Region_markers_are_present()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), AuditPath.Replace('/', Path.DirectorySeparatorChar)));

        foreach (var marker in new[] { LedgerBegin, LedgerEnd, DistributionBegin, DistributionEnd })
        {
            Assert.True(
                text.Contains(marker, Ordinal.Ordinal),
                $"{AuditPath} is missing the '{marker}' marker. The markers delimit what the "
                + $"derivation counts; without them the summary table is unverifiable prose again.");
        }
    }

    // ── Parsing ────────────────────────────────────────────────────────────

    readonly record struct Tally(int Sites, int ShippedSites, int DeferredSites);

    sealed record Row(string Where, string Site, int Sites, string Verdict, string Status, string Notes);

    sealed record Section(string Heading, string RelativePath, int LineNumber);

    sealed record Table(string Section, string Header, int LineNumber, int DataLineCount);

    sealed record Audit(
        string Text,
        IReadOnlyList<Row> Rows,
        IReadOnlyList<Section> Sections,
        IReadOnlyList<Table> Tables,
        IReadOnlyDictionary<string, Tally> Distribution,
        IReadOnlySet<string> Vocabulary,
        IReadOnlySet<string> KeepJustifications);

    static string RepoRoot()
    {
        var root = RepoRootFinder.FindRepoRoot();
        Assert.True(root is not null, "Could not locate the repository root from the test assembly directory.");
        return root!;
    }

    static Audit Load()
    {
        var path = Path.Combine(RepoRoot(), AuditPath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Expected the audit file at '{path}'.");

        var text = File.ReadAllText(path);
        var lines = File.ReadAllLines(path);

        var (ledgerFrom, ledgerTo) = Region(lines, LedgerBegin, LedgerEnd);
        var (distFrom, distTo) = Region(lines, DistributionBegin, DistributionEnd);

        var sections = new List<Section>();
        var tables = new List<Table>();
        var rows = new List<Row>();
        var section = "(before any section)";

        for (var i = ledgerFrom; i < ledgerTo; i++)
        {
            var line = lines[i];

            if (line.StartsWith("### ", Ordinal.Ordinal))
            {
                var heading = line[4..].Trim();
                section = heading;
                sections.Add(new Section(heading, BacktickedPath(heading), i + 1));
                continue;
            }

            if (!IsTableHeader(lines, i))
            {
                continue;
            }

            var dataFrom = i + 2;
            var dataTo = dataFrom;
            while (dataTo < ledgerTo && lines[dataTo].TrimStart().StartsWith('|'))
            {
                dataTo++;
            }

            tables.Add(new Table(section, line.Trim(), i + 1, dataTo - dataFrom));

            for (var j = dataFrom; j < dataTo; j++)
            {
                if (TryParseRow(lines[j], section, j + 1, out var row))
                {
                    rows.Add(row);
                }
            }

            i = dataTo - 1;
        }

        return new Audit(
            text,
            rows,
            sections,
            tables,
            ParseDistribution(lines, distFrom, distTo),
            ParseFirstColumnTokens(lines, "## Verdict vocabulary"),
            ParseFirstColumnTokens(lines, "### `Keep` justifications"));
    }

    static (int From, int To) Region(string[] lines, string begin, string end)
    {
        var from = global::System.Array.FindIndex(lines, l => l.Trim() == begin);
        var to = global::System.Array.FindIndex(lines, l => l.Trim() == end);

        Assert.True(from >= 0, $"{AuditPath} is missing the '{begin}' marker.");
        Assert.True(to > from, $"{AuditPath} is missing the '{end}' marker, or it precedes '{begin}'.");

        return (from + 1, to);
    }

    // A markdown table header is a `|`-row immediately followed by a delimiter
    // row. Keying off structure rather than off the expected header text is what
    // lets assertion 3 *see* a non-canonical table instead of skipping it.
    static bool IsTableHeader(string[] lines, int i)
    {
        if (!lines[i].TrimStart().StartsWith('|') || i + 1 >= lines.Length)
        {
            return false;
        }

        var next = lines[i + 1].Trim();
        return next.StartsWith('|') && next.Contains("---", Ordinal.Ordinal) && Regex.IsMatch(next, @"^\|[\s:|-]+\|$");
    }

    static bool TryParseRow(string line, string section, int lineNumber, out Row row)
    {
        row = null!;

        var cells = Cells(line);
        if (cells is null || cells.Length != 5)
        {
            return false;
        }

        if (!int.TryParse(cells[1], out var sites) || sites <= 0)
        {
            return false;
        }

        row = new Row(
            $"{AuditPath}:{lineNumber} ({section})",
            cells[0],
            sites,
            cells[2].Trim('`'),
            cells[3],
            cells[4]);

        return true;
    }

    // Splits a table row into cells, folding any surplus back into the last one:
    // Notes legitimately contains `|` inside inline code, and losing those rows
    // would silently undercount.
    static string[]? Cells(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < 2 || !trimmed.StartsWith('|') || !trimmed.EndsWith('|'))
        {
            return null;
        }

        var parts = trimmed[1..^1].Split('|');
        if (parts.Length < 5)
        {
            return parts.Select(p => p.Trim()).ToArray();
        }

        return
        [
            parts[0].Trim(),
            parts[1].Trim(),
            parts[2].Trim(),
            parts[3].Trim(),
            string.Join('|', parts[4..]).Trim(),
        ];
    }

    static Dictionary<string, Tally> ParseDistribution(string[] lines, int from, int to)
    {
        var result = new Dictionary<string, Tally>(global::System.StringComparer.Ordinal);
        var sawHeader = false;

        for (var i = from; i < to; i++)
        {
            var cells = Cells(lines[i]);
            if (cells is null || cells.Length < 4)
            {
                continue;
            }

            if (Normalize(lines[i]) == Normalize(DistributionHeader))
            {
                sawHeader = true;
                continue;
            }

            if (!int.TryParse(cells[1], out var sites)
                || !int.TryParse(cells[2], out var shipped)
                || !int.TryParse(cells[3], out var deferred))
            {
                continue;
            }

            result[cells[0].Trim('`')] = new Tally(sites, shipped, deferred);
        }

        Assert.True(
            sawHeader,
            $"The verdict-distribution table's header must be exactly '{DistributionHeader}'.");

        return result;
    }

    static HashSet<string> ParseFirstColumnTokens(string[] lines, string heading)
    {
        var start = global::System.Array.FindIndex(lines, l => l.Trim() == heading);
        Assert.True(start >= 0, $"{AuditPath} is missing the '{heading}' heading.");

        var result = new HashSet<string>(global::System.StringComparer.Ordinal);

        for (var i = start + 1; i < lines.Length; i++)
        {
            // Stop at the next heading of the same or higher level.
            if (lines[i].StartsWith("## ", Ordinal.Ordinal)
                || (heading.StartsWith("###", Ordinal.Ordinal) && lines[i].StartsWith("### ", Ordinal.Ordinal)))
            {
                break;
            }

            if (!IsTableHeader(lines, i))
            {
                continue;
            }

            for (var j = i + 2; j < lines.Length && lines[j].TrimStart().StartsWith('|'); j++)
            {
                var cells = Cells(lines[j]);
                if (cells is { Length: >= 2 })
                {
                    result.Add(cells[0].Trim('`').Trim('*').Trim());
                }
            }

            break;
        }

        return result;
    }

    static string BacktickedPath(string heading)
    {
        var match = Regex.Match(heading, "`(?<path>[^`]+)`");
        return match.Success ? match.Groups["path"].Value : heading;
    }

    static string? LeadingBoldTag(string notes)
    {
        var match = Regex.Match(notes.Trim(), @"^\*\*(?<tag>[^*]+)\*\*");
        return match.Success ? match.Groups["tag"].Value.TrimEnd('.', ' ') : null;
    }

    static Dictionary<string, Tally> Derive(IReadOnlyList<Row> rows)
    {
        var result = new Dictionary<string, Tally>(global::System.StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var current = result.GetValueOrDefault(row.Verdict);
            result[row.Verdict] = new Tally(
                current.Sites + row.Sites,
                current.ShippedSites + (row.Status == Shipped ? row.Sites : 0),
                current.DeferredSites + (row.Status == Deferred ? row.Sites : 0));
        }

        return result;
    }

    // Ordered to match the vocabulary table's reading order so the printed fix
    // is a drop-in replacement, not a reshuffle.
    static string RenderTable(IReadOnlyDictionary<string, Tally> derived)
    {
        var ordered = RequiredVocabulary
            .Where(derived.ContainsKey)
            .Concat(derived.Keys.Where(k => !RequiredVocabulary.Contains(k)).Order(global::System.StringComparer.Ordinal));

        var body = ordered.Select(v =>
        {
            var t = derived[v];
            return $"| `{v}` | {t.Sites} | {t.ShippedSites} | {t.DeferredSites} |";
        });

        return string.Join(
            global::System.Environment.NewLine,
            [DistributionHeader, "|---|---|---|---|", .. body]);
    }

    static string Normalize(string line) =>
        Regex.Replace(line.Trim(), @"\s+", " ");
}

using System.Diagnostics.Tracing;
using Microsoft.UI.Reactor.Core.Diagnostics;
using Microsoft.UI.Reactor.Hosting.Persistence;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.Diagnostics;

/// <summary>
/// Spec 044 Phase C §4.7 — regression guard that the persistence layer
/// (<see cref="JsonFileStore"/>, <see cref="PackagedSettingsStore"/>,
/// <see cref="WindowPlacementCodec"/>) routes its swallowed exceptions
/// through <c>DiagnosticLog.SwallowedError(LogCategory.Persistence, ...)</c>
/// and its explicit rejection paths through the typed
/// <c>PersistenceRejected</c> event under <see cref="ReactorEventSource.Keywords.Persistence"/>.
///
/// PII discipline (§6.2.1): file paths are never on the ETW payload. The
/// <c>storeKind</c> field is a short developer-authored label
/// (<c>"json-file"</c>, <c>"packaged-settings"</c>, <c>"placement"</c>);
/// rejection <c>reason</c> labels are similarly bounded.
/// </summary>
public class PersistenceEtwBridgeTests : IDisposable
{
    private sealed class CapturingListener : EventListener
    {
        private readonly List<EventWrittenEventArgs> _events = new();

        public IReadOnlyList<EventWrittenEventArgs> Events
        {
            get { lock (_events) return _events.ToArray(); }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            lock (_events) _events.Add(eventData);
        }
    }

    private readonly CapturingListener _listener = new();
    private readonly string _path;

    public PersistenceEtwBridgeTests()
    {
        _listener.EnableEvents(
            ReactorEventSource.Log,
            EventLevel.Verbose,
            ReactorEventSource.Keywords.Persistence | ReactorEventSource.Keywords.Errors);
        _path = global::System.IO.Path.Combine(
            global::System.IO.Path.GetTempPath(),
            $"reactor-windows-persist-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        _listener.DisableEvents(ReactorEventSource.Log);
        _listener.Dispose();
        try { if (global::System.IO.File.Exists(_path)) global::System.IO.File.Delete(_path); } catch { }
    }

    /// <summary>
    /// Operation label used by
    /// <see cref="AssertEvent_discriminates_against_a_concurrent_foreign_event"/> to stand in for
    /// a concurrent subsystem's event. Namespaced so it can never collide with a real operation,
    /// in either direction.
    /// </summary>
    private const string ForeignOperation = "PersistenceEtwBridgeTests.ForeignEventProbe";

    private static EventWrittenEventArgs AssertEvent(
        IReadOnlyList<EventWrittenEventArgs> events,
        string name,
        int discriminatorIndex,
        string discriminator)
    {
        var candidates = events.Where(e => e.EventName == name).ToArray();
        var match = candidates.FirstOrDefault(e =>
            e.Payload is { } payload
            && discriminatorIndex >= 0
            && discriminatorIndex < payload.Count
            && payload[discriminatorIndex] is string value
            && value == discriminator);

        if (match is null)
        {
            var observedDiscriminators = candidates.Length == 0
                ? "<none>"
                : string.Join(", ", candidates.Select(e =>
                    e.Payload is { } payload
                    && discriminatorIndex >= 0
                    && discriminatorIndex < payload.Count
                        ? payload[discriminatorIndex]?.ToString() ?? "<null>"
                        : "<missing>"));
            Assert.Fail(
                $"Expected {name} with payload[{discriminatorIndex}] = '{discriminator}'. "
                + $"Same-name payload[{discriminatorIndex}] values: {observedDiscriminators}");
        }

        return match;
    }

    // ── JsonFileStore round-trip emits Read + Write ─────────────────────

    [Fact]
    public void JsonFileStore_Write_emits_PersistenceWrite_with_storeKind_no_path()
    {
        var store = new JsonFileStore(_path);

        store.Write("main", new byte[] { 1, 2, 3 });

        var evt = AssertEvent(
            _listener.Events,
            nameof(ReactorEventSource.PersistenceWrite),
            0,
            "json-file");
        Assert.True((int)(evt.Payload?[1] ?? 0) > 0);
        // PII: serialized payload size is on the event, but the file path
        // must not appear anywhere on the payload list.
        Assert.DoesNotContain(_listener.Events, e =>
            e.Payload?.Any(p => p is string s && s.Contains(_path, StringComparison.OrdinalIgnoreCase)) == true);
    }

    [Fact]
    public void JsonFileStore_TryRead_emits_PersistenceRead_on_hit()
    {
        var store = new JsonFileStore(_path);
        store.Write("main", new byte[] { 7, 8, 9 });

        Assert.True(store.TryRead("main", out var data));
        Assert.NotNull(data);

        AssertEvent(
            _listener.Events,
            nameof(ReactorEventSource.PersistenceRead),
            0,
            "json-file");
    }

    // ── JsonFileStore explicit rejects → PersistenceRejected ────────────

    [Fact]
    public void JsonFileStore_oversize_file_emits_PersistenceRejected_oversize_read()
    {
        var oversize = new byte[(int)(JsonFileStore.MaxFileSizeBytes + 64)];
        global::System.IO.File.WriteAllBytes(_path, oversize);

        var store = new JsonFileStore(_path);
        Assert.False(store.TryRead("main", out _));

        var evt = AssertEvent(
            _listener.Events,
            nameof(ReactorEventSource.PersistenceRejected),
            0,
            "json-file");
        Assert.Equal("oversize-read", evt.Payload?[1]);
    }

    // ── JsonFileStore malformed inputs → SwallowedError ─────────────────

    [Fact]
    public void JsonFileStore_malformed_json_emits_SwallowedError_JsonException()
    {
        global::System.IO.File.WriteAllText(_path, "this is not json{{{");
        var store = new JsonFileStore(_path);

        Assert.False(store.TryRead("main", out _));

        // Why this lookup is discriminated by operation rather than matched on event name:
        // SwallowedError is emitted by every subsystem under Keywords.Errors,
        // ReactorEventSource.Log is process-global, and CapturingListener therefore also
        // receives events raised by any test class running concurrently. This assertion used
        // to take the FIRST SwallowedError in the buffer and then assert its category, so a
        // foreign event won the race and it failed with "Intl != Persistence" — observed
        // ~1 full-suite run in 4, and 9/9 passing in isolation, which is why it was
        // mis-triaged for so long. It was the only one of this class's three SwallowedError
        // lookups missing the discriminator; AssertEvent now makes that omission
        // unrepresentable rather than something each author has to remember.
        var evt = AssertEvent(
            _listener.Events,
            nameof(ReactorEventSource.SwallowedError),
            1,
            "JsonFileStore.TryRead.parse");
        Assert.Equal(nameof(LogCategory.Persistence), evt.Payload?[0]);
        // The payload carries ex.GetType().Name — the concrete runtime
        // type, which for malformed JSON is the JsonException-derived
        // JsonReaderException. Assert by IsAssignableFrom-style prefix
        // so we don't pin to a private internal name.
        var thrown = evt.Payload?[2] as string;
        Assert.NotNull(thrown);
        Assert.StartsWith("Json", thrown);
        Assert.EndsWith("Exception", thrown);
        // PII: malformed body must not appear in the payload.
        Assert.DoesNotContain("not json", string.Join("|", evt.Payload?.OfType<string>() ?? Array.Empty<string>()));
    }

    // ── The discriminator itself is load-bearing; pin it ────────────────

    [Fact]
    public void AssertEvent_discriminates_against_a_concurrent_foreign_event()
    {
        // Every other test in this class passes whether or not AssertEvent actually filters on
        // the discriminator, because in an uncontended run the first same-name event IS the
        // right one. Measured: with the filter removed, 9 of the 10 tests here still passed.
        // That is why the fix needs its own guard — the flake it prevents is invisible to the
        // coverage that already exists.
        //
        // ReactorEventSource.Log is process-global, so this listener also receives SwallowedError
        // events raised by any test class running concurrently. Emitting one here turns that
        // interleaving from a ~1-in-4 race into a certainty of write order on a single thread,
        // so the guard is provable in milliseconds without sleeps or retries. LogCategory.Intl
        // reproduces the exact reported symptom ("Expected: Persistence, Actual: Intl").
        //
        // Safe to emit globally: it goes out under Keywords.Errors, which this class has held
        // open since its constructor either way, so no concurrent listener's keyword mask
        // changes. ReactorTraceRegressionTests' allocation probe subscribes to Keywords.Reconcile
        // only and already early-returns when Errors is enabled elsewhere; IntlEtwBridgeTests
        // matches on per-test discriminators that ForeignOperation cannot collide with.
        DiagnosticLog.SwallowedError(
            LogCategory.Intl, ForeignOperation, new InvalidOperationException());

        global::System.IO.File.WriteAllText(_path, "this is not json{{{");
        var store = new JsonFileStore(_path);
        Assert.False(store.TryRead("main", out _));

        // Setup oracle: prove the foreign event actually landed, WITHOUT going through the
        // helper under test — otherwise a broken AssertEvent could make this look fine.
        Assert.Contains(_listener.Events, e =>
            e.EventName == nameof(ReactorEventSource.SwallowedError)
            && (e.Payload?[0] as string) == nameof(LogCategory.Intl)
            && (e.Payload?[1] as string) == ForeignOperation);

        // Guard the guard: the FIRST same-name event must not be the one we are looking for, or
        // an undiscriminated lookup would return the right answer by luck and this test would
        // prove nothing. Asserting "not the target" rather than "is our Intl event" keeps it
        // deterministic even if a third class's event arrives first — the injected event always
        // precedes the parse event in this thread's write order, so the target can never be first.
        var firstByNameOnly = _listener.Events.First(e =>
            e.EventName == nameof(ReactorEventSource.SwallowedError));
        Assert.NotEqual("JsonFileStore.TryRead.parse", firstByNameOnly.Payload?[1] as string);

        // The discriminated lookup must skip past it. Drop the payload[discriminatorIndex]
        // clause from AssertEvent and this returns the Intl event, failing on the category
        // assertion exactly as the original flake did.
        var evt = AssertEvent(
            _listener.Events,
            nameof(ReactorEventSource.SwallowedError),
            1,
            "JsonFileStore.TryRead.parse");
        Assert.Equal(nameof(LogCategory.Persistence), evt.Payload?[0]);
        Assert.Equal("JsonFileStore.TryRead.parse", evt.Payload?[1]);
    }

    [Fact]
    public void JsonFileStore_malformed_base64_emits_SwallowedError_FormatException()
    {
        global::System.IO.File.WriteAllText(_path, "{\"main\":\"not_valid_base64!@#\"}");
        var store = new JsonFileStore(_path);

        Assert.False(store.TryRead("main", out _));

        var evt = AssertEvent(
            _listener.Events,
            nameof(ReactorEventSource.SwallowedError),
            1,
            "JsonFileStore.TryRead.base64");
        Assert.Equal(nameof(LogCategory.Persistence), evt.Payload?[0]);
        Assert.Equal(nameof(FormatException), evt.Payload?[2]);
    }

    // ── PackagedSettingsStore (unpackaged context throws WinRT) ─────────

    [Fact]
    public void PackagedSettingsStore_TryRead_in_unpackaged_emits_SwallowedError()
    {
        // xUnit host has no package identity → ApplicationData.Current
        // throws InvalidOperationException (0x80073D54). The narrow catch
        // must route through DiagnosticLog and never propagate.
        var store = new PackagedSettingsStore();

        var result = store.TryRead("anything", out _);

        Assert.False(result);
        var evt = AssertEvent(
            _listener.Events,
            nameof(ReactorEventSource.SwallowedError),
            1,
            "PackagedSettingsStore.TryRead");
        Assert.Equal(nameof(LogCategory.Persistence), evt.Payload?[0]);
    }

    [Fact]
    public void PackagedSettingsStore_Write_in_unpackaged_emits_SwallowedError()
    {
        var store = new PackagedSettingsStore();

        store.Write("anything", new byte[] { 1, 2, 3 });

        var evt = AssertEvent(
            _listener.Events,
            nameof(ReactorEventSource.SwallowedError),
            1,
            "PackagedSettingsStore.Write");
        Assert.Equal(nameof(LogCategory.Persistence), evt.Payload?[0]);
    }

    // ── WindowPlacementCodec rejects ────────────────────────────────────

    [Fact]
    public void WindowPlacementCodec_implausible_monitor_count_emits_PersistenceRejected()
    {
        // Hand-craft a payload that decodes to a too-large monitor count
        // (encoded as int32 = 999, which exceeds the 64-monitor cap).
        using var ms = new global::System.IO.MemoryStream();
        using var bw = new global::System.IO.BinaryWriter(ms);
        bw.Write(999);
        bw.Flush();

        var monitors = new[] { new MonitorRect(null, 0, 0, 1920, 1080) };
        Assert.False(WindowPlacementCodec.Restore(hwnd: 0, ms.ToArray(), monitors));

        var evt = AssertEvent(
            _listener.Events,
            nameof(ReactorEventSource.PersistenceRejected),
            1,
            "implausible-monitor-count");
        Assert.Equal("placement", evt.Payload?[0]);
    }

    [Fact]
    public void WindowPlacementCodec_truncated_payload_emits_PersistenceRejected_truncated()
    {
        // Payload claims 1 monitor but contains nothing past the count.
        using var ms = new global::System.IO.MemoryStream();
        using var bw = new global::System.IO.BinaryWriter(ms);
        bw.Write(1);
        bw.Flush();

        var monitors = new[] { new MonitorRect(null, 0, 0, 1920, 1080) };
        Assert.False(WindowPlacementCodec.Restore(hwnd: 0, ms.ToArray(), monitors));

        var evt = AssertEvent(
            _listener.Events,
            nameof(ReactorEventSource.PersistenceRejected),
            1,
            "truncated");
        Assert.Equal("placement", evt.Payload?[0]);
    }
}

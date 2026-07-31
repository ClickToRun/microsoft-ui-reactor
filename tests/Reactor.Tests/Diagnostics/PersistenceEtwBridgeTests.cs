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

    private static EventWrittenEventArgs? FindByName(IReadOnlyList<EventWrittenEventArgs> events, string name)
        => events.FirstOrDefault(e => e.EventName == name);

    /// <summary>
    /// Operation label used by <see cref="FindSwallowedError_discriminates_against_a_concurrent_foreign_event"/>
    /// to stand in for a concurrent subsystem's event. Namespaced so it can never
    /// collide with a real operation, in either direction.
    /// </summary>
    private const string ForeignOperation = "PersistenceEtwBridgeTests.ForeignEventProbe";

    /// <summary>
    /// Finds a <c>SwallowedError</c> by its <c>operation</c> payload field (index 1) —
    /// never by event name alone.
    ///
    /// Matching on the name alone is not safe: <c>SwallowedError</c> is emitted by every
    /// subsystem under <c>Keywords.Errors</c>, <c>ReactorEventSource.Log</c> is
    /// process-global, and <see cref="CapturingListener"/> therefore also receives events
    /// raised by any test class running concurrently. The malformed-JSON assertion below
    /// used to take the FIRST <c>SwallowedError</c> in the buffer and then assert its
    /// category, so a foreign event could win the race and fail it with
    /// "Intl != Persistence" — observed ~1 full-suite run in 4, 9/9 in isolation. It was
    /// the only one of this class's <c>SwallowedError</c> lookups missing the discriminator.
    ///
    /// Routing every lookup through here leaves no name-only variant to regress to, and
    /// <see cref="FindSwallowedError_discriminates_against_a_concurrent_foreign_event"/>
    /// pins it: drop the <paramref name="operation"/> filter and that test fails
    /// deterministically rather than the flake silently returning.
    /// </summary>
    private static EventWrittenEventArgs? FindSwallowedError(
        IReadOnlyList<EventWrittenEventArgs> events, string operation)
        => events.FirstOrDefault(e =>
            e.EventName == nameof(ReactorEventSource.SwallowedError)
            && (e.Payload?[1] as string) == operation);

    // ── JsonFileStore round-trip emits Read + Write ─────────────────────

    [Fact]
    public void JsonFileStore_Write_emits_PersistenceWrite_with_storeKind_no_path()
    {
        var store = new JsonFileStore(_path);

        store.Write("main", new byte[] { 1, 2, 3 });

        var evt = FindByName(_listener.Events, nameof(ReactorEventSource.PersistenceWrite));
        Assert.NotNull(evt);
        Assert.Equal("json-file", evt!.Payload?[0]);
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

        var evt = FindByName(_listener.Events, nameof(ReactorEventSource.PersistenceRead));
        Assert.NotNull(evt);
        Assert.Equal("json-file", evt!.Payload?[0]);
    }

    // ── JsonFileStore explicit rejects → PersistenceRejected ────────────

    [Fact]
    public void JsonFileStore_oversize_file_emits_PersistenceRejected_oversize_read()
    {
        var oversize = new byte[(int)(JsonFileStore.MaxFileSizeBytes + 64)];
        global::System.IO.File.WriteAllBytes(_path, oversize);

        var store = new JsonFileStore(_path);
        Assert.False(store.TryRead("main", out _));

        var evt = FindByName(_listener.Events, nameof(ReactorEventSource.PersistenceRejected));
        Assert.NotNull(evt);
        Assert.Equal("json-file", evt!.Payload?[0]);
        Assert.Equal("oversize-read", evt.Payload?[1]);
    }

    // ── JsonFileStore malformed inputs → SwallowedError ─────────────────

    [Fact]
    public void JsonFileStore_malformed_json_emits_SwallowedError_JsonException()
    {
        global::System.IO.File.WriteAllText(_path, "this is not json{{{");
        var store = new JsonFileStore(_path);

        Assert.False(store.TryRead("main", out _));

        // Disambiguate by operation via FindSwallowedError — see its remarks for why
        // matching on the event name alone was a 1-in-4 flake, and for the test that
        // now pins the discriminator.
        var evt = FindSwallowedError(_listener.Events, "JsonFileStore.TryRead.parse");
        Assert.NotNull(evt);
        Assert.Equal(nameof(LogCategory.Persistence), evt!.Payload?[0]);
        Assert.Equal("JsonFileStore.TryRead.parse", evt.Payload?[1]);
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

    [Fact]
    public void JsonFileStore_malformed_base64_emits_SwallowedError_FormatException()
    {
        global::System.IO.File.WriteAllText(_path, "{\"main\":\"not_valid_base64!@#\"}");
        var store = new JsonFileStore(_path);

        Assert.False(store.TryRead("main", out _));

        var evt = FindSwallowedError(_listener.Events, "JsonFileStore.TryRead.base64");
        Assert.NotNull(evt);
        Assert.Equal(nameof(LogCategory.Persistence), evt!.Payload?[0]);
        Assert.Equal(nameof(FormatException), evt.Payload?[2]);
    }

    // ── Regression guard for the lookup itself ──────────────────────────

    [Fact]
    public void FindSwallowedError_discriminates_against_a_concurrent_foreign_event()
    {
        // Makes the 1-in-4 full-suite flake deterministic instead of hoping the race
        // fires. ReactorEventSource.Log is process-global, so this listener also sees
        // SwallowedError events raised by other test classes; emitting one here turns
        // that interleaving from a race into a certainty. LogCategory.Intl reproduces
        // the exact reported symptom ("Expected: Persistence, Actual: Intl").
        //
        // Safe to emit globally: it goes out under Keywords.Errors, which this class has
        // held open since its constructor either way, so no concurrent listener's keyword
        // mask changes. ReactorTraceRegressionTests' allocation probe subscribes to
        // Keywords.Reconcile only and already skips when Errors is enabled elsewhere;
        // IntlEtwBridgeTests matches on per-test discriminators that ForeignOperation
        // cannot collide with.
        DiagnosticLog.SwallowedError(
            LogCategory.Intl, ForeignOperation, new InvalidOperationException());

        global::System.IO.File.WriteAllText(_path, "this is not json{{{");
        var store = new JsonFileStore(_path);
        Assert.False(store.TryRead("main", out _));

        // Setup oracle: prove the foreign event actually landed, without going through
        // the helper under test — otherwise a broken helper could make this look fine.
        Assert.Contains(_listener.Events, e =>
            e.EventName == nameof(ReactorEventSource.SwallowedError)
            && (e.Payload?[0] as string) == nameof(LogCategory.Intl)
            && (e.Payload?[1] as string) == ForeignOperation);

        // Guard the guard: the first SwallowedError in the buffer must NOT be the one
        // we are looking for, or a name-only lookup would succeed by luck and this test
        // would prove nothing. Asserting "not the target" rather than "is our Intl event"
        // keeps it deterministic even if a third class's event arrives first — the
        // injected event always precedes the parse event in this thread's write order,
        // so the target can never be first.
        var nameOnly = _listener.Events.First(e =>
            e.EventName == nameof(ReactorEventSource.SwallowedError));
        Assert.NotEqual("JsonFileStore.TryRead.parse", nameOnly.Payload?[1] as string);

        // The discriminated lookup must skip past it. Remove the operation filter from
        // FindSwallowedError and this returns the Intl event, failing on the category
        // assertion exactly as the original flake did.
        var evt = FindSwallowedError(_listener.Events, "JsonFileStore.TryRead.parse");
        Assert.NotNull(evt);
        Assert.Equal(nameof(LogCategory.Persistence), evt!.Payload?[0]);
        Assert.Equal("JsonFileStore.TryRead.parse", evt.Payload?[1]);
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
        var evt = FindSwallowedError(_listener.Events, "PackagedSettingsStore.TryRead");
        Assert.NotNull(evt);
        Assert.Equal(nameof(LogCategory.Persistence), evt!.Payload?[0]);
    }

    [Fact]
    public void PackagedSettingsStore_Write_in_unpackaged_emits_SwallowedError()
    {
        var store = new PackagedSettingsStore();

        store.Write("anything", new byte[] { 1, 2, 3 });

        var evt = FindSwallowedError(_listener.Events, "PackagedSettingsStore.Write");
        Assert.NotNull(evt);
        Assert.Equal(nameof(LogCategory.Persistence), evt!.Payload?[0]);
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

        var evt = _listener.Events.FirstOrDefault(e =>
            e.EventName == nameof(ReactorEventSource.PersistenceRejected)
            && (e.Payload?[1] as string) == "implausible-monitor-count");
        Assert.NotNull(evt);
        Assert.Equal("placement", evt!.Payload?[0]);
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

        var evt = _listener.Events.FirstOrDefault(e =>
            e.EventName == nameof(ReactorEventSource.PersistenceRejected)
            && (e.Payload?[1] as string) == "truncated");
        Assert.NotNull(evt);
        Assert.Equal("placement", evt!.Payload?[0]);
    }
}

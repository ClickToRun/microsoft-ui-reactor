using Microsoft.UI.Reactor.Hosting.Persistence;
using Microsoft.UI.Reactor.Hosting.Shell;
using Xunit;

namespace Microsoft.UI.Reactor.Tests;

/// <summary>
/// Spec 036 §8 — <see cref="PackagedSettingsStore"/> bound-check and
/// error-handling pins. The store routes through
/// <c>Windows.Storage.ApplicationData.Current</c> which requires WinRT
/// package identity; tests run in an unpackaged xUnit context, so each
/// API call exercises the catch arms. The "warn-and-default" contract
/// (spec §8) must keep the host healthy when persistence fails — these
/// tests pin that the catch-all paths return false / swallow without
/// bubbling exceptions to the caller.
/// </summary>
[Collection("PackageIdentityProbe")]
public class PackagedSettingsStoreTests
{
    // ══════════════════════════════════════════════════════════════
    //  Early-return guards — null/empty inputs.
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void TryRead_Empty_Id_Returns_False_Without_Touching_WinRT()
    {
        // The `string.IsNullOrEmpty(id)` guard short-circuits before any
        // WinRT call. A regression that dropped this guard would attempt
        // `Containers.TryGetValue` with the prefix-only key and crash
        // when running unpackaged with a "no package identity" error.
        var store = new PackagedSettingsStore();
        Assert.False(store.TryRead("", out var data));
        Assert.Null(data);
    }

    [Fact]
    public void Write_Empty_Id_Is_Silent_NoOp()
    {
        // Same guard on the write side.
        var store = new PackagedSettingsStore();
        // Must not throw.
        store.Write("", new byte[] { 1, 2, 3 });
    }

    [Fact]
    public void Write_Null_Data_Is_Silent_NoOp()
    {
        var store = new PackagedSettingsStore();
        store.Write("main", null!);
    }

    // ══════════════════════════════════════════════════════════════
    //  WinRT failure handling — running unpackaged means every
    //  ApplicationData.Current call throws. The catch arms must
    //  swallow + return false / void.
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void TryRead_In_Unpackaged_Context_Returns_False_Not_Throws()
    {
        // Bug shape: a regression that didn't wrap the WinRT call in
        // try/catch would propagate the COMException up through
        // ReactorWindow.LoadPlacement → ReactorApp.OnLaunched, crashing
        // every unpackaged host at startup.
        var store = new PackagedSettingsStore();
        // Must not throw.
        var result = store.TryRead("some-id", out var data);
        Assert.False(result);
        Assert.Null(data);
    }

    [Fact]
    public void Write_In_Unpackaged_Context_Is_Silent_NoOp()
    {
        // The Write side's catch arm. Spec §8: "Write failures are
        // swallowed with a stderr diagnostic." Pin: the call must not
        // throw even when ApplicationData.Current itself throws.
        var store = new PackagedSettingsStore();
        // Must not throw.
        store.Write("some-id", new byte[] { 1, 2, 3 });
    }

    // ══════════════════════════════════════════════════════════════
    //  IsAvailable — detect WinRT package identity without throwing.
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void IsAvailable_In_Unpackaged_Test_Context_Returns_False()
    {
        // The xUnit test host runs unpackaged — no package identity.
        // IsAvailable() must return false (and not propagate the
        // InvalidOperationException 0x80073D54 the WinRT call throws).
        // Auto-detection logic in spec §8 uses this to pick JsonFileStore
        // over PackagedSettingsStore at app bring-up; a regression that
        // throw'd here would crash the bring-up before the store choice.
        Assert.False(PackagedSettingsStore.IsAvailable());
    }

    [Fact]
    public void IsAvailable_DetectsIdentity_WithoutRaisingAFirstChanceException()
    {
        // IsAvailable() used to answer the identity question by touching
        // Windows.ApplicationModel.Package.Current inside a try/catch, so every
        // unpackaged bring-up (ReactorApp picks the store through this call) raised a
        // first-chance InvalidOperationException 0x80073D54 into the debugger and into
        // any crash telemetry the host wires up. It now shares PackageRuntime's
        // non-throwing Win32 probe. Reset the shared cache first so the probe genuinely
        // runs inside the capture window.
        PackageRuntime.ResetForTests();

        bool available = true;
        var observed = FirstChanceExceptionProbe.Capture(
            () => available = PackagedSettingsStore.IsAvailable());

        Assert.False(available);
        Assert.True(
            observed.Count == 0,
            "PackagedSettingsStore.IsAvailable() must detect package identity without throwing. Observed: "
                + FirstChanceExceptionProbe.Describe(observed));
    }

    [Fact]
    public void IsAvailable_TracksPackageRuntime_InBothDirections()
    {
        // Pins the delegation itself. The unpackaged test host can only ever produce
        // `false` from a live probe, so force the packaged verdict: an IsAvailable() that
        // hard-coded a result, or that went back to probing WinRT on its own, would not
        // follow PackageRuntime here.
        using (PackageIdentityCache.Force(packaged: true))
        {
            Assert.True(PackagedSettingsStore.IsAvailable());

            PackageRuntime.ResetForTests();
            Assert.False(PackagedSettingsStore.IsAvailable());
        }
    }
}

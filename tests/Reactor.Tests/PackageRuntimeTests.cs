using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Reactor.Hosting.Shell;
using Xunit;

namespace Microsoft.UI.Reactor.Tests;

/// <summary>
/// Captures the first-chance exceptions a delegate raises on the calling thread.
/// <see cref="AppDomain.FirstChanceException"/> fires for every exception the CLR
/// dispatches, whether or not it is caught — which is exactly what a debugger, an
/// exception log, or crash telemetry sees. Observations are filtered to the calling
/// thread so unrelated work elsewhere in the process can never be miscounted.
/// </summary>
internal static class FirstChanceExceptionProbe
{
    public static IReadOnlyList<Exception> Capture(Action action)
    {
        int probeThreadId = Environment.CurrentManagedThreadId;
        var observed = new List<Exception>();

        void OnFirstChance(object? sender, FirstChanceExceptionEventArgs e)
        {
            if (Environment.CurrentManagedThreadId == probeThreadId)
                observed.Add(e.Exception);
        }

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        try
        {
            action();
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
        }

        return observed;
    }

    /// <summary>Renders captured exceptions for an assertion failure message.</summary>
    public static string Describe(IReadOnlyList<Exception> observed)
        => string.Join("; ", observed.Select(e => $"{e.GetType().FullName}: {e.Message}"));
}

/// <summary>
/// Overwrites <see cref="PackageRuntime"/>'s cached identity verdict so tests can exercise
/// the packaged arm from an unpackaged host, where the live probe can only ever say
/// "unpackaged". Disposing restores the normal (recompute-on-next-read) state.
/// </summary>
internal static class PackageIdentityCache
{
    public static IDisposable Force(bool packaged)
    {
        var valueField = typeof(PackageRuntime).GetField(
            "_isPackaged", BindingFlags.NonPublic | BindingFlags.Static);
        var computedField = typeof(PackageRuntime).GetField(
            "_isPackagedComputed", BindingFlags.NonPublic | BindingFlags.Static);
        // Fail loudly if the cache fields are renamed rather than silently passing.
        Assert.NotNull(valueField);
        Assert.NotNull(computedField);

        valueField!.SetValue(null, packaged ? 1 : 0);
        computedField!.SetValue(null, 1);
        return new Restore();
    }

    private sealed class Restore : IDisposable
    {
        public void Dispose() => PackageRuntime.ResetForTests();
    }
}

/// <summary>
/// Spec 036 §11.3 — <see cref="PackageRuntime"/> answers "is this process running
/// under an MSIX package identity?".
/// </summary>
/// <remarks>
/// The detection used to probe <c>Windows.ApplicationModel.Package.Current</c> inside a
/// try/catch. That property signals "unpackaged" by <em>throwing</em>
/// <c>InvalidOperationException</c> (HRESULT 0x80073D54, "the process has no package
/// identity"), so every unpackaged launch raised a first-chance exception into the
/// debugger, the exception log, and any crash telemetry the host wires up — drowning the
/// real first-chance exceptions those tools exist to surface. Detection now goes through
/// the non-throwing Win32 <c>GetCurrentPackageFullName</c> status code instead.
/// </remarks>
[Collection("PackageIdentityProbe")]
public partial class PackageRuntimeTests
{
    // Declared independently of the product constants on purpose: a test that reused
    // PackageRuntime's own values could not catch a wrong constant.
    // winerror.h / appmodel.h.
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const int AppmodelErrorNoPackage = 15700;

    // Deliberately a separate declaration from PackageRuntime's own probe: an independent
    // binding to the same export, so a probe that stopped calling the OS could not pass.
    // Source-generated [LibraryImport] rather than DllImport keeps the marshalling
    // compile-time and AOT-honest (this project sets IsAotCompatible).
    [LibraryImport("kernel32.dll")]
    private static partial int GetCurrentPackageFullName(ref uint packageFullNameLength, nint packageFullName);

    // ══════════════════════════════════════════════════════════════
    //  The regression pin: detection must not throw.
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void IsPackaged_ComputesWithoutRaisingAFirstChanceException()
    {
        PackageRuntime.ResetForTests();

        bool packaged = false;
        var observed = FirstChanceExceptionProbe.Capture(() => packaged = PackageRuntime.IsPackaged);

        // The xUnit host is unpackaged, so this is the arm that used to throw. Assert
        // the verdict too, so the "no exception" claim is about a probe that actually ran.
        Assert.False(packaged);
        Assert.True(
            observed.Count == 0,
            "PackageRuntime.IsPackaged must determine package identity without throwing. Observed: "
                + FirstChanceExceptionProbe.Describe(observed));
    }

    // ══════════════════════════════════════════════════════════════
    //  The status-code mapping.
    // ══════════════════════════════════════════════════════════════

    [Theory]
    // Identity exists and the full name was written / would not fit in a zero-length buffer.
    [InlineData(ErrorSuccess, true)]
    [InlineData(ErrorInsufficientBuffer, true)]
    // The documented "this process has no package identity" status.
    [InlineData(AppmodelErrorNoPackage, false)]
    // Anything undocumented must stay on the conservative unpackaged path, matching the
    // fallback the old catch-all produced — reporting "packaged" would send callers down
    // WinRT paths (JumpList, SecondaryTile, ApplicationData) that then throw for real.
    [InlineData(unchecked((int)0x80070005), false)]
    [InlineData(int.MaxValue, false)]
    public void IsPackagedIdentityCode_MapsTheDocumentedStatusCodes(int code, bool expected)
        => Assert.Equal(expected, PackageRuntime.IsPackagedIdentityCode(code));

    [Fact]
    public void QueryPackageIdentityCode_ReturnsTheSameStatusAsKernel32()
    {
        // Independent call to the same Win32 API. Pins that the probe really asks the OS
        // (rather than hard-coding an answer) and that it passes the zero-length/NULL
        // buffer that makes the call a pure identity question.
        uint length = 0;
        int expected = GetCurrentPackageFullName(ref length, nint.Zero);

        Assert.Equal(expected, PackageRuntime.QueryPackageIdentityCode());
        Assert.True(
            expected is ErrorSuccess or ErrorInsufficientBuffer or AppmodelErrorNoPackage,
            $"Unexpected GetCurrentPackageFullName status {expected}.");
    }

    [Fact]
    public void IsPackaged_AgreesWithTheLiveProbe()
    {
        PackageRuntime.ResetForTests();

        // Ties the public verdict to OS truth end to end: a probe that stopped calling
        // kernel32, or a mapping that inverted a status, breaks this on some host.
        bool expected = PackageRuntime.IsPackagedIdentityCode(PackageRuntime.QueryPackageIdentityCode());
        Assert.Equal(expected, PackageRuntime.IsPackaged);
    }

    // ══════════════════════════════════════════════════════════════
    //  Caching + ResetForTests.
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void IsPackaged_CachesTheVerdict_AndResetForTestsRecomputesIt()
    {
        // Poison the cache with the verdict the live probe never gives on this
        // (unpackaged) host. Reading `true` back proves the getter served the cached
        // value instead of re-probing.
        using (PackageIdentityCache.Force(packaged: true))
        {
            Assert.True(PackageRuntime.IsPackaged);

            // ...and ResetForTests must drop it so the next read probes again.
            PackageRuntime.ResetForTests();
            Assert.False(PackageRuntime.IsPackaged);
        }
    }
}

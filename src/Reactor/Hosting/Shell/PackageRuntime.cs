using System.Runtime.InteropServices;

namespace Microsoft.UI.Reactor.Hosting.Shell;

/// <summary>
/// Runtime detection of MSIX vs. unpackaged execution. WinRT classes that
/// implicitly query the package context (<c>JumpList</c>, <c>SecondaryTile</c>)
/// throw on unpackaged apps; this helper lets the caller pick the unpackaged
/// fallback path before the WinRT call. (spec 036 §11.3, §0.5 — no
/// <c>#if PACKAGED</c> branching anywhere.)
/// </summary>
/// <remarks>
/// Detection goes through the Win32 <c>GetCurrentPackageFullName</c> probe rather
/// than <c>Windows.ApplicationModel.Package.Current</c>: the WinRT property answers
/// "unpackaged" by <em>throwing</em> <see cref="InvalidOperationException"/>
/// (HRESULT 0x80073D54, "the process has no package identity"), so every unpackaged
/// launch used to raise a first-chance exception that polluted the debugger, the
/// exception log, and any crash telemetry the host wires up — masking the real
/// first-chance exceptions those tools exist to surface.
/// </remarks>
internal static partial class PackageRuntime
{
    // winerror.h / appmodel.h return codes for the identity probe below.
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const int AppmodelErrorNoPackage = 15700;

    private static int _isPackagedComputed;
    private static int _isPackaged;

    /// <summary>True iff this process runs under an MSIX package identity.</summary>
    public static bool IsPackaged
    {
        get
        {
            if (Volatile.Read(ref _isPackagedComputed) != 0)
                return Volatile.Read(ref _isPackaged) != 0;

            bool packaged = IsPackagedIdentityCode(QueryPackageIdentityCode());
            Volatile.Write(ref _isPackaged, packaged ? 1 : 0);
            Volatile.Write(ref _isPackagedComputed, 1);
            return packaged;
        }
    }

    /// <summary>
    /// Non-throwing package-identity probe. <c>packageFullName</c> is documented
    /// <c>[out, optional]</c>, so passing <c>NULL</c> with a zero length asks only the
    /// identity question — the name itself is never needed here. The documented status
    /// codes are <c>ERROR_SUCCESS</c>, <c>APPMODEL_ERROR_NO_PACKAGE</c> ("the process has
    /// no package identity") and <c>ERROR_INSUFFICIENT_BUFFER</c> ("the buffer is not
    /// large enough to hold the data"), the last being what a process that *does* have
    /// identity reports for a zero-length buffer. <c>ERROR_INVALID_PARAMETER</c> is not a
    /// documented outcome for this call shape.
    /// </summary>
    /// <returns>
    /// The raw <c>GetCurrentPackageFullName</c> status code; feed it to
    /// <see cref="IsPackagedIdentityCode"/> to get the verdict.
    /// </returns>
    internal static int QueryPackageIdentityCode()
    {
        uint length = 0;
        // No try/catch: GetCurrentPackageFullName has been exported from
        // kernel32 since Windows 8 and this assembly's TargetPlatformMinVersion
        // is 10.0.17763.0, so the entry point always resolves.
        return GetCurrentPackageFullName(ref length, nint.Zero);
    }

    /// <summary>
    /// Maps a <see cref="QueryPackageIdentityCode"/> status onto the packaged
    /// verdict. A process with identity reports <c>ERROR_INSUFFICIENT_BUFFER</c>
    /// (the full name doesn't fit in zero characters); one without reports
    /// <c>APPMODEL_ERROR_NO_PACKAGE</c>. Any other code is unexpected and reads as
    /// unpackaged, matching the conservative fallback the previous try/catch gave.
    /// </summary>
    internal static bool IsPackagedIdentityCode(int code) => code switch
    {
        ErrorSuccess or ErrorInsufficientBuffer => true,
        AppmodelErrorNoPackage => false,
        _ => false,
    };

    // Blittable signature only (uint by-ref + native int), so the source-generated
    // marshalling stub stays trim/AOT-clean. The return is `int` because the documented
    // Win32 signature returns LONG (signed 32-bit), not DWORD.
    [LibraryImport("kernel32.dll")]
    private static partial int GetCurrentPackageFullName(ref uint packageFullNameLength, nint packageFullName);

    internal static void ResetForTests()
    {
        Volatile.Write(ref _isPackagedComputed, 0);
        Volatile.Write(ref _isPackaged, 0);
    }
}

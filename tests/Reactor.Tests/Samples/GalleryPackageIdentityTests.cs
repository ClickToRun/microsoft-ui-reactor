using System;
using System.Runtime.InteropServices;
using WinUIGalleryReactor;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.Samples;

/// <summary>
/// Pins the <c>GetCurrentPackageFullName</c> status-code mapping in
/// samples/ReactorGallery/DeepLink/GalleryPackageIdentity.cs.
///
/// <para>Getting this wrong fails silently: the gallery would simply take the other
/// flavour's registration path — an unpackaged build believing it is packaged would skip
/// registering the scheme entirely and every <c>reactor-gallery://</c> link would stop
/// working, with nothing thrown or logged. The live probe can only ever report what the
/// test process happens to be, so the mapping is tested directly and then tied back to
/// the OS by an independent binding to the same export.</para>
/// </summary>
public sealed class GalleryPackageIdentityTests
{
    [Theory]
    [InlineData(GalleryPackageIdentity.ErrorSuccess, true)]
    [InlineData(GalleryPackageIdentity.ErrorInsufficientBuffer, true)]
    [InlineData(GalleryPackageIdentity.AppModelErrorNoPackage, false)]
    // Unexpected codes must fall to "unpackaged" — the conservative answer, which keeps
    // the app managing its own registration rather than assuming Windows did.
    [InlineData(5, false)]     // ERROR_ACCESS_DENIED
    [InlineData(-1, false)]
    [InlineData(int.MaxValue, false)]
    public void IsPackaged_MapsTheDocumentedStatusCodes(int status, bool expected)
    {
        Assert.Equal(expected, GalleryPackageIdentity.IsPackaged(status));
    }

    [Fact]
    public void ErrorInsufficientBuffer_IsTreatedAsPackaged_NotAsFailure()
    {
        // The probe passes a zero-length buffer on purpose, so 122 is the *expected*
        // success code for a packaged process. Reading it as an error — the intuitive
        // mistake — would make every packaged build report unpackaged.
        Assert.True(GalleryPackageIdentity.IsPackaged(GalleryPackageIdentity.ErrorInsufficientBuffer));
        Assert.NotEqual(
            GalleryPackageIdentity.IsPackaged(GalleryPackageIdentity.AppModelErrorNoPackage),
            GalleryPackageIdentity.IsPackaged(GalleryPackageIdentity.ErrorInsufficientBuffer));
    }

    [Fact]
    public void Constants_MatchTheWin32Values()
    {
        Assert.Equal(0, GalleryPackageIdentity.ErrorSuccess);
        Assert.Equal(122, GalleryPackageIdentity.ErrorInsufficientBuffer);
        Assert.Equal(15700, GalleryPackageIdentity.AppModelErrorNoPackage);
    }

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, nint packageFullName);

    [Fact]
    public void LiveProbe_ReturnsAStatusTheMappingUnderstands()
    {
        // Independent binding to the same export: ties the table above to OS truth, so a
        // mapping that drifted away from what kernel32 actually returns is caught.
        uint length = 0;
        int status = GetCurrentPackageFullName(ref length, nint.Zero);

        Assert.True(
            status is GalleryPackageIdentity.ErrorSuccess
                   or GalleryPackageIdentity.ErrorInsufficientBuffer
                   or GalleryPackageIdentity.AppModelErrorNoPackage,
            $"Unexpected GetCurrentPackageFullName status {status}.");

        // The headless test host is not packaged, so the probe must say so — and must do
        // it without the first-chance exception that Package.Current would raise.
        Assert.Equal(GalleryPackageIdentity.AppModelErrorNoPackage, status);
        Assert.False(GalleryPackageIdentity.IsPackaged(status));
    }
}

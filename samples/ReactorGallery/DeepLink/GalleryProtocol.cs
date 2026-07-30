using System;
using System.Runtime.InteropServices;
using Microsoft.Windows.AppLifecycle;

namespace WinUIGalleryReactor;

/// <summary>
/// Owns the <c>reactor-gallery://</c> scheme registration.
///
/// <para>The gallery ships in two flavours and they register the scheme very
/// differently:</para>
/// <list type="bullet">
/// <item><b>Packaged (MSIX)</b> — <c>Package.appxmanifest</c> declares a
/// <c>windows.protocol</c> extension, so Windows wires the scheme up at install time
/// and tears it down at uninstall. Everything here is a no-op; there is nothing for
/// the app (or the user) to manage at runtime.</item>
/// <item><b>Unpackaged</b> — no manifest exists, so the app registers itself for the
/// current user via <see cref="ActivationRegistrationManager"/>. That is a real,
/// persistent side effect on the user's machine, so the Settings page surfaces it and
/// lets the user take it back.</item>
/// </list>
///
/// <para>Every operation is best-effort: a registry failure must never stop the
/// gallery from starting.</para>
/// </summary>
public static partial class GalleryProtocol
{
    const string DisplayName = "WinUI Gallery (Reactor)";

    static int _isPackagedComputed;
    static int _isPackaged;

    /// <summary>True when this process runs under an MSIX package identity.</summary>
    /// <remarks>
    /// <para>Deliberately a runtime probe rather than a compile-time
    /// <c>#if PACKAGED</c>: both flavours build from the same sources and produce the
    /// same binaries, so the branch has to be decided at run time.</para>
    /// <para>The probe asks Win32 rather than touching
    /// <c>Windows.ApplicationModel.Package.Current</c>, which reports "unpackaged" by
    /// <em>throwing</em> — turning the normal case for this sample into a first-chance
    /// <c>InvalidOperationException</c> that pollutes the debugger and any crash
    /// telemetry. Mirrors the framework's own <c>Hosting.Shell.PackageRuntime</c>
    /// (internal to <c>Microsoft.UI.Reactor</c>, so a sample cannot reuse it).</para>
    /// </remarks>
    public static bool IsPackaged
    {
        get
        {
            if (Volatile.Read(ref _isPackagedComputed) != 0)
                return Volatile.Read(ref _isPackaged) != 0;

            // A zero-length / NULL buffer asks only the identity question; the status code
            // is interpreted by GalleryPackageIdentity. No try/catch: the export has
            // shipped in kernel32 since Windows 8 and this app's minimum platform is far
            // newer, so wrapping it would only reintroduce exception-driven control flow.
            uint length = 0;
            bool packaged = GalleryPackageIdentity.IsPackaged(GetCurrentPackageFullName(ref length, nint.Zero));

            Volatile.Write(ref _isPackaged, packaged ? 1 : 0);
            Volatile.Write(ref _isPackagedComputed, 1);
            return packaged;
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial int GetCurrentPackageFullName(ref uint packageFullNameLength, nint packageFullName);

    /// <summary>
    /// True when the scheme is managed by the OS on the app's behalf (MSIX), meaning
    /// the app must neither register nor unregister it itself.
    /// </summary>
    public static bool IsManagedByPackage => IsPackaged;

    /// <summary>Icon the shell shows for <c>reactor-gallery://</c> links.</summary>
    /// <remarks>
    /// <c>Path.Join</c> rather than <c>Path.Combine</c>: Combine silently discards
    /// everything before a rooted segment, so it only stays correct while every later
    /// segment is guaranteed relative. Join concatenates unconditionally, which is the
    /// property actually wanted for "a file inside the app directory".
    /// </remarks>
    static string LogoPath =>
        global::System.IO.Path.Join(global::System.AppContext.BaseDirectory, "Assets", "GalleryIcon.ico");

    /// <summary>
    /// True when <c>reactor-gallery://</c> is currently handled by this app. Always
    /// true for the packaged flavour, where the package manifest owns it.
    /// </summary>
    /// <remarks>
    /// Tracked in-process rather than read back from the registry, and that is
    /// deliberate. <see cref="ActivationRegistrationManager"/> has no query API, and
    /// its on-disk layout is an undocumented implementation detail that cannot be
    /// interpreted safely: unregistering removes the generated
    /// <c>App.&lt;hash&gt;.Protocol</c> ProgId that carries the handler command but
    /// leaves <c>HKCU\Software\Classes\reactor-gallery</c> (including its
    /// <c>URL Protocol</c> marker) behind, so the obvious registry probe reports a
    /// false positive for an app that no longer handles the scheme. In-process state
    /// is exact instead of merely plausible, and it is sufficient because startup
    /// always registers — so the flag starts life correct on every launch.
    /// </remarks>
    public static bool IsRegistered => IsPackaged || Volatile.Read(ref _registered) != 0;

    static int _registered;

    /// <summary>
    /// Called once at startup. Registers the scheme for the current user unless the
    /// package already owns it.
    /// </summary>
    /// <remarks>
    /// Registers unconditionally rather than only when missing: the write is an
    /// idempotent overwrite that re-points the handler at the currently running
    /// executable, which is what keeps links working after a rebuild moves the binary.
    /// Turning the scheme off from Settings therefore lasts for the session; the next
    /// launch registers again.
    /// </remarks>
    public static bool EnsureRegistered() => IsPackaged || Register();

    /// <summary>
    /// Write the current-user registration. No-op when packaged.
    /// </summary>
    /// <returns>
    /// Whether the scheme is registered to this app afterwards. See
    /// <see cref="Unregister"/> for the shared contract.
    /// </returns>
    public static bool Register()
    {
        if (IsPackaged) return true;

        try
        {
            // An empty exe path means "the current executable".
            ActivationRegistrationManager.RegisterForProtocolActivation(
                GalleryRoutes.Scheme, LogoPath, DisplayName, string.Empty);
            Volatile.Write(ref _registered, 1);
            return true;
        }
        catch (Exception ex)
        {
            global::System.Diagnostics.Debug.WriteLine(
                $"[Gallery] protocol registration failed: {ex.GetType().Name}: {ex.Message}");
            Volatile.Write(ref _registered, 0);
            return false;
        }
    }

    /// <summary>Remove the current-user registration. No-op when packaged.</summary>
    /// <returns>
    /// Whether the registration state the caller asked for holds afterwards — <b>not</b>
    /// "did a registry write happen". So both methods return <c>true</c> only when the
    /// scheme ends up in the requested state: <see cref="Register"/> returns <c>true</c>
    /// when packaged (the package already registered it), and this returns <c>false</c>
    /// when packaged, because an app cannot revoke a manifest-declared protocol and the
    /// scheme is still handled. Reporting success there would be a lie. Callers that only
    /// need the current state should read <see cref="IsRegistered"/> instead, which is
    /// what the Settings toggle does.
    /// </returns>
    /// <remarks>
    /// Removes the registration for the <em>currently running</em> executable.
    /// <see cref="ActivationRegistrationManager"/> derives its handler ProgId from the
    /// executable path, so a build that has been moved or copied elsewhere leaves the
    /// old location's entry behind — harmless for a sample that is rebuilt in place,
    /// but worth knowing if you relocate the output and want the registry clean.
    /// </remarks>
    public static bool Unregister()
    {
        if (IsPackaged) return false;

        try
        {
            ActivationRegistrationManager.UnregisterForProtocolActivation(
                GalleryRoutes.Scheme, string.Empty);
            Volatile.Write(ref _registered, 0);
            return true;
        }
        catch (Exception ex)
        {
            global::System.Diagnostics.Debug.WriteLine(
                $"[Gallery] protocol unregistration failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}

using System;
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
public static class GalleryProtocol
{
    const string DisplayName = "WinUI Gallery (Reactor)";

    static int _isPackagedComputed;
    static int _isPackaged;

    /// <summary>True when this process runs under an MSIX package identity.</summary>
    /// <remarks>
    /// Mirrors Reactor's own <c>Hosting.Shell.PackageRuntime</c>, which is internal to
    /// the framework. Deliberately a runtime probe rather than a compile-time
    /// <c>#if PACKAGED</c>: both flavours build from the same sources and produce the
    /// same binaries, so the branch has to be decided at run time.
    /// </remarks>
    public static bool IsPackaged
    {
        get
        {
            if (Volatile.Read(ref _isPackagedComputed) != 0)
                return Volatile.Read(ref _isPackaged) != 0;

            bool packaged;
            try
            {
                // Package.Current throws InvalidOperationException when unpackaged.
                _ = global::Windows.ApplicationModel.Package.Current;
                packaged = true;
            }
            catch
            {
                packaged = false;
            }

            Volatile.Write(ref _isPackaged, packaged ? 1 : 0);
            Volatile.Write(ref _isPackagedComputed, 1);
            return packaged;
        }
    }

    /// <summary>
    /// True when the scheme is managed by the OS on the app's behalf (MSIX), meaning
    /// the app must neither register nor unregister it itself.
    /// </summary>
    public static bool IsManagedByPackage => IsPackaged;

    /// <summary>Icon the shell shows for <c>reactor-gallery://</c> links.</summary>
    static string LogoPath =>
        global::System.IO.Path.Combine(global::System.AppContext.BaseDirectory, "Assets", "GalleryIcon.ico");

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

    /// <summary>Write the current-user registration. No-op when packaged.</summary>
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

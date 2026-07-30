using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Reactor;
using Microsoft.Windows.AppLifecycle;

namespace WinUIGalleryReactor;

/// <summary>
/// Turns OS activations into gallery navigation.
///
/// <para>Two paths matter:</para>
/// <list type="number">
/// <item><b>Cold start</b> — the process was launched by a <c>reactor-gallery://</c>
/// link. The URI is parsed before WinUI bootstraps and parked in
/// <see cref="InitialRoute"/>, which the shell reads as its initial state.</item>
/// <item><b>Warm start</b> — the gallery is already running. This process registers
/// as a *keyed* instance, so a second launch hands its activation to the running
/// one and exits, and the running one raises <see cref="RouteActivated"/> on the UI
/// thread. Without this, every link would spawn another gallery window.</item>
/// </list>
///
/// <para>Everything is best-effort: if single-instancing or URI parsing fails, the
/// gallery still starts normally on the home page.</para>
/// </summary>
public static class GalleryActivation
{
    /// <summary>
    /// Key identifying the "main" gallery instance. Any value works as long as it is
    /// stable across launches — it is scoped to the app, not the machine.
    /// </summary>
    const string InstanceKey = "ReactorGallery.Main";

    /// <summary>How long a redirecting instance waits before giving up and exiting.</summary>
    static readonly TimeSpan RedirectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Guards <see cref="_initialRoute"/> and <see cref="_pendingRoute"/>.</summary>
    static readonly object Gate = new();

    static GalleryRoute? _initialRoute;
    static GalleryRoute? _pendingRoute;

    /// <summary>
    /// The route this process was launched with, or <c>null</c> for a plain launch.
    /// Written once by <see cref="TryRedirectToRunningInstance"/> on the startup thread
    /// and read from the UI thread when the shell seeds its state.
    /// </summary>
    public static GalleryRoute? InitialRoute
    {
        get { lock (Gate) return _initialRoute; }
    }

    /// <summary>
    /// Raised on the UI thread when a link arrives while the gallery is already
    /// running.
    /// </summary>
    public static event Action<GalleryRoute>? RouteActivated;

    /// <summary>
    /// Take a route that arrived before anything could handle it — because the UI thread
    /// didn't exist yet, or because the shell hadn't finished subscribing.
    /// </summary>
    /// <remarks>
    /// The shell calls this immediately after subscribing to
    /// <see cref="RouteActivated"/>. Without the hand-off, a link that lands in the gap
    /// between process start and first render is simply dropped: the shell only reads
    /// <see cref="InitialRoute"/> once, on its first render, so a later write there
    /// would never be seen.
    /// </remarks>
    public static bool TryTakePendingRoute([NotNullWhen(true)] out GalleryRoute? route)
    {
        lock (Gate)
        {
            route = _pendingRoute;
            _pendingRoute = null;
        }
        return route is not null;
    }

    /// <summary>
    /// Claim the single-instance key, or hand our activation to whoever already holds
    /// it.
    /// </summary>
    /// <returns>
    /// <c>true</c> when this process redirected and should exit immediately without
    /// starting WinUI; <c>false</c> when this process is the primary instance and
    /// should carry on booting.
    /// </returns>
    public static bool TryRedirectToRunningInstance()
    {
        try
        {
            var args = AppInstance.GetCurrent().GetActivatedEventArgs();
            var keyInstance = AppInstance.FindOrRegisterForKey(InstanceKey);

            if (!keyInstance.IsCurrent)
            {
                RedirectAndWait(keyInstance, args);
                return true;
            }

            // Subscribe before doing anything else with this instance: registering the
            // key is what makes us discoverable, so a second launch can redirect to us
            // from this moment on, and an activation that arrives with no handler
            // attached is lost along with the process that sent it.
            keyInstance.Activated += OnKeyInstanceActivated;

            var route = ResolveRoute(args, allowCommandLineFallback: true);
            lock (Gate) _initialRoute = route;
            return false;
        }
        catch (Exception ex)
        {
            // Single-instancing is a nicety, not a requirement. If the AppLifecycle
            // surface is unavailable we still want a working gallery — just fall
            // back to the command line for a cold-start deep link.
            global::System.Diagnostics.Debug.WriteLine(
                $"[Gallery] single-instance registration failed: {ex.GetType().Name}: {ex.Message}");
            var fallback = RouteFromCommandLine();
            lock (Gate) _initialRoute = fallback;
            return false;
        }
    }

    /// <summary>
    /// Hand <paramref name="args"/> to the primary instance and block until it lands.
    /// </summary>
    /// <remarks>
    /// <c>RedirectActivationToAsync</c> cannot be awaited on the thread that will go on
    /// to pump messages — the documented pattern is to run it on the thread pool and
    /// block the launching thread. The timeout keeps a wedged primary instance from
    /// leaving an invisible zombie process behind.
    /// </remarks>
    static void RedirectAndWait(AppInstance target, AppActivationArguments args)
    {
        try
        {
            var redirect = Task.Run(async () => await target.RedirectActivationToAsync(args));
            redirect.Wait(RedirectTimeout);
        }
        catch (Exception ex)
        {
            global::System.Diagnostics.Debug.WriteLine(
                $"[Gallery] activation redirect failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles an activation forwarded from a second launch. Fires on a background
    /// thread, so both the navigation and the window activation are marshalled onto
    /// the UI thread.
    /// </summary>
    static void OnKeyInstanceActivated(object? sender, AppActivationArguments args)
    {
        // No command-line fallback here: this process's argv belongs to the *original*
        // launch, so falling back to it would re-navigate to wherever the gallery was
        // first opened instead of honouring (or ignoring) the incoming activation.
        var route = ResolveRoute(args, allowCommandLineFallback: false) ?? GalleryRoutes.HomeRoute;

        var dispatcher = ReactorApp.UIDispatcher;
        if (dispatcher is null)
        {
            // The UI thread doesn't exist yet — this activation raced startup. Park the
            // route for the shell to drain once it has subscribed.
            Park(route);
            return;
        }

        if (!dispatcher.TryEnqueue(() =>
        {
            var handler = RouteActivated;
            if (handler is null)
            {
                // Mounted between the null check and here, or not mounted at all.
                Park(route);
                return;
            }

            // Bring the existing window forward first: a link that navigates a window
            // the user can't see is worse than not handling it at all.
            try { ReactorApp.PrimaryWindow?.Activate(); }
            catch (Exception ex)
            {
                global::System.Diagnostics.Debug.WriteLine(
                    $"[Gallery] window activation failed: {ex.GetType().Name}: {ex.Message}");
            }

            handler(route);
        }))
        {
            // The dispatcher refused the work item (shutting down). Park rather than
            // silently drop, in case another window is still coming up.
            Park(route);
        }
    }

    /// <summary>
    /// Hold a route that could not be delivered yet. Last one wins: if two links arrive
    /// before the shell is listening, the user's most recent intent is the right one.
    /// </summary>
    static void Park(GalleryRoute route)
    {
        lock (Gate) _pendingRoute = route;
    }

    /// <summary>
    /// Extract a route from an activation payload, or <c>null</c> when it carries no
    /// usable link.
    /// </summary>
    static GalleryRoute? ResolveRoute(AppActivationArguments? args, bool allowCommandLineFallback)
    {
        try
        {
            if (args?.Kind == ExtendedActivationKind.Protocol &&
                args.Data is global::Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocolArgs &&
                GalleryRoutes.TryResolve(protocolArgs.Uri?.ToString(), out var protocolRoute))
            {
                return protocolRoute;
            }

            if (args?.Data is global::Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launchArgs &&
                GalleryRoutes.TryResolve(launchArgs.Arguments, out var launchRoute))
            {
                return launchRoute;
            }
        }
        catch (Exception ex)
        {
            global::System.Diagnostics.Debug.WriteLine(
                $"[Gallery] activation parse failed: {ex.GetType().Name}: {ex.Message}");
        }

        return allowCommandLineFallback ? RouteFromCommandLine() : null;
    }

    /// <summary>
    /// Last-resort parse of the raw command line, covering
    /// <c>ReactorGallery.exe reactor-gallery:///item/button</c> typed by hand and any
    /// shell that passes the URI through without the AppLifecycle marker.
    /// </summary>
    static GalleryRoute? RouteFromCommandLine()
    {
        try
        {
            var argv = global::System.Environment.GetCommandLineArgs();
            for (int i = 1; i < argv.Length; i++)
            {
                if (GalleryRoutes.TryResolve(argv[i], out var route))
                    return route;
            }
        }
        catch (Exception ex)
        {
            global::System.Diagnostics.Debug.WriteLine(
                $"[Gallery] command-line parse failed: {ex.GetType().Name}: {ex.Message}");
        }

        return null;
    }
}

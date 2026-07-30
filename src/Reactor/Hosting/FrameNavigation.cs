using System.Diagnostics.CodeAnalysis;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Microsoft.UI.Reactor.Hosting;

/// <summary>
/// Shared entry point for every <c>Frame.Navigate</c> Reactor performs
/// (<c>FrameElement</c> and <c>XamlPageElement</c>).
///
/// <para>Navigating to a type the XAML metadata chain cannot resolve kills the process with
/// an access violation inside native WinUI — not a managed exception, so nothing can catch
/// it (see <see cref="ReactorPageTypeRegistry"/> for the full explanation). Every navigation
/// therefore goes through here: the target is first published to the metadata chain, then
/// verified to be resolvable, and only then handed to WinUI.</para>
/// </summary>
internal static class FrameNavigation
{
    /// <summary>
    /// Pure decision seam: is <paramref name="pageType"/> resolvable by
    /// <paramref name="resolveXamlType"/>? Shaped around a delegate rather than
    /// <see cref="IXamlMetadataProvider"/> so it stays exercisable from headless tests, which
    /// cannot construct WinUI objects.
    ///
    /// <para>A resolver that throws counts as "not resolvable" — a broken provider must
    /// degrade into a reported navigation failure, never into the access violation that
    /// calling <c>Navigate</c> anyway would produce.</para>
    /// </summary>
    internal static bool CanResolvePageType(Type? pageType, Func<Type, object?> resolveXamlType)
    {
        ArgumentNullException.ThrowIfNull(resolveXamlType);
        if (pageType is null) return false;
        try { return resolveXamlType(pageType) is not null; }
        // Deliberately BROAD, and not a narrowing: an exception filter still compiles to an IL
        // filter region with a nil CatchType, so this catches everything except the two fatal
        // carve-outs. That is intended here — spec 044's audit keeps broad catches for
        // "genuine fail-safe-to-default behavior", and this method's contract is "true only if
        // definitively resolvable". Any failure to answer means we cannot confirm, and the safe
        // default is to refuse the navigation rather than risk the access violation.
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { return false; }
    }

    /// <summary>
    /// Builds the resolver for the running application. Returns a resolver that always yields
    /// <c>null</c> when there is no <see cref="Application.Current"/> or it does not implement
    /// <see cref="IXamlMetadataProvider"/> — in both cases WinUI has no way to resolve a
    /// custom page type and navigating would fault.
    /// </summary>
    internal static Func<Type, object?> CurrentApplicationResolver()
        => Application.Current is IXamlMetadataProvider provider
            ? provider.GetXamlType
            : static _ => null;

    /// <summary>
    /// Publishes <paramref name="pageType"/> to the XAML metadata chain and navigates
    /// <paramref name="frame"/> to it.
    /// </summary>
    /// <returns>
    /// <c>null</c> when the navigation was handed to WinUI; otherwise the reason it was
    /// refused, which the caller surfaces through the element's navigation-failed callback.
    /// </returns>
    internal static Exception? TryNavigate(
        Frame frame,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type pageType,
        object? parameter)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(pageType);

        // Publish first: for a Reactor app this is what makes the type resolvable at all.
        ReactorPageTypeRegistry.Register(pageType);

        if (!CanResolvePageType(pageType, CurrentApplicationResolver()))
            return new InvalidOperationException(BuildUnresolvableMessage(pageType));

        try
        {
            // Frame exposes both a 1-arg and a 2-arg overload; the 2-arg one boxes a null
            // parameter into the navigation entry, so prefer the 1-arg form when there is
            // nothing to pass.
            if (parameter is null) frame.Navigate(pageType);
            else frame.Navigate(pageType, parameter);
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Deliberately BROAD, and not a narrowing — the filter above still compiles to an
            // IL filter region with a nil CatchType. Intended: what surfaces here is the *page
            // constructor's* exception, i.e. arbitrary application code, and routing it into
            // the element's declared navigation-failed channel is precisely spec 044's
            // "user-callback isolation / fail-safe-to-default" Keep category. A type list would
            // let an unanticipated page-constructor failure escape the mount pass, which is the
            // behaviour this arm exists to prevent.
            //
            // A page whose constructor throws surfaces one of two ways depending on how
            // WinUI classifies the failure: either it raises NavigationFailed (which the
            // element's own trampoline marks Handled, so Navigate returns normally and we
            // never get here), or it propagates the failure straight out of Navigate. This
            // arm covers the second case. The two paths are mutually exclusive, so the
            // failure is never reported twice.
            return ex;
        }
    }

    // Neither refusal reason is helped by the registration APIs, but for different reasons,
    // and the distinction is the useful part:
    //   • Host-app case — ReactorApp.RegisterPageType / RegisterControlAssembly both feed
    //     ReactorApplication.GetXamlType, which is exactly the chain that is NOT running when a
    //     non-Reactor Application owns type resolution. Registering would be a no-op there.
    //   • Non-activatable case — publishing is refused by design (see
    //     ReactorPageTypeRegistry.Register): WinUI could never activate an open generic or a
    //     type with no full name, so registering harder changes nothing. The fix is the type.
    // Hence the message below sends each case somewhere genuinely different.
    private static string BuildUnresolvableMessage(Type pageType)
    {
        // FullName is null for generic parameters and some constructed types, and WinUI keys
        // its lookup on it — so print something identifiable rather than an empty string.
        var name = pageType.FullName ?? pageType.Name;
        return $"Frame navigation to '{name}' was refused: the WinUI XAML metadata chain cannot " +
               "resolve the type, and calling Frame.Navigate anyway terminates the process with an " +
               "access violation. Either the target cannot be published as a navigation target — " +
               "open generics and types without a full name are skipped because WinUI could never " +
               "activate them, so use a concrete, closed, named Page type — or Application.Current " +
               "is not a ReactorApplication (for example a Reactor tree hosted inside a stock WinUI " +
               "app via ReactorHostControl), in which case the host application owns type " +
               "resolution: give the page a .xaml file in the host project so its XAML compiler " +
               "emits the type into the host's generated metadata, or make the host's Application " +
               "implement IXamlMetadataProvider and resolve the type itself.";
    }
}

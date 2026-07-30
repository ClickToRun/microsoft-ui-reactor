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
        catch { return false; }
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
        catch (Exception ex)
        {
            // A page whose constructor throws surfaces one of two ways depending on how
            // WinUI classifies the failure: either it raises NavigationFailed (which the
            // element's own trampoline marks Handled, so Navigate returns normally and we
            // never get here), or it propagates the failure straight out of Navigate. This
            // arm covers the second case so a broken page degrades into the caller's
            // navigation-failed channel instead of tearing down the render pass. The two
            // paths are mutually exclusive, so the failure is never reported twice.
            return ex;
        }
    }

    // Deliberately does NOT suggest ReactorApp.RegisterPageType / RegisterControlAssembly:
    // both feed ReactorApplication.GetXamlType, which is exactly the chain that is not
    // running in the only case that reaches this message.
    private static string BuildUnresolvableMessage(Type pageType)
        => $"Frame navigation to '{pageType.FullName}' was refused: the WinUI XAML metadata chain " +
           "cannot resolve the type, and calling Frame.Navigate anyway terminates the process with " +
           "an access violation. Reactor publishes navigation targets itself, so this only happens " +
           "when Application.Current is not a ReactorApplication — for example a Reactor tree hosted " +
           "inside a stock WinUI app via ReactorHostControl. In that case the host application owns " +
           "type resolution: give the page a .xaml file in the host project so its XAML compiler " +
           "emits the type into the host's generated metadata, or make the host's Application " +
           "implement IXamlMetadataProvider and resolve the type itself.";
}

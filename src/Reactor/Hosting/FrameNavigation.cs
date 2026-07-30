using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Microsoft.UI.Reactor.Hosting;

/// <summary>
/// Shared entry point for every <c>Frame.Navigate</c> Reactor performs
/// (<c>FrameElement</c> and <c>XamlPageElement</c>).
///
/// <para>Navigating to a type the XAML metadata chain cannot resolve kills the process with
/// an access violation inside native WinUI — not a managed exception, so nothing can catch it.
/// WinUI resolves a custom navigation target through <c>Application.Current</c>'s
/// <see cref="IXamlMetadataProvider"/>, and dereferences the null it gets back when the type
/// is absent. Every navigation therefore goes through here, which verifies the target is
/// resolvable and refuses rather than handing an unresolvable one to WinUI.</para>
///
/// <para><b>Reactor deliberately does not make code-only pages resolvable.</b> Publishing
/// synthesized XAML metadata would violate spec 011's "zero XAML dependency" goal and would
/// only partially work — three further <c>Frame</c> constraints remain (the <c>IPage</c>
/// hard-cast in <c>PageStackEntry::PrepareContent</c>, parameterless-constructor activation,
/// and the absence of extension points). <c>Frame</c> exists for interop with apps that
/// already have XAML pages; those are in the host's generated metadata and resolve normally.
/// For navigation inside a Reactor app, use <c>UseNavigation&lt;TRoute&gt;</c> +
/// <c>NavigationHost</c>, which is the designed system.</para>
/// </summary>
internal static class FrameNavigation
{
    /// <summary>
    /// Pure decision seam: can WinUI resolve <paramref name="pageType"/> as a navigation
    /// target? Shaped around a delegate rather than <see cref="IXamlMetadataProvider"/> so it
    /// stays exercisable from headless tests, which cannot construct WinUI objects.
    ///
    /// <para>Two ways a type is resolvable, and the first is easy to miss. A
    /// <b>WinRT-projected</b> type (<c>Microsoft.UI.Xaml.Controls.Page</c> and anything else
    /// carrying <c>WindowsRuntimeTypeAttribute</c>) lives in the native WinRT type system, so
    /// <c>MetadataAPI</c> finds it without consulting any managed provider — asking
    /// <c>Application.Current</c> about it returns null even though navigation would have
    /// worked. Refusing on that null would break navigation that works today. Everything
    /// else is a managed type, invisible to native metadata unless a generated
    /// <c>XamlTypeInfo</c> publishes it, which is what the resolver is asked about.</para>
    ///
    /// <para>A resolver that throws counts as "not resolvable" — a broken provider must
    /// degrade into a reported navigation failure, never into the access violation that
    /// calling <c>Navigate</c> anyway would produce.</para>
    /// </summary>
    internal static bool CanResolvePageType(Type? pageType, Func<Type, object?> resolveXamlType)
    {
        ArgumentNullException.ThrowIfNull(resolveXamlType);
        if (pageType is null) return false;
        // inherit: false is load-bearing — a managed `class MyPage : Page` must NOT inherit the
        // attribute from its projected base, or every code-only page would look resolvable.
        if (pageType.IsDefined(typeof(global::WinRT.WindowsRuntimeTypeAttribute), inherit: false)) return true;
        try { return resolveXamlType(pageType) is not null; }
        // Deliberately BROAD, and not a narrowing: an exception filter still compiles to an IL
        // filter region with a nil CatchType, so this catches everything except the two fatal
        // carve-outs. That is intended here — spec 044's audit keeps broad catches for
        // "genuine fail-safe-to-default behavior", and this method's contract is "true only if
        // definitively resolvable". Any failure to answer means we cannot confirm, and the safe
        // default is to refuse the navigation rather than risk the access violation.
        //
        // If you are about to replace this with a type list, the argument you need is: the usual
        // case for propagating is that swallowing changes the outcome — you continue in a state
        // you should not be in. That does not hold here. The navigation is refused either way,
        // so propagating buys no correctness and costs a crash; it would only convert a
        // third-party provider's bug into a render-loop error. Expected throws are COMException
        // at the WinRT boundary, InvalidOperationException / ArgumentException from a
        // hand-written provider, and TypeLoadException / FileNotFoundException from an
        // unloadable assembly — but the list is not what makes this safe, the identical outcome is.
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
        Type pageType,
        object? parameter)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(pageType);

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

    // The refusal message's job is to redirect, not just to report. The overwhelmingly
    // likely cause is a code-only Page in a Reactor app, and the answer to that is not
    // "make Frame work harder" — it is "you are using the wrong navigation system".
    // Internal rather than private so a headless test can pin that redirect: dropping the
    // UseNavigation pointer would silently turn a signpost back into a dead end.
    internal static string BuildUnresolvableMessage(Type pageType)
    {
        // FullName is null for generic parameters and some constructed types, and WinUI keys
        // its lookup on it — so print something identifiable rather than an empty string.
        var name = pageType.FullName ?? pageType.Name;
        return $"Frame navigation to '{name}' was refused: the WinUI XAML metadata chain cannot " +
               "resolve the type, and calling Frame.Navigate anyway terminates the process with an " +
               "access violation rather than throwing. " +
               "Frame is for interop with pages that already have a .xaml file — the XAML compiler " +
               "emits those into the host's generated metadata, so they resolve. A Page declared " +
               "only in C# is absent from that metadata and cannot be navigated to. " +
               "For navigation inside a Reactor app use UseNavigation<TRoute> with NavigationHost, " +
               "which needs no XAML, no Page subclass and no parameterless constructor (see " +
               "docs/guide/navigation.md). To keep using Frame, give the page a .xaml file in the " +
               "host project.";
    }
}

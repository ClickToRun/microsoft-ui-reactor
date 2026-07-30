using System.Diagnostics.CodeAnalysis;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;

namespace Microsoft.UI.Reactor.Hosting;

// ════════════════════════════════════════════════════════════════════════
//  Publishing code-only Page types to the XAML metadata chain
// ════════════════════════════════════════════════════════════════════════
//
// WinUI's Frame.Navigate resolves its target through
// MetadataAPI::GetClassInfoByTypeName, which for a *custom* (non-WinUI) type
// falls through to Application.Current's IXamlMetadataProvider. When that
// lookup returns null the native code dereferences it anyway and the process
// dies with an access violation (0xC0000005) inside
// ActivationAPI::ActivateInstance — no managed exception, no
// Application.UnhandledException, nothing to catch. This is the constraint
// recorded in docs/specs/011-navigation-design.md §"Why WinUI Frame is not
// the answer".
//
// A normal WinUI 3 app never hits it because its pages have .xaml files, so
// the XAML compiler emits them into <App>_XamlTypeInfo. A Reactor app has no
// XAML at all, so ReactorApplication.HostAppProvider collapses to
// EmptyXamlMetadataProvider and *every* app-defined page is invisible.
//
// The cure is to supply the same shape the XAML compiler would have: a
// constructible IXamlType for the page (XamlUserType) whose BaseType is a
// schema-only stub for its nearest framework ancestor (XamlSystemBaseType).
// Registration is explicit — driven from FrameElement / XamlPageElement mount
// — rather than a blanket reflective scan, so the activator can be
// trim/AOT-annotated and Reactor never invents metadata for arbitrary names
// the XAML parser happens to ask about.

/// <summary>
/// Process-wide registry of app-defined page types that Reactor has published to the
/// WinUI XAML metadata chain so <c>Frame.Navigate</c> can resolve them.
/// </summary>
internal static class ReactorPageTypeRegistry
{
    // CopyOnWrite snapshot semantics, matching ReactorApp's registered-provider list:
    // reads happen from GetXamlType on the UI thread (hot, called by native XAML) and
    // must not lock.
    private static Dictionary<Type, ReactorUserXamlType> _byType = [];
    private static Dictionary<string, ReactorUserXamlType> _byName = new(StringComparer.Ordinal);
    private static readonly object _lock = new();

    /// <summary>
    /// Publishes <paramref name="pageType"/> to the XAML metadata chain. Idempotent and
    /// thread-safe. Types WinUI could never activate as a navigation target — open generics
    /// and anything without a usable <see cref="Type.FullName"/>, which is the key WinUI
    /// looks up by — are ignored rather than published.
    /// </summary>
    internal static void Register(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type pageType)
    {
        ArgumentNullException.ThrowIfNull(pageType);
        if (pageType.ContainsGenericParameters) return;
        var fullName = pageType.FullName;
        if (fullName is null) return;
        if (Volatile.Read(ref _byType).ContainsKey(pageType)) return;

        lock (_lock)
        {
            var currentByType = _byType;
            if (currentByType.ContainsKey(pageType)) return;

            var entry = new ReactorUserXamlType(pageType, ReactorSystemBaseXamlType.ForNearestFrameworkAncestor(pageType));

            var nextByType = new Dictionary<Type, ReactorUserXamlType>(currentByType) { [pageType] = entry };
            var nextByName = new Dictionary<string, ReactorUserXamlType>(_byName, StringComparer.Ordinal) { [fullName] = entry };
            Volatile.Write(ref _byName, nextByName);
            Volatile.Write(ref _byType, nextByType);
        }
    }

    internal static IXamlType? Resolve(Type type)
        => type is not null && Volatile.Read(ref _byType).TryGetValue(type, out var t) ? t : null;

    internal static IXamlType? Resolve(string fullName)
        => fullName is not null && Volatile.Read(ref _byName).TryGetValue(fullName, out var t) ? t : null;
}

/// <summary>
/// The provider face of <see cref="ReactorPageTypeRegistry"/>, chained into
/// <see cref="ReactorApplication"/>'s <c>IXamlMetadataProvider</c> implementation.
/// </summary>
internal sealed partial class ReactorPageXamlMetadataProvider : IXamlMetadataProvider
{
    internal static readonly ReactorPageXamlMetadataProvider Instance = new();

    public IXamlType? GetXamlType(Type type) => ReactorPageTypeRegistry.Resolve(type);
    public IXamlType? GetXamlType(string fullName) => ReactorPageTypeRegistry.Resolve(fullName);
    public XmlnsDefinition[] GetXmlnsDefinitions() => [];
}

/// <summary>
/// Constructible metadata for an app-defined type, mirroring the <c>XamlUserType</c> the
/// XAML compiler emits into <c>XamlTypeInfo.g.cs</c> for a page with code-behind. Only the
/// members WinUI needs to resolve and activate a navigation target are meaningful — Reactor
/// pages are never parsed from markup, so member lookup and string conversion are not
/// supported.
/// </summary>
internal sealed partial class ReactorUserXamlType : IXamlType
{
    // Annotated field ← annotated ctor param ← annotated ReactorPageTypeRegistry.Register
    // param ← annotated FrameElement.SourcePageType. Keeps the parameterless constructor
    // rooted under trimming/AOT all the way from the caller's `typeof(MyPage)`.
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
    private readonly Type _underlyingType;

    internal ReactorUserXamlType(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type underlyingType,
        IXamlType? baseType)
    {
        _underlyingType = underlyingType;
        FullName = underlyingType.FullName!;
        BaseType = baseType;
    }

    public string FullName { get; }
    public Type UnderlyingType => _underlyingType;
    public IXamlType? BaseType { get; }
    public IXamlMember? ContentProperty => null;
    public bool IsArray => false;
    public bool IsCollection => false;
    public bool IsConstructible => true;
    public bool IsDictionary => false;
    public bool IsMarkupExtension => false;
    public bool IsBindable => false;
    public bool IsReturnTypeStub => false;
    public bool IsLocalType => true;
    public IXamlType? ItemType => null;
    public IXamlType? KeyType => null;
    public IXamlType? BoxedType => null;

    public IXamlMember? GetMember(string name) => null;

    public object ActivateInstance()
    {
        try
        {
            return Activator.CreateInstance(_underlyingType)!;
        }
        catch (global::System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Report what the page's constructor actually threw. Activator wraps it, so a
            // failing page would otherwise surface as the useless "Exception has been thrown
            // by the target of an invocation" through NavigationFailed. The XAML compiler's
            // generated activator calls `new MyPage()` directly and does not wrap either.
            global::System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw; // unreachable — Throw() always rethrows.
        }
    }

    public void AddToMap(object instance, object key, object item)
        => throw new NotSupportedException($"{FullName} is not a XAML dictionary.");

    public void AddToVector(object instance, object item)
        => throw new NotSupportedException($"{FullName} is not a XAML collection.");

    [UnconditionalSuppressMessage("Trimming", "IL2059",
        Justification = "The registered page type is kept alive by the annotated _underlyingType field, so its static constructor is preserved along with it; the handle here always refers to a live, non-trimmed type.")]
    public void RunInitializer()
        => global::System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(_underlyingType.TypeHandle);

    public object CreateFromString(string input)
        => throw new NotSupportedException($"{FullName} cannot be created from a string.");
}

/// <summary>
/// Schema-only metadata for a framework base type, mirroring the <c>XamlSystemBaseType</c>
/// the XAML compiler emits. Exists purely so a <see cref="ReactorUserXamlType"/> can report a
/// <see cref="IXamlType.BaseType"/> that WinUI recognises (e.g. <c>Page</c>); it is never
/// activated.
/// </summary>
internal sealed partial class ReactorSystemBaseXamlType : IXamlType
{
    private ReactorSystemBaseXamlType(Type underlyingType)
    {
        UnderlyingType = underlyingType;
        FullName = underlyingType.FullName!;
    }

    /// <summary>
    /// Walks up from <paramref name="type"/> to the first ancestor declared by the WinUI
    /// assembly. Intermediate app-defined base classes are skipped: WinUI only needs the
    /// chain to terminate at a type it already knows, and flattening avoids demanding
    /// constructor metadata for abstract intermediates that are never activated.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "Only FullName/UnderlyingType are read off the framework ancestor; it is never activated or reflected over.")]
    internal static IXamlType? ForNearestFrameworkAncestor(Type type)
    {
        var frameworkAssembly = typeof(DependencyObject).Assembly;
        for (var t = type.BaseType; t is not null; t = t.BaseType)
        {
            if (t.Assembly == frameworkAssembly && t.FullName is not null)
                return new ReactorSystemBaseXamlType(t);
        }
        return null;
    }

    public string FullName { get; }
    public Type UnderlyingType { get; }
    public IXamlType? BaseType => null;
    public IXamlMember? ContentProperty => null;
    public bool IsArray => false;
    public bool IsCollection => false;
    public bool IsConstructible => false;
    public bool IsDictionary => false;
    public bool IsMarkupExtension => false;
    public bool IsBindable => false;
    public bool IsReturnTypeStub => false;
    public bool IsLocalType => false;
    public IXamlType? ItemType => null;
    public IXamlType? KeyType => null;
    public IXamlType? BoxedType => null;

    public IXamlMember? GetMember(string name) => null;

    public object ActivateInstance()
        => throw new NotSupportedException($"{FullName} is schema-only; cannot activate from XAML.");

    public void AddToMap(object instance, object key, object item) => throw new NotSupportedException();
    public void AddToVector(object instance, object item) => throw new NotSupportedException();
    public void RunInitializer() { }

    public object CreateFromString(string input)
        => throw new NotSupportedException($"{FullName} cannot be created from a string.");
}

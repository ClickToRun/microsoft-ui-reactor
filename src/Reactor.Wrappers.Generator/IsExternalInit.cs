// netstandard2.0 polyfill: the C# compiler requires this type to emit `init`
// accessors and positional records, which this generator uses internally.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}

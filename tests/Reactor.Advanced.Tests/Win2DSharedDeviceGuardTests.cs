using System.Reflection;
using Microsoft.UI.Reactor.Advanced.Win2D;
using Xunit;

namespace Microsoft.UI.Reactor.Advanced.Tests;

/// <summary>
/// Verifies the internal <c>Win2DSharedDeviceGuard</c> contract that backs the canvas handlers'
/// "UseSharedDevice is fixed at mount" rule. The guard is internal, so it is invoked via reflection
/// (the same approach <see cref="ElementConstructorTests"/> uses for internal element members).
/// </summary>
public sealed class Win2DSharedDeviceGuardTests
{
    private static MethodInfo GuardMethod()
    {
        var type = typeof(Win2DCanvasElement).Assembly
            .GetType("Microsoft.UI.Reactor.Advanced.Win2D.Win2DSharedDeviceGuard", throwOnError: true)!;
        return type.GetMethod("EnsureUseSharedDeviceUnchanged", BindingFlags.Public | BindingFlags.Static)!;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void EnsureUseSharedDeviceUnchanged_SameValue_DoesNotThrow(bool oldValue, bool newValue)
    {
        var ex = Record.Exception(() => GuardMethod().Invoke(null, [oldValue, newValue]));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void EnsureUseSharedDeviceUnchanged_ChangedValue_FailsFastInDebug(bool oldValue, bool newValue)
    {
        var ex = Record.Exception(() => GuardMethod().Invoke(null, [oldValue, newValue]));
#if DEBUG
        // Reactor.Advanced built Debug: toggling across renders fails fast with a clear message
        // rather than performing the crash-prone in-place device recreation.
        Assert.IsType<InvalidOperationException>(Assert.IsType<TargetInvocationException>(ex).InnerException);
#else
        // Release: the new value is intentionally ignored (control keeps its mount-time device).
        Assert.Null(ex);
#endif
    }
}

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.UI.Reactor.Wrappers.Generator;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.WrappersGenerator;

/// <summary>
/// Tests for <see cref="WrapLifecycleAnalyzer"/> (REACTORGEN011) — the
/// <c>[WrapLifecycle]</c> OnMounted/OnUnmounted methods must be static methods on
/// the element record taking a single parameter assignable from the control.
/// </summary>
public class WrapLifecycleAnalyzerTests
{
    private const string Stubs = @"
using System;
namespace Microsoft.UI.Reactor.Wrappers
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    internal sealed class GenerateReactorWrapperAttribute : Attribute
    {
        public GenerateReactorWrapperAttribute(Type controlType) { }
    }
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
    internal sealed class WrapLifecycleAttribute : Attribute
    {
        public WrapLifecycleAttribute(string onMounted) { }
        public string? OnUnmounted { get; set; }
    }
}
public class Cam { }
";

    private static Task Verify(string body, params DiagnosticResult[] expected)
    {
        var test = new CSharpAnalyzerTest<WrapLifecycleAnalyzer, DefaultVerifier> { TestCode = Stubs + body };
        test.ExpectedDiagnostics.AddRange(expected);
        return test.RunAsync();
    }

    [Fact]
    public Task Valid_Static_Mount_And_Unmount_Methods_Are_Clean() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Cam))]
[Microsoft.UI.Reactor.Wrappers.WrapLifecycle(""Start"", OnUnmounted = ""Stop"")]
partial class CamElement
{
    private static void Start(Cam c) { }
    private static void Stop(Cam c) { }
}
");

    [Fact]
    public Task Missing_Mount_Method_Errors() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Cam))]
[{|REACTORGEN011:Microsoft.UI.Reactor.Wrappers.WrapLifecycle(""Nope"")|}]
partial class CamElement { }
");

    [Fact]
    public Task Instance_Method_Errors() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Cam))]
[{|REACTORGEN011:Microsoft.UI.Reactor.Wrappers.WrapLifecycle(""Start"")|}]
partial class CamElement
{
    private void Start(Cam c) { }   // not static
}
");

    [Fact]
    public Task Wrong_Parameter_Type_Errors() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Cam))]
[{|REACTORGEN011:Microsoft.UI.Reactor.Wrappers.WrapLifecycle(""Start"")|}]
partial class CamElement
{
    private static void Start(string s) { }   // wrong parameter type
}
");

    [Fact]
    public Task Bad_OnUnmounted_Method_Errors() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Cam))]
[{|REACTORGEN011:Microsoft.UI.Reactor.Wrappers.WrapLifecycle(""Start"", OnUnmounted = ""Gone"")|}]
partial class CamElement
{
    private static void Start(Cam c) { }
}
");
}

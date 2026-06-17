using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.UI.Reactor.Wrappers.Generator;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.WrappersGenerator;

/// <summary>
/// Tests for <see cref="WrapOneWayAnalyzer"/> (REACTORGEN006), which errors when
/// a <c>[WrapOneWay(property)]</c> names a property that does not exist.
/// </summary>
public class WrapOneWayAnalyzerTests
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
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
    internal sealed class WrapOneWayAttribute : Attribute
    {
        public WrapOneWayAttribute(string property) { }
    }
}
public class Gauge { public double Value { get; set; } }
";

    private static Task Verify(string body, params DiagnosticResult[] expected)
    {
        var test = new CSharpAnalyzerTest<WrapOneWayAnalyzer, DefaultVerifier> { TestCode = Stubs + body };
        test.ExpectedDiagnostics.AddRange(expected);
        return test.RunAsync();
    }

    [Fact]
    public Task Unknown_Property_Errors() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Gauge))]
[{|REACTORGEN006:Microsoft.UI.Reactor.Wrappers.WrapOneWay(""Nope"")|}]
partial class GaugeElement { }
");

    [Fact]
    public Task Valid_Property_Is_Clean() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Gauge))]
[Microsoft.UI.Reactor.Wrappers.WrapOneWay(""Value"")]
partial class GaugeElement { }
");
}

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.UI.Reactor.Wrappers.Generator;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.WrappersGenerator;

/// <summary>
/// Tests for <see cref="WrapAliasAnalyzer"/> (REACTORGEN005), which errors when a
/// <c>[WrapAlias(name, controlProperty)]</c> names a control property that does
/// not exist.
/// </summary>
public class WrapAliasAnalyzerTests
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
    internal sealed class WrapAliasAttribute : Attribute
    {
        public WrapAliasAttribute(string name, string controlProperty) { }
    }
}
public class Range { public double Minimum { get; set; } }
";

    private static Task Verify(string body, params DiagnosticResult[] expected)
    {
        var test = new CSharpAnalyzerTest<WrapAliasAnalyzer, DefaultVerifier> { TestCode = Stubs + body };
        test.ExpectedDiagnostics.AddRange(expected);
        return test.RunAsync();
    }

    [Fact]
    public Task Unknown_Control_Property_Errors() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Range))]
[{|REACTORGEN005:Microsoft.UI.Reactor.Wrappers.WrapAlias(""Min"", ""Bogus"")|}]
partial class RangeElement { }
");

    [Fact]
    public Task Valid_Alias_Is_Clean() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Range))]
[Microsoft.UI.Reactor.Wrappers.WrapAlias(""Min"", ""Minimum"")]
partial class RangeElement { }
");
}

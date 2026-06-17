using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.UI.Reactor.Wrappers.Generator;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.WrappersGenerator;

/// <summary>
/// Tests for <see cref="WrapContentAnalyzer"/> (REACTORGEN007), which errors when
/// a <c>[WrapContent(property)]</c> names a property that does not exist.
/// </summary>
public class WrapContentAnalyzerTests
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
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    internal sealed class WrapContentAttribute : Attribute
    {
        public WrapContentAttribute(string property) { }
    }
}
public class Holder { public object Body { get; set; } }
";

    private static Task Verify(string body, params DiagnosticResult[] expected)
    {
        var test = new CSharpAnalyzerTest<WrapContentAnalyzer, DefaultVerifier> { TestCode = Stubs + body };
        test.ExpectedDiagnostics.AddRange(expected);
        return test.RunAsync();
    }

    [Fact]
    public Task Unknown_Property_Errors() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Holder))]
[{|REACTORGEN007:Microsoft.UI.Reactor.Wrappers.WrapContent(""Nope"")|}]
partial class HolderElement { }
");

    [Fact]
    public Task Valid_Property_Is_Clean() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Holder))]
[Microsoft.UI.Reactor.Wrappers.WrapContent(""Body"")]
partial class HolderElement { }
");
}

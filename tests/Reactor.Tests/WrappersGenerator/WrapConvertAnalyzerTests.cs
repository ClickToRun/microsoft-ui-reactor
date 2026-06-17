using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.UI.Reactor.Wrappers.Generator;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.WrappersGenerator;

/// <summary>
/// Tests for <see cref="WrapConvertAnalyzer"/> (REACTORGEN008), which errors when a
/// <c>[WrapConvert(property)]</c> names a property that is not a public settable
/// property of the control whose type has a public single-argument constructor.
/// </summary>
public class WrapConvertAnalyzerTests
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
    internal sealed class GenerateReactorDescriptorAttribute : Attribute
    {
        public GenerateReactorDescriptorAttribute(Type controlType) { }
    }
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
    internal sealed class WrapConvertAttribute : Attribute
    {
        public WrapConvertAttribute(string property) { }
    }
}
public struct Corner { public Corner(double uniform) {} }
public struct Plain { public double X; }
public class Ctl
{
    public Corner Cr { get; set; }
    public Plain Pl { get; set; }
}
";

    private static Task Verify(string body, params DiagnosticResult[] expected)
    {
        var test = new CSharpAnalyzerTest<WrapConvertAnalyzer, DefaultVerifier> { TestCode = Stubs + body };
        test.ExpectedDiagnostics.AddRange(expected);
        return test.RunAsync();
    }

    [Fact]
    public Task Convertible_Struct_Property_Is_Clean() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Ctl))]
[Microsoft.UI.Reactor.Wrappers.WrapConvert(""Cr"")]
partial class CtlElement { }
");

    [Fact]
    public Task Unknown_Property_Errors() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Ctl))]
[{|REACTORGEN008:Microsoft.UI.Reactor.Wrappers.WrapConvert(""Nope"")|}]
partial class CtlElement { }
");

    [Fact]
    public Task Property_Without_Single_Arg_Ctor_Errors() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Ctl))]
[{|REACTORGEN008:Microsoft.UI.Reactor.Wrappers.WrapConvert(""Pl"")|}]
partial class CtlElement { }
");

    [Fact]
    public Task Recognized_For_Descriptor_Only_Trigger() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorDescriptor(typeof(Ctl))]
[Microsoft.UI.Reactor.Wrappers.WrapConvert(""Cr"")]
partial class CtlElement { }
");
}

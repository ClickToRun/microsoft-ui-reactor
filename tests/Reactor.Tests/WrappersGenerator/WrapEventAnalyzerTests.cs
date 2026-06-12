using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.UI.Reactor.Wrappers.Generator;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.WrappersGenerator;

/// <summary>
/// Tests for <see cref="WrapEventAnalyzer"/> — REACTORGEN009 (the
/// <c>[WrapEvent]</c> event name is not a public event of the control) and
/// REACTORGEN010 (an <c>Arg</c>/<c>Args</c> entry is not a public property of the
/// event's argument type). Catches author typos at the attribute site instead of
/// as a cryptic generated-code error.
/// </summary>
public class WrapEventAnalyzerTests
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
    internal sealed class WrapEventAttribute : Attribute
    {
        public WrapEventAttribute(string eventName) { }
        public string? Arg { get; set; }
        public string[]? Args { get; set; }
    }
}
public sealed class FailedArgs { public string Error { get; set; } public int Code { get; set; } }
#pragma warning disable CS0067
public class Ctl
{
    public event EventHandler<FailedArgs> Failed;
}
#pragma warning restore CS0067
";

    private static Task Verify(string body, params DiagnosticResult[] expected)
    {
        var test = new CSharpAnalyzerTest<WrapEventAnalyzer, DefaultVerifier> { TestCode = Stubs + body };
        test.ExpectedDiagnostics.AddRange(expected);
        return test.RunAsync();
    }

    [Fact]
    public Task Valid_Event_And_Arg_Is_Clean() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Ctl))]
[Microsoft.UI.Reactor.Wrappers.WrapEvent(""Failed"", Arg = ""Error"")]
partial class CtlElement { }
");

    [Fact]
    public Task WholeArgs_Event_Without_Arg_Is_Clean() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Ctl))]
[Microsoft.UI.Reactor.Wrappers.WrapEvent(""Failed"")]
partial class CtlElement { }
");

    [Fact]
    public Task Unknown_Event_Errors_REACTORGEN009() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Ctl))]
[{|REACTORGEN009:Microsoft.UI.Reactor.Wrappers.WrapEvent(""Nope"")|}]
partial class CtlElement { }
");

    [Fact]
    public Task Unknown_Arg_Property_Errors_REACTORGEN010() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Ctl))]
[{|REACTORGEN010:Microsoft.UI.Reactor.Wrappers.WrapEvent(""Failed"", Arg = ""Eror"")|}]
partial class CtlElement { }
");

    [Fact]
    public Task Unknown_Entry_In_Args_Array_Errors_REACTORGEN010() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Ctl))]
[{|REACTORGEN010:Microsoft.UI.Reactor.Wrappers.WrapEvent(""Failed"", Args = new[] { ""Error"", ""Missing"" })|}]
partial class CtlElement { }
");

    [Fact]
    public Task Recognized_For_Descriptor_Only_Trigger() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorDescriptor(typeof(Ctl))]
[{|REACTORGEN009:Microsoft.UI.Reactor.Wrappers.WrapEvent(""Ghost"")|}]
partial class CtlElement { }
");
}

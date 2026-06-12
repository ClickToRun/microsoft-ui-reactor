using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.UI.Reactor.Wrappers.Generator;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.WrappersGenerator;

/// <summary>
/// Tests for <see cref="WrapperIncludeExcludeAnalyzer"/> (REACTORGEN002), which
/// errors when an <c>Include</c>/<c>Exclude</c> entry on
/// <c>[GenerateReactorWrapper]</c> names a property the control doesn't have.
/// Stubs the marker attribute + a control so no Reactor/WinUI reference is
/// needed. The attribute is fully qualified at each use site so it follows the
/// stub declarations without a misplaced <c>using</c>.
/// </summary>
public class WrapperIncludeExcludeAnalyzerTests
{
    private const string Stubs = @"
using System;
namespace Microsoft.UI.Reactor.Wrappers
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    internal sealed class GenerateReactorWrapperAttribute : Attribute
    {
        public GenerateReactorWrapperAttribute(Type controlType) { }
        public bool AutoDiscover { get; set; }
        public string[] Include { get; set; }
        public string[] Exclude { get; set; }
    }
}
public class BaseControl { public string Inherited { get; set; } }
public class FakeControl : BaseControl
{
    public string Header { get; set; }
    public bool IsActive { get; set; }
}
";

    private static Task Verify(string body, params DiagnosticResult[] expected)
    {
        var test = new CSharpAnalyzerTest<WrapperIncludeExcludeAnalyzer, DefaultVerifier>
        {
            TestCode = Stubs + body,
        };
        test.ExpectedDiagnostics.AddRange(expected);
        return test.RunAsync();
    }

    [Fact]
    public Task Invalid_Exclude_Name_Errors() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(FakeControl), Exclude = new[] { {|REACTORGEN002:""Bogus""|} })]
partial class FakeControlElement { }
");

    [Fact]
    public Task Invalid_Include_Name_Errors() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(FakeControl), AutoDiscover = false, Include = new[] { {|REACTORGEN002:""Nope""|} })]
partial class FakeControlElement { }
");

    [Fact]
    public Task Valid_Exclude_Name_Is_Clean() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(FakeControl), Exclude = new[] { ""Header"" })]
partial class FakeControlElement { }
");

    [Fact]
    public Task Inherited_Property_Name_Is_Valid() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(FakeControl), Exclude = new[] { ""Inherited"" })]
partial class FakeControlElement { }
");

    [Fact]
    public Task NameOf_Resolves_To_Valid_Property() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(FakeControl), Exclude = new[] { nameof(FakeControl.Header) })]
partial class FakeControlElement { }
");

    [Fact]
    public Task Mixed_Valid_And_Invalid_Flags_Only_Invalid() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(FakeControl), Exclude = new[] { ""Header"", {|REACTORGEN002:""Typo""|}, ""IsActive"" })]
partial class FakeControlElement { }
");
}

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.UI.Reactor.Wrappers.Generator;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.WrappersGenerator;

/// <summary>
/// Tests for <see cref="WrapControlledAnalyzer"/> (REACTORGEN003 / REACTORGEN004),
/// which validates <c>[WrapControlled("Prop", ChangedEvent = "Event")]</c> overrides
/// against the wrapped control. Stubs both marker attributes + a control.
/// </summary>
public class WrapControlledAnalyzerTests
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
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
    internal sealed class WrapControlledAttribute : Attribute
    {
        public WrapControlledAttribute(string property) { Property = property; }
        public string Property { get; }
        public string ChangedEvent { get; set; }
        public string[] Events { get; set; }
    }
}
public delegate void Handler(object sender, object args);
public class Toggle
{
    public bool IsOn { get; set; }
    public bool? IsChecked { get; set; }
    public double Value { get; set; }
#pragma warning disable CS0067
    public event Handler Toggled;
    public event Handler ValueChanged;
    public event Handler Checked;
    public event Handler Unchecked;
#pragma warning restore CS0067
}
";

    private static Task Verify(string body, params DiagnosticResult[] expected)
    {
        var test = new CSharpAnalyzerTest<WrapControlledAnalyzer, DefaultVerifier>
        {
            TestCode = Stubs + body,
        };
        test.ExpectedDiagnostics.AddRange(expected);
        return test.RunAsync();
    }

    [Fact]
    public Task Unknown_Property_Errors() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Toggle))]
[{|REACTORGEN003:Microsoft.UI.Reactor.Wrappers.WrapControlled(""Nope"")|}]
partial class ToggleElement { }
");

    [Fact]
    public Task Unknown_ChangedEvent_Errors() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Toggle))]
[{|REACTORGEN004:Microsoft.UI.Reactor.Wrappers.WrapControlled(""IsOn"", ChangedEvent = ""Bogus"")|}]
partial class ToggleElement { }
");

    [Fact]
    public Task Explicit_Event_Override_Is_Clean() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Toggle))]
[Microsoft.UI.Reactor.Wrappers.WrapControlled(""IsOn"", ChangedEvent = ""Toggled"")]
partial class ToggleElement { }
");

    [Fact]
    public Task Default_Convention_Event_Is_Clean() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Toggle))]
[Microsoft.UI.Reactor.Wrappers.WrapControlled(""Value"")]
partial class ToggleElement { }
");

    [Fact]
    public Task Multi_Event_List_Is_Clean() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Toggle))]
[Microsoft.UI.Reactor.Wrappers.WrapControlled(""IsChecked"", Events = new[] { ""Checked"", ""Unchecked"" })]
partial class ToggleElement { }
");

    [Fact]
    public Task Multi_Event_With_Bad_Entry_Errors() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Toggle))]
[{|REACTORGEN004:Microsoft.UI.Reactor.Wrappers.WrapControlled(""IsChecked"", Events = new[] { ""Checked"", ""Nope"" })|}]
partial class ToggleElement { }
");
}

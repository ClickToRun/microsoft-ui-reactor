using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.UI.Reactor.Wrappers.Generator;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.WrappersGenerator;

/// <summary>
/// Tests for <see cref="WrapElementSlotAnalyzer"/> (REACTORGEN013 / REACTORGEN014 /
/// REACTORGEN015), which validate that a <c>[WrapElementSlot]</c> target control property
/// exists, is public-settable, is assignable from a mounted UIElement, and that the
/// element-facing slot name is a valid C# identifier.
/// </summary>
public class WrapElementSlotAnalyzerTests
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
    internal sealed class WrapElementSlotAttribute : Attribute
    {
        public WrapElementSlotAttribute(string property) { }
        public string? ControlProperty { get; set; }
    }
}
namespace Microsoft.UI.Xaml { public class UIElement { } }
namespace Microsoft.UI.Xaml.Controls
{
    using Microsoft.UI.Xaml;
    public class IconElement : UIElement { }
    public class Holder
    {
        public IconElement HeaderIcon { get; set; }
        public object Banner { get; set; }
        public string Title { get; set; }
        public IconElement ReadOnlyIcon { get; }
    }
}
";

    private static Task Verify(string body, params DiagnosticResult[] expected)
    {
        var test = new CSharpAnalyzerTest<WrapElementSlotAnalyzer, DefaultVerifier> { TestCode = Stubs + body };
        test.ExpectedDiagnostics.AddRange(expected);
        return test.RunAsync();
    }

    [Fact]
    public Task Element_Typed_Property_Is_Clean() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Microsoft.UI.Xaml.Controls.Holder))]
[Microsoft.UI.Reactor.Wrappers.WrapElementSlot(""HeaderIcon"")]
partial class HolderElement { }
");

    [Fact]
    public Task Object_Typed_Property_Is_Clean() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorDescriptor(typeof(Microsoft.UI.Xaml.Controls.Holder))]
[Microsoft.UI.Reactor.Wrappers.WrapElementSlot(""Banner"")]
partial class HolderElement { }
");

    [Fact]
    public Task ControlProperty_Mapping_Is_Clean() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Microsoft.UI.Xaml.Controls.Holder))]
[Microsoft.UI.Reactor.Wrappers.WrapElementSlot(""Glyph"", ControlProperty = ""HeaderIcon"")]
partial class HolderElement { }
");

    [Fact]
    public Task Unknown_Property_Errors() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Microsoft.UI.Xaml.Controls.Holder))]
[{|REACTORGEN013:Microsoft.UI.Reactor.Wrappers.WrapElementSlot(""Nope"")|}]
partial class HolderElement { }
");

    [Fact]
    public Task ReadOnly_Property_Errors() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Microsoft.UI.Xaml.Controls.Holder))]
[{|REACTORGEN013:Microsoft.UI.Reactor.Wrappers.WrapElementSlot(""ReadOnlyIcon"")|}]
partial class HolderElement { }
");

    [Fact]
    public Task Non_Element_Typed_Property_Errors() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Microsoft.UI.Xaml.Controls.Holder))]
[{|REACTORGEN014:Microsoft.UI.Reactor.Wrappers.WrapElementSlot(""Title"")|}]
partial class HolderElement { }
");

    [Fact]
    public Task Invalid_Identifier_Slot_Name_Errors() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Microsoft.UI.Xaml.Controls.Holder))]
[{|REACTORGEN015:Microsoft.UI.Reactor.Wrappers.WrapElementSlot(""Bad-Name"", ControlProperty = ""HeaderIcon"")|}]
partial class HolderElement { }
");

    [Fact]
    public Task Keyword_Slot_Name_Errors() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Microsoft.UI.Xaml.Controls.Holder))]
[{|REACTORGEN015:Microsoft.UI.Reactor.Wrappers.WrapElementSlot(""class"", ControlProperty = ""HeaderIcon"")|}]
partial class HolderElement { }
");

    [Fact]
    public Task Contextual_Keyword_Slot_Name_Is_Clean() => Verify(@"
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Microsoft.UI.Xaml.Controls.Holder))]
[Microsoft.UI.Reactor.Wrappers.WrapElementSlot(""value"", ControlProperty = ""HeaderIcon"")]
partial class HolderElement { }
");
}

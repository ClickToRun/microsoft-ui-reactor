using Microsoft.UI.Reactor.Analyzers;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.AnalyzerTests;

/// <summary>
/// Tests for <see cref="ComponentInpcAnalyzer"/> (<c>REACTOR_STATE_001</c>). Stubs a
/// minimal <c>Microsoft.UI.Reactor.Core.Component</c> shape so the analyzer's two-condition
/// symbol match (derives from <c>Component</c> and implements <c>INotifyPropertyChanged</c>)
/// resolves without pulling the framework in. <c>INotifyPropertyChanged</c> and the near-miss
/// <c>System.ComponentModel.Component</c> come from the default reference assemblies and are
/// fully qualified so they never collide with the stubbed Reactor <c>Component</c>.
/// </summary>
public class ComponentInpcAnalyzerTests
{
    // Mirrors the real base shape: an abstract Component plus the generic Component<TProps>
    // (which derives from the non-generic Component, exactly as in src/Reactor/Core/Component.cs).
    // The `using` sits at the top of the compilation unit so the global-namespace test types
    // below can name `Component` while the namespace block declares it.
    private const string Stubs = @"
using Microsoft.UI.Reactor.Core;

namespace Microsoft.UI.Reactor.Core
{
    public abstract class Component { }
    public abstract class Component<TProps> : Component { }
}
";

    [Fact]
    public async Task Fires_For_Component_Implementing_Inpc()
    {
        // The XAML habit: a Component subclass raising PropertyChanged for local state.
        var source = Stubs + @"
class {|REACTOR_STATE_001:MyComponent|} : Component, System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
}";

        await new CSharpAnalyzerTest<ComponentInpcAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_For_Generic_Component_Subclass()
    {
        // Component<TProps> derives from Component, so the generic base must also trip the rule.
        var source = Stubs + @"
class MyProps { }

class {|REACTOR_STATE_001:MyComponent|} : Component<MyProps>, System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
}";

        await new CSharpAnalyzerTest<ComponentInpcAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task No_Diagnostic_For_Component_Without_Inpc()
    {
        // Negative: a plain Component with no INotifyPropertyChanged is the idiomatic shape.
        var source = Stubs + @"
class MyComponent : Component
{
}";

        await new CSharpAnalyzerTest<ComponentInpcAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task No_Diagnostic_For_Plain_Inpc_ViewModel()
    {
        // Negative: a real MVVM view-model implementing INPC but not deriving Component
        // is exactly what UseObservable is meant to consume — never flag it.
        var source = Stubs + @"
class MyViewModel : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
}";

        await new CSharpAnalyzerTest<ComponentInpcAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task No_Diagnostic_For_NonReactor_Component_Lookalike()
    {
        // Near-miss: derives from a class literally named 'Component' AND implements INPC,
        // but it is System.ComponentModel.Component — not Reactor's. The namespace-qualified
        // symbol match (not a name match) must keep this quiet.
        var source = Stubs + @"
class MyThing : System.ComponentModel.Component, System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
}";

        await new CSharpAnalyzerTest<ComponentInpcAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Reports_Once_On_Base_Not_Cascaded_To_Derived()
    {
        // When a base Component introduces INPC, only the base (the mistake site) is flagged;
        // a derived type that merely inherits INPC must not produce a duplicate diagnostic.
        var source = Stubs + @"
class {|REACTOR_STATE_001:MyBase|} : Component, System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
}

class MyDerived : MyBase
{
}";

        await new CSharpAnalyzerTest<ComponentInpcAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }
}

// Spec-048 §3.4 test bootstrap.
//
// `Reconciler.RegisterV1BuiltInHandlers()` was removed so the trimmer can drop
// unreferenced WinUI controls in shipping apps. Production code is expected to
// either (a) call a factory (e.g. `TextBlock("hi")`) which auto-registers via
// its closed-generic `Reg<>` cctor latch, (b) call `ControlRegistry.Register<,>`
// explicitly, or (c) opt into the whole catalog with the public
// `ReactorApp.RegisterAllBuiltIns()` (spec-048 §3.4 option A, issue #486).
//
// Test assemblies, however, exercise direct-record-ctor patterns extensively
// (`new TextBlockElement("hi")` — see issue #486). Forcing every test to call
// a factory first would be invasive and would mask genuine "missing handler"
// regressions. Instead, this file registers every built-in handler globally via
// a `[ModuleInitializer]` that simply delegates to the public
// `ReactorApp.RegisterAllBuiltIns()` — so the catalog list lives in exactly one
// place (`src/Reactor/Hosting/ReactorApp.BuiltIns.cs`) and the test bootstrap
// can never drift out of sync with production.
//
// Using a `[ModuleInitializer]` here (rooted in the *test* assembly) is allowed:
// the spec only forbids `[ModuleInitializer]` in the shipping `Reactor.dll`,
// where it would unconditionally root every handler and defeat trimming.

using System.Runtime.CompilerServices;
using Microsoft.UI.Reactor;

namespace Reactor.Tests.Bootstrap;

internal static class BuiltInHandlerBootstrap
{
    [ModuleInitializer]
    internal static void Initialize() => ReactorApp.RegisterAllBuiltIns();
}

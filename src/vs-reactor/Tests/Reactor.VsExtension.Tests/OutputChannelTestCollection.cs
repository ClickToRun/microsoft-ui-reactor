#nullable enable

using Xunit;

namespace Reactor.VsExtension.Tests
{
    // OutputChannel (and therefore SafeAsync, which logs through it) is a process-global mutable
    // static. xUnit runs distinct test classes in parallel by default, so any test class that
    // installs an OutputChannel test sink — or exercises code that logs through OutputChannel —
    // must NOT run concurrently with another such class, or one class's writes bleed into the
    // other's sink (and race on the sink's accumulator). Placing every OutputChannel-touching
    // class in this single, parallelization-disabled collection serializes them.
    //
    // Mirrors the existing "JobObjectCounterTests" collection, which serializes the classes that
    // share JobObject's global alive-counter static.
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class OutputChannelTestCollection
    {
        public const string Name = "OutputChannelTests";
    }
}

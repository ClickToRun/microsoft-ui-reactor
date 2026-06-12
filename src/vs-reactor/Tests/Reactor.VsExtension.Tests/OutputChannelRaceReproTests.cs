#nullable enable

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Reactor.VsExtension.Logging;
using Xunit;

namespace Reactor.VsExtension.Tests
{
    // Reproduction for the flaky LoggingTests failure
    // (LoggingTests.SafeAsync_AsyncRun_NoGetAwaiterGetResult_OnUiThread timing out on
    // `Assert.True(lines.Count >= 2)`).
    //
    // OutputChannel is a process-global mutable static and SafeAsync.Run's exception path
    // always logs to it. Because xUnit runs different test classes in parallel by default,
    // logging originating in OTHER test classes (EmbedSessionTests, command code paths) bleeds
    // into the test sink that LoggingTests installs. The LoggingTests sink accumulates into a
    // non-thread-safe List<string>, so concurrent writes race on List.Add — lost updates leave
    // the captured count BELOW what was written, which is exactly the timeout the CI hit.
    //
    // This test deterministically forces that concurrency and asserts no lines are lost. It is
    // RED with a naive List<string> sink (IndexOutOfRangeException / lost updates) and GREEN once
    // the sink is made thread-safe — guarding against a regression of the flaky behavior.
    [Collection(OutputChannelTestCollection.Name)]
    public sealed class OutputChannelRaceReproTests
    {
        [Fact]
        public async Task OutputChannel_ConcurrentWrites_DoNotLoseLines()
        {
            const int writers = 64;
            const int perWriter = 16;
            const int expected = writers * perWriter;

            OutputChannel.ResetForTest();

            var sink = new ConcurrentQueue<string>();
            OutputChannel.InitializeForTest(async line =>
            {
                await Task.Yield();
                sink.Enqueue(line);
            });

            var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
            {
                for (var i = 0; i < perWriter; i++)
                {
                    await OutputChannel.WriteLineAsync($"w{w}-{i}");
                }
            }));

            await Task.WhenAll(tasks);

            await EventuallyAsync(() => sink.Count >= expected);

            Assert.Equal(expected, sink.Count);
        }

        private static async Task EventuallyAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(10);
            }

            Assert.True(condition());
        }
    }
}

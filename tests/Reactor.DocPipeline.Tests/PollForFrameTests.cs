using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.UI.Reactor.Cli.Docs;
using Xunit;

namespace Microsoft.UI.Reactor.Cli.Docs.Tests;

/// <summary>
/// Covers <see cref="ScreenshotCapture.PollForFrame"/>'s
/// <c>requireContent</c> hold-out.
/// </summary>
/// <remarks>
/// <para>
/// This is the branch that turns issue #989's mechanism from a corrupt commit
/// into a correct capture: the capture server starts its frame timer lazily and
/// a cold WinUI window's first delivered frame is routinely the unpainted
/// surface. Accepting it — which is what the code did before — writes a
/// solid-white PNG over a good committed screenshot and exits 0.
/// </para>
/// <para>
/// Driven over a real loopback socket rather than a mocked <c>HttpClient</c>
/// because the thing under test is a polling loop over HTTP responses; a fake
/// that returns byte arrays directly would skip the status-code and
/// empty-body handling that sit in the same loop. A raw
/// <see cref="TcpListener"/> is used rather than <c>HttpListener</c> so the
/// test needs no URL ACL reservation on Windows.
/// </para>
/// </remarks>
public class PollForFrameTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    private readonly ITestOutputHelper _output;

    public PollForFrameTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The whole point of the hold-out: a blank first frame must not be what
    /// gets written. Without <c>requireContent</c> the same server returns the
    /// blank frame, which is asserted below as the differential control — so
    /// this pair fails if the branch is deleted in either direction.
    /// </summary>
    [Fact]
    public async Task Blank_frames_are_skipped_until_a_painted_one_arrives()
    {
        var blank = SolidPng(60, 40, Color.White);
        var painted = PaintedPng(60, 40);

        using var server = new FrameServer([blank, blank, painted]);
        using var http = new HttpClient();

        var got = await ScreenshotCapture.PollForFrame(http, server.Port, Deadline, requireContent: true);

        _output.WriteLine($"accepted={server.Accepted} served={server.Served}");
        Assert.Null(server.Fault);
        Assert.Equal(painted, got);
        Assert.NotEqual(blank, got);
        // Exactly three requests: two blanks held out, then the painted frame.
        // Keyed on requests, not connections, so a stray probe connection
        // cannot shift the sequence.
        Assert.Equal(3, server.Served);
    }

    /// <summary>
    /// Differential control for the test above. Same server, same frames, one
    /// different argument — opposite answer. If the hold-out were removed the
    /// two tests would agree, and this one is what notices.
    /// </summary>
    [Fact]
    public async Task Without_the_hold_out_the_first_frame_wins_even_when_blank()
    {
        var blank = SolidPng(60, 40, Color.White);
        var painted = PaintedPng(60, 40);

        using var server = new FrameServer([blank, blank, painted]);
        using var http = new HttpClient();

        var got = await ScreenshotCapture.PollForFrame(http, server.Port, Deadline, requireContent: false);

        Assert.Null(server.Fault);
        Assert.Equal(blank, got);
    }

    /// <summary>
    /// When every frame is blank the last one is still returned, so the caller
    /// hits <c>BlankFrameException</c> — an accurate "the window never painted"
    /// — rather than an empty array reported as "no frame produced", which
    /// would send the reader looking at the transport instead of the app.
    /// </summary>
    [Fact]
    public async Task A_deadline_of_only_blank_frames_returns_the_last_blank_frame()
    {
        var blank = SolidPng(60, 40, Color.White);

        using var server = new FrameServer([blank]);
        using var http = new HttpClient();

        var got = await ScreenshotCapture.PollForFrame(
            http, server.Port, TimeSpan.FromMilliseconds(600), requireContent: true);

        Assert.Null(server.Fault);
        Assert.Equal(blank, got);
        Assert.NotEmpty(got);
        Assert.Throws<BlankFrameException>(
            () => ImageProcessor.Process(got, ImageProcessor.ParseCropMode("content")));
    }

    /// <summary>
    /// 204/empty responses are the server's "timer hasn't started yet" reply and
    /// must not be mistaken for a frame.
    /// </summary>
    [Fact]
    public async Task Empty_responses_are_not_treated_as_frames()
    {
        var painted = PaintedPng(60, 40);

        using var server = new FrameServer([[], [], painted]);
        using var http = new HttpClient();

        var got = await ScreenshotCapture.PollForFrame(http, server.Port, Deadline, requireContent: true);

        Assert.Null(server.Fault);
        Assert.Equal(painted, got);
    }

    private static byte[] SolidPng(int w, int h, Color color)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.Clear(color);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static byte[] PaintedPng(int w, int h)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var ink = new SolidBrush(Color.FromArgb(20, 20, 20));
            g.FillRectangle(ink, w / 4, h / 4, w / 2, h / 2);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>
    /// Minimal HTTP/1.1 server that answers each <c>GET</c> with the next body in
    /// a scripted sequence, sticking on the last one.
    /// </summary>
    /// <remarks>
    /// A body slot is consumed only once a complete request head (<c>\r\n\r\n</c>)
    /// has actually been read. An earlier version keyed the response on the
    /// accepted-connection ordinal instead, and any connection that carried no
    /// request — <c>localhost</c> resolves to both <c>::1</c> and
    /// <c>127.0.0.1</c>, so the client's happy-eyeballs probing can leave one —
    /// silently shifted the whole sequence by one. That made the fixture report
    /// "the poller accepted a blank frame" when the poller had done nothing
    /// wrong. Keying on a parsed request removes the ambiguity: a connection
    /// with no request gets no body and advances nothing.
    /// </remarks>
    private sealed class FrameServer : global::System.IDisposable
    {
        private readonly TcpListener _listener;
        private readonly IReadOnlyList<byte[]> _bodies;
        private readonly CancellationTokenSource _cts = new();
        private int _served;
        private int _accepted;
        private Exception? _fault;

        public FrameServer(IReadOnlyList<byte[]> bodies)
        {
            _bodies = bodies;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoop);
        }

        public int Port { get; }

        /// <summary>Number of complete requests answered, not connections accepted.</summary>
        public int Served => Volatile.Read(ref _served);

        /// <summary>
        /// Connections accepted. Reported alongside <see cref="Served"/> so a
        /// future failure shows its own input: if these differ, the client
        /// opened a connection that carried no request and the old
        /// ordinal-keyed fixture would have mis-served the sequence.
        /// </summary>
        public int Accepted => Volatile.Read(ref _accepted);

        /// <summary>
        /// First exception the accept loop hit that was <em>not</em> explained by
        /// teardown, or null. The loop runs detached on a background task, so
        /// without this a genuine socket/IO fault would be swallowed and the test
        /// would report only the downstream symptom — a served count that is
        /// mysteriously short — with no trace of the cause. Tests assert this is
        /// null, which is what makes the shutdown catches safe to keep quiet.
        /// </summary>
        public Exception? Fault => Volatile.Read(ref _fault);

        private async Task AcceptLoop()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    using var stream = client.GetStream();
                    Interlocked.Increment(ref _accepted);

                    if (!await ReadRequestHead(stream)) continue;

                    var index = Interlocked.Increment(ref _served) - 1;
                    var body = _bodies[global::System.Math.Min(index, _bodies.Count - 1)];

                    var header = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\n" +
                        "Content-Type: image/png\r\n" +
                        $"Content-Length: {body.Length}\r\n" +
                        "Connection: close\r\n\r\n");
                    await stream.WriteAsync(header, _cts.Token);
                    if (body.Length > 0) await stream.WriteAsync(body, _cts.Token);
                    await stream.FlushAsync(_cts.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException
                                          or global::System.IO.IOException
                                          or global::System.ObjectDisposedException)
            {
                // These four are exactly what Dispose() produces: cancelling the
                // token, then stopping the listener, races the pending accept and
                // any in-flight write. Silent only when teardown explains them —
                // outside teardown the same exception means a real transport
                // failure, and the loop is detached, so it is recorded for the
                // test to assert on rather than dropped.
                if (!_cts.IsCancellationRequested)
                    Interlocked.CompareExchange(ref _fault, ex, null);
            }
        }

        /// <summary>
        /// Reads until the end of the request head. False when the peer closed
        /// without sending one, which must not consume a body slot.
        /// </summary>
        private async Task<bool> ReadRequestHead(NetworkStream stream)
        {
            var buf = new byte[1024];
            var head = new StringBuilder();
            while (!_cts.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buf, _cts.Token);
                if (read == 0) return false;
                head.Append(Encoding.ASCII.GetString(buf, 0, read));
                if (head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }
}

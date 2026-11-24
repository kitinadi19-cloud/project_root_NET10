using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using TcpBench.Server;
using Xunit;

namespace TcpBench.Tests;

public class EchoServerTests : IAsyncLifetime
{
    private EchoServer _server = null!;

    public Task InitializeAsync()
    {
        _server = new EchoServer(IPAddress.Loopback, 0, idleTimeoutMs: 5000);
        _server.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    // ---------- T1: một Client ----------
    [Fact]
    public async Task SingleClient_ReceivesSameFrame()
    {
        using var c = await ConnectAsync(_server.Port);
        var s = c.GetStream();
        var frame = MakeFrame(42, RandomBytes(1024));
        await s.WriteAsync(frame);
        var resp = new byte[frame.Length];
        await ReadExactAsync(s, resp);
        Assert.Equal(frame, resp);                 // đúng requestId và payload
    }

    // ---------- T2: nhiều Client đồng thời ----------
    [Fact]
    public async Task ManyConcurrentClients_AllResponsesCorrect()
    {
        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            using var c = await ConnectAsync(_server.Port);
            var s = c.GetStream();
            for (int k = 0; k < 50; k++)
            {
                var frame = MakeFrame(((long)i << 32) | (uint)k, RandomBytes(1024));
                await s.WriteAsync(frame);
                var resp = new byte[frame.Length];
                await ReadExactAsync(s, resp);
                Assert.Equal(frame, resp);
            }
        });
        await Task.WhenAll(tasks);
        Assert.Equal(20 * 50, _server.Requests);
    }

    // ---------- T3: message bị chia nhỏ (Receive trả về thiếu) ----------
    [Fact]
    public async Task FragmentedMessage_IsReassembled()
    {
        using var c = await ConnectAsync(_server.Port);
        var s = c.GetStream();
        var frame = MakeFrame(7, RandomBytes(300));
        await s.WriteAsync(frame.AsMemory(0, 2));            // chia ngay giữa phần header
        await Task.Delay(50);
        await s.WriteAsync(frame.AsMemory(2, 5));
        await Task.Delay(50);
        await s.WriteAsync(frame.AsMemory(7));
        var resp = new byte[frame.Length];
        await ReadExactAsync(s, resp);
        Assert.Equal(frame, resp);
    }

    // ---------- T4: hai message dính liền trong một lần gửi ----------
    [Fact]
    public async Task TwoMessagesInOneWrite_BothEchoed()
    {
        using var c = await ConnectAsync(_server.Port);
        var s = c.GetStream();
        var f1 = MakeFrame(1, RandomBytes(100));
        var f2 = MakeFrame(2, RandomBytes(200));
        await s.WriteAsync(f1.Concat(f2).ToArray());
        var r1 = new byte[f1.Length];
        var r2 = new byte[f2.Length];
        await ReadExactAsync(s, r1);
        await ReadExactAsync(s, r2);
        Assert.Equal(f1, r1);
        Assert.Equal(f2, r2);
    }

    // ---------- T5: Client đóng kết nối bất thường giữa message ----------
    [Fact]
    public async Task AbruptDisconnect_ServerKeepsServing()
    {
        using (var bad = await ConnectAsync(_server.Port))
        {
            var frame = MakeFrame(1, RandomBytes(1000));
            await bad.GetStream().WriteAsync(frame.AsMemory(0, 100));   // gửi dở
            bad.Client.LingerState = new LingerOption(true, 0);         // đóng đột ngột (RST)
        }
        await Task.Delay(200);
        await AssertServerStillWorksAsync(_server);
    }

    // ---------- T6: dữ liệu không hợp lệ / vượt giới hạn ----------
    [Theory]
    [InlineData(0u)]
    [InlineData(7u)]                  // nhỏ hơn kích thước requestId
    [InlineData(65537u)]              // vượt MaxBodySize
    [InlineData(0xFFFFFFFFu)]
    public async Task InvalidLength_ConnectionClosed_ServerKeepsServing(uint badLen)
    {
        using var c = await ConnectAsync(_server.Port);
        var s = c.GetStream();
        var hdr = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(hdr, badLen);
        await s.WriteAsync(hdr);

        Assert.True(await IsClosedByServerAsync(s, 3000));
        Assert.Equal(1, _server.ProtocolErrors);
        await AssertServerStillWorksAsync(_server);
    }

    // ---------- T7: frame lớn đúng giới hạn vẫn hợp lệ ----------
    [Fact]
    public async Task MaxSizeFrame_IsEchoed()
    {
        using var c = await ConnectAsync(_server.Port);
        var s = c.GetStream();
        var frame = MakeFrame(9, RandomBytes(Protocol.MaxBodySize - Protocol.IdSize));
        await s.WriteAsync(frame);
        var resp = new byte[frame.Length];
        await ReadExactAsync(s, resp);
        Assert.Equal(frame, resp);
    }

    // ---------- T8: timeout khi Client im lặng ----------
    [Fact]
    public async Task IdleClient_IsDisconnectedByTimeout()
    {
        await using var server = new EchoServer(IPAddress.Loopback, 0, idleTimeoutMs: 300);
        server.Start();
        using var c = await ConnectAsync(server.Port);
        Assert.True(await IsClosedByServerAsync(c.GetStream(), 3000));
        Assert.Equal(1, server.Timeouts);
    }

    // ---------------- helpers ----------------
    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        Random.Shared.NextBytes(b);
        return b;
    }

    private static byte[] MakeFrame(long id, byte[] payload)
    {
        var f = new byte[4 + 8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(f, (uint)(8 + payload.Length));
        BinaryPrimitives.WriteInt64BigEndian(f.AsSpan(4), id);
        payload.CopyTo(f, 12);
        return f;
    }

    private static async Task<TcpClient> ConnectAsync(int port)
    {
        var c = new TcpClient { NoDelay = true };
        await c.ConnectAsync(IPAddress.Loopback, port);
        return c;
    }

    private static async Task ReadExactAsync(NetworkStream s, byte[] buf)
    {
        using var cts = new CancellationTokenSource(5000);
        int off = 0;
        while (off < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(off), cts.Token);
            if (n == 0) throw new EndOfStreamException();
            off += n;
        }
    }

    private static async Task<bool> IsClosedByServerAsync(NetworkStream s, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try { return await s.ReadAsync(new byte[1], cts.Token) == 0; }
        catch (IOException) { return true; }                 // RST cũng nghĩa là server đã đóng
        catch (OperationCanceledException) { return false; } // hết thời gian mà chưa bị đóng
    }

    private static async Task AssertServerStillWorksAsync(EchoServer server)
    {
        using var c = await ConnectAsync(server.Port);
        var s = c.GetStream();
        var frame = MakeFrame(123, RandomBytes(1024));
        await s.WriteAsync(frame);
        var resp = new byte[frame.Length];
        await ReadExactAsync(s, resp);
        Assert.Equal(frame, resp);
    }
}

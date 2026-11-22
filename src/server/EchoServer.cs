using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace TcpBench.Server;

public static class Protocol
{
    public const int HeaderSize = 4;            // uint32 big-endian: độ dài body
    public const int IdSize = 8;                // int64 big-endian: requestId
    public const int MaxBodySize = 64 * 1024;   // body hợp lệ: IdSize..MaxBodySize
}

/// <summary>Echo server dùng asynchronous socket (async/await).</summary>
public sealed class EchoServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly int _idleTimeoutMs;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;
    private long _accepted, _requests, _protocolErrors, _ioErrors, _timeouts;

    public EchoServer(IPAddress ip, int port, int idleTimeoutMs = 10_000)
    {
        _listener = new TcpListener(ip, port);
        _idleTimeoutMs = idleTimeoutMs;
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public long Accepted => Interlocked.Read(ref _accepted);
    public long Requests => Interlocked.Read(ref _requests);
    public long ProtocolErrors => Interlocked.Read(ref _protocolErrors);
    public long IoErrors => Interlocked.Read(ref _ioErrors);
    public long Timeouts => Interlocked.Read(ref _timeouts);

    public void Start()
    {
        _listener.Start(1024);
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public string Snapshot() =>
        $"accepted={Accepted} requests={Requests} protocolErrors={ProtocolErrors} " +
        $"ioErrors={IoErrors} idleTimeouts={Timeouts}";

    private async Task AcceptLoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            Interlocked.Increment(ref _accepted);
            _ = Task.Run(() => HandleClientAsync(client, ct));   // mỗi kết nối một Task
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverCt)
    {
        using (client)
        {
            client.NoDelay = true;                                // tắt Nagle
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
            var buf = new byte[Protocol.HeaderSize + Protocol.MaxBodySize];
            try
            {
                var stream = client.GetStream();
                while (true)
                {
                    idleCts.CancelAfter(_idleTimeoutMs);          // timeout khi client im lặng

                    // 1) đọc đủ 4 byte độ dài; EOF sạch ở đây = client đã đóng kết nối
                    if (!await ReadExactAsync(stream, buf.AsMemory(0, Protocol.HeaderSize),
                                              allowCleanEof: true, idleCts.Token))
                        break;

                    uint len = BinaryPrimitives.ReadUInt32BigEndian(buf);
                    if (len < Protocol.IdSize || len > Protocol.MaxBodySize)
                    {
                        Interlocked.Increment(ref _protocolErrors);
                        break;                                    // dữ liệu không hợp lệ: chỉ đóng kết nối này
                    }

                    // 2) đọc đủ body (requestId + payload)
                    await ReadExactAsync(stream, buf.AsMemory(Protocol.HeaderSize, (int)len),
                                         allowCleanEof: false, idleCts.Token);
                    Interlocked.Increment(ref _requests);

                    // 3) echo nguyên frame trong một lần ghi
                    await stream.WriteAsync(buf.AsMemory(0, Protocol.HeaderSize + (int)len),
                                            idleCts.Token);
                }
            }
            catch (OperationCanceledException) when (!serverCt.IsCancellationRequested)
            {
                Interlocked.Increment(ref _timeouts);
            }
            catch (OperationCanceledException) { /* server đang tắt */ }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
                Interlocked.Increment(ref _ioErrors);             // client ngắt đột ngột (RST...)
            }
        }
    }

    /// <summary>Đọc đúng buf.Length byte; một lần Receive có thể trả về thiếu.</summary>
    private static async ValueTask<bool> ReadExactAsync(
        NetworkStream s, Memory<byte> buf, bool allowCleanEof, CancellationToken ct)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = await s.ReadAsync(buf.Slice(off), ct);
            if (n == 0)
            {
                if (off == 0 && allowCleanEof) return false;
                throw new IOException("Client đóng kết nối giữa chừng một message.");
            }
            off += n;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null) await _acceptLoop;
        _cts.Dispose();
    }
}
using System.Net;
using TcpBench.Server;

static string Arg(string[] a, string key, string def)
{
    int i = Array.IndexOf(a, key);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : def;
}

var ip = IPAddress.Parse(Arg(args, "--ip", "127.0.0.1"));
var port = int.Parse(Arg(args, "--port", "5000"));
var idleMs = int.Parse(Arg(args, "--idle-timeout-ms", "10000"));

await using var server = new EchoServer(ip, port, idleMs);
server.Start();
Console.WriteLine($"Echo server {ip}:{server.Port} (PID {Environment.ProcessId}). Ctrl+C để dừng.");

using var stop = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
stop.Wait();

Console.WriteLine(server.Snapshot());
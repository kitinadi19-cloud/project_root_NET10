using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;

var cfg = Config.Parse(args);
Console.WriteLine(cfg);

Process? serverProc = cfg.ServerPid > 0 ? Process.GetProcessById(cfg.ServerPid) : null;
Process self = Process.GetCurrentProcess();
long freq = Stopwatch.Frequency;

// Mốc thời gian chung: [warm-up] [cửa sổ đo]
long t0 = Stopwatch.GetTimestamp();
long measureStart = t0 + cfg.WarmupSec * freq;
long measureEnd = measureStart + cfg.DurationSec * freq;

var workers = Enumerable.Range(0, cfg.Clients)
    .Select(i => new ClientWorker(i, cfg, measureStart, measureEnd)).ToArray();
Task[] tasks = workers.Select(w => Task.Run(w.RunAsync)).ToArray();

// Lấy mẫu CPU đúng đầu/cuối cửa sổ đo
await Helpers.DelayUntil(measureStart);
TimeSpan srv0 = Helpers.Cpu(serverProc), gen0 = Helpers.Cpu(self);
long w0 = Stopwatch.GetTimestamp();
await Helpers.DelayUntil(measureEnd);
TimeSpan srv1 = Helpers.Cpu(serverProc), gen1 = Helpers.Cpu(self);
long w1 = Stopwatch.GetTimestamp();
await Task.WhenAll(tasks);

// ---- Tổng hợp ----
double wall = (double)(w1 - w0) / freq;
int cores = Environment.ProcessorCount;
double srvCore = serverProc is null ? double.NaN : (srv1 - srv0).TotalSeconds / wall * 100.0;
double genCore = (gen1 - gen0).TotalSeconds / wall * 100.0;

var all = workers.SelectMany(w => w.Samples).ToList();
double[] lat = all.Where(s => s.Status == Status.Ok)
                  .Select(s => s.LatencyTicks * 1000.0 / freq)
                  .OrderBy(x => x).ToArray();
int ok = lat.Length, errors = all.Count - ok;
int CountOf(Status st) => all.Count(s => s.Status == st);
double Pct(double p) => ok == 0 ? double.NaN : lat[(int)Math.Ceiling(p * ok) - 1];  // nearest-rank
double errRate = all.Count == 0 ? 0 : (double)errors / all.Count;
double throughput = (double)ok / cfg.DurationSec;
double mean = ok == 0 ? double.NaN : lat.Average();

static string F(double d, int digits = 3) =>
    double.IsNaN(d) ? "" : d.ToString("F" + digits, CultureInfo.InvariantCulture);

const string Header = "run_id,timestamp,clients,payload_bytes,warmup_s,duration_s,ok,errors," +
    "timeouts,mismatches,io_errors,error_rate,throughput_rps,mean_ms,p50_ms,p95_ms,p99_ms," +
    "max_ms,srv_cpu_core_pct,srv_cpu_machine_pct,gen_cpu_core_pct,cores";

string row = string.Join(',',
    cfg.RunId, DateTime.Now.ToString("s", CultureInfo.InvariantCulture), cfg.Clients,
    cfg.PayloadBytes, cfg.WarmupSec, cfg.DurationSec, ok, errors,
    CountOf(Status.Timeout), CountOf(Status.Mismatch), CountOf(Status.IoError),
    F(errRate, 6), F(throughput), F(mean), F(Pct(0.50)), F(Pct(0.95)), F(Pct(0.99)),
    F(ok == 0 ? double.NaN : lat[^1]), F(srvCore), F(srvCore / cores), F(genCore), cores);

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cfg.SummaryOut))!);
bool newFile = !File.Exists(cfg.SummaryOut);
using (var sw = new StreamWriter(cfg.SummaryOut, append: true))
{
    if (newFile) sw.WriteLine(Header);
    sw.WriteLine(row);
}

if (cfg.RawOut is not null)   // dữ liệu thô từng request, dùng cho verify.py
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cfg.RawOut))!);
    using var rw = new StreamWriter(cfg.RawOut, append: false);
    rw.WriteLine("run_id,client_id,request_id,send_offset_us,latency_us,status");
    foreach (var s in all.OrderBy(x => x.SendTicks))
        rw.WriteLine(string.Join(',', cfg.RunId, s.ClientId, s.RequestId,
            F((s.SendTicks - measureStart) * 1e6 / freq, 1),
            F(s.LatencyTicks * 1e6 / freq, 1), s.Status));
}

Console.WriteLine(Header);
Console.WriteLine(row);
return 0;

// ======================= Kiểu dữ liệu & lớp hỗ trợ =======================

enum Status : byte { Ok, Timeout, Mismatch, IoError }

readonly record struct Sample(int ClientId, long RequestId, long SendTicks,
                              long LatencyTicks, Status Status);

sealed record Config(string Ip, int Port, int Clients, int PayloadBytes, int WarmupSec,
    int DurationSec, int TimeoutMs, string RunId, string SummaryOut, string? RawOut,
    int ServerPid)
{
    public static Config Parse(string[] a)
    {
        string Get(string key, string def)
        {
            int i = Array.IndexOf(a, key);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : def;
        }
        int pid = int.Parse(Get("--server-pid", "0"));
        string srvName = Get("--server-name", "");          // ví dụ: TcpBench.Server
        if (pid == 0 && srvName != "")
            pid = Process.GetProcessesByName(srvName).FirstOrDefault()?.Id ?? 0;
        string raw = Get("--raw-out", "");

        var c = new Config(
            Get("--ip", "127.0.0.1"), int.Parse(Get("--port", "5000")),
            int.Parse(Get("--clients", "10")), int.Parse(Get("--payload-bytes", "1024")),
            int.Parse(Get("--warmup-s", "5")), int.Parse(Get("--duration-s", "20")),
            int.Parse(Get("--timeout-ms", "5000")),
            Get("--run-id", $"run_{DateTime.Now:yyyyMMdd_HHmmss}"),
            Get("--out", "experiments/raw-data/summary.csv"),
            raw == "" ? null : raw, pid);
        if (c.PayloadBytes < 1 || c.PayloadBytes > 64 * 1024 - 8)
            throw new ArgumentException("--payload-bytes phải nằm trong [1, 65528].");
        return c;
    }
}

static class Helpers
{
    public static async Task DelayUntil(long ts)
    {
        long remaining = ts - Stopwatch.GetTimestamp();
        if (remaining > 0)
            await Task.Delay(TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency));
    }

    public static TimeSpan Cpu(Process? p)
    {
        if (p is null) return TimeSpan.Zero;
        p.Refresh();
        return p.TotalProcessorTime;
    }
}

/// <summary>Một Client logic = một kết nối TCP riêng, vòng lặp closed-loop.</summary>
sealed class ClientWorker(int id, Config cfg, long mStart, long mEnd)
{
    public List<Sample> Samples { get; } = new();

    public async Task RunAsync()
    {
        const int Header = 4, IdSize = 8;
        byte[] frame = new byte[Header + IdSize + cfg.PayloadBytes];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)(IdSize + cfg.PayloadBytes));
        Random.Shared.NextBytes(frame.AsSpan(Header + IdSize));   // payload riêng mỗi client
        byte[] recv = new byte[frame.Length];

        long seq = 0;
        TcpClient? tcp = null;
        NetworkStream? ns = null;
        var cts = new CancellationTokenSource();
        try
        {
            while (Stopwatch.GetTimestamp() < mEnd)
            {
                if (ns is null)                                    // (re)connect
                {
                    long c0 = Stopwatch.GetTimestamp();
                    try
                    {
                        tcp = new TcpClient { NoDelay = true };
                        await tcp.ConnectAsync(cfg.Ip, cfg.Port);
                        ns = tcp.GetStream();
                    }
                    catch (SocketException)
                    {
                        tcp?.Dispose(); tcp = null;
                        Record(0, c0, Stopwatch.GetTimestamp() - c0, Status.IoError);
                        await Task.Delay(100);
                        continue;
                    }
                }

                long reqId = ((long)id << 40) | ++seq;              // duy nhất toàn hệ thống
                BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(Header, IdSize), reqId);

                long t1 = Stopwatch.GetTimestamp();
                var st = Status.Ok;
                try
                {
                    if (!cts.TryReset()) { cts.Dispose(); cts = new CancellationTokenSource(); }
                    cts.CancelAfter(cfg.TimeoutMs);
                    await ns.WriteAsync(frame, cts.Token);
                    await ReadExactAsync(ns, recv, cts.Token);
                    if (!recv.AsSpan().SequenceEqual(frame))        // kiểm tra requestId + payload
                        st = Status.Mismatch;
                }
                catch (OperationCanceledException) { st = Status.Timeout; }
                catch (Exception e) when (e is IOException or SocketException
                                              or ObjectDisposedException)
                { st = Status.IoError; }
                long t2 = Stopwatch.GetTimestamp();

                Record(reqId, t1, t2 - t1, st);
                if (st != Status.Ok)                                // lỗi: đóng và kết nối lại
                {
                    ns?.Dispose(); tcp?.Dispose(); ns = null; tcp = null;
                }
            }
        }
        finally { ns?.Dispose(); tcp?.Dispose(); cts.Dispose(); }
    }

    // Chỉ ghi nhận request nằm trọn trong cửa sổ đo (loại warm-up)
    private void Record(long reqId, long sendTs, long latTicks, Status st)
    {
        if (sendTs >= mStart && sendTs + latTicks < mEnd)
            Samples.Add(new Sample(id, reqId, sendTs, latTicks, st));
    }

    private static async Task ReadExactAsync(NetworkStream s, byte[] buf, CancellationToken ct)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(off), ct);
            if (n == 0) throw new IOException("Server đóng kết nối.");
            off += n;
        }
    }
}

// TV3: TCP Load Generator

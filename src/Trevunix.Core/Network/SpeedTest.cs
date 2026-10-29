using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Trevunix.Core.Network;

public sealed record SpeedTestServer(
    string City,
    string Country,
    string Colo,
    string Asn,
    string ClientIp
);

public enum SpeedPhase
{
    Latency,
    Download,
    Upload,
}

/// <param name="Mbps">Speed over the last second, for the live gauge.</param>
/// <param name="Fraction">How far the current phase is, 0 to 1.</param>
public sealed record SpeedProgress(SpeedPhase Phase, double Mbps, double Fraction);

/// <summary>Cloudflare is throttling this address after too many tests; try again in a few minutes.</summary>
public sealed class SpeedTestLimitedException()
    : Exception("Rate limited by the speed test server.");

public sealed record SpeedTestResult(
    SpeedTestServer? Server,
    double PingMs,
    double JitterMs,
    double DownloadMbps,
    double UploadMbps
);

/// <summary>Arithmetic for the speed test, kept apart so it can be tested without a network.</summary>
public static partial class SpeedMath
{
    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>Average change between one ping and the next.</summary>
    public static double Jitter(IReadOnlyList<double> values)
    {
        if (values.Count < 2)
            return 0;
        double sum = 0;
        for (var i = 1; i < values.Count; i++)
            sum += Math.Abs(values[i] - values[i - 1]);
        return sum / (values.Count - 1);
    }

    /// <summary>
    ///     Megabits per second from (seconds since start, total bytes so far) samples, ignoring the
    ///     first <paramref name="warmupSeconds" /> while TCP is still speeding up.
    /// </summary>
    public static double SteadyMbps(
        IReadOnlyList<(double Seconds, long Bytes)> samples,
        double warmupSeconds
    )
    {
        if (samples.Count < 2)
            return 0;

        var start = samples.FirstOrDefault(s => s.Seconds >= warmupSeconds);
        if (start == default || start == samples[^1])
            start = samples[0];
        var end = samples[^1];
        var seconds = end.Seconds - start.Seconds;
        return seconds <= 0 ? 0 : ToMbps(end.Bytes - start.Bytes, seconds);
    }

    /// <summary>
    ///     True when each of the last three one-second rates is within 5% of the others, which
    ///     means more measuring would not change the result.
    /// </summary>
    public static bool IsSteady(IReadOnlyList<(double Seconds, long Bytes)> samples)
    {
        if (samples.Count < 2)
            return false;
        var end = samples[^1].Seconds;
        var rates = new List<double>();
        for (var k = 3; k >= 1; k--)
        {
            var from = BytesAt(samples, end - k);
            var to = BytesAt(samples, end - k + 1);
            if (from is null || to is null)
                return false;
            rates.Add(to.Value - from.Value);
        }
        var min = rates.Min();
        return min > 0 && rates.Max() / min <= 1.05;
    }

    private static long? BytesAt(
        IReadOnlyList<(double Seconds, long Bytes)> samples,
        double seconds
    )
    {
        if (seconds < samples[0].Seconds - 0.01)
            return null;
        // The last sample taken at or before the time (samples come every 200 ms).
        var match = samples.LastOrDefault(s => s.Seconds <= seconds + 0.01);
        return match == default ? null : match.Bytes;
    }

    public static double ToMbps(long bytes, double seconds) =>
        seconds <= 0 ? 0 : bytes * 8 / seconds / 1_000_000;

    /// <summary>
    ///     Reads Cloudflare's Server-Timing header: the time the server itself spent (summed over
    ///     its "dur" entries) and, when present, the TCP round trip it measured on the connection
    ///     ("cfL4 ... rtt=" in microseconds), which is the most exact ping available.
    /// </summary>
    public static (double ServerMs, double? RttMs) ParseServerTiming(string? header)
    {
        if (string.IsNullOrEmpty(header))
            return (0, null);

        double server = 0;
        foreach (Match m in DurPattern().Matches(header))
            server += double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);

        var rtt = RttPattern().Match(header);
        double? rttMs = rtt.Success
            ? double.Parse(rtt.Groups[1].Value, CultureInfo.InvariantCulture) / 1000
            : null;
        return (server, rttMs);
    }

    [GeneratedRegex(@"dur=([\d.]+)")]
    private static partial Regex DurPattern();

    [GeneratedRegex(@"[?&;\s]rtt=(\d+)")]
    private static partial Regex RttPattern();
}

/// <summary>
///     Measures ping, jitter, download and upload against Cloudflare's speed test servers (the
///     same ones speed.cloudflare.com uses), picking the nearest data center automatically.
///     Several connections run in parallel so one TCP stream cannot be the bottleneck, and the
///     first seconds of each direction are left out while connections ramp up.
/// </summary>
public sealed class SpeedTest : IDisposable
{
    private const int PingCount = 20;
    private const int DownloadStreams = 8;
    private const int UploadStreams = 6;
    private const int DownloadChunk = 25_000_000;
    private const int UploadChunk = 4_000_000;
    private static readonly TimeSpan PhaseLength = TimeSpan.FromSeconds(10);
    private const double WarmupSeconds = 2.5;
    private static readonly TimeSpan MinPhaseLength = TimeSpan.FromSeconds(6);

    private readonly HttpClient _http;

    /// <param name="ipv4Only">
    ///     Connect over IPv4 only. Used to test again when the server throttles the IPv6 address;
    ///     it is the same line, just a different address for it.
    /// </param>
    public SpeedTest(bool ipv4Only = false)
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 32,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        if (ipv4Only)
            handler.ConnectCallback = ConnectIPv4Async;
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://speed.cloudflare.com/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Trevunix");
    }

    public async Task<SpeedTestResult> RunAsync(
        IProgress<SpeedProgress>? progress,
        CancellationToken ct
    )
    {
        var (server, ping, jitter) = await MeasurePingAsync(progress, ct);
        var download = await MeasureAsync(
            SpeedPhase.Download,
            DownloadStreams,
            DownloadAsync,
            progress,
            ct
        );
        var upload = await MeasureAsync(
            SpeedPhase.Upload,
            UploadStreams,
            UploadAsync,
            progress,
            ct
        );
        return new SpeedTestResult(server, ping, jitter, download, upload);
    }

    private async Task<(SpeedTestServer? Server, double Ping, double Jitter)> MeasurePingAsync(
        IProgress<SpeedProgress>? progress,
        CancellationToken ct
    )
    {
        // The first request opens the connection and tells us which server answered;
        // only the requests after it are timed.
        var (_, _, server) = await PingOnceAsync(ct);
        var rtts = new List<double>();
        var times = new List<double>();
        for (var i = 0; i < PingCount; i++)
        {
            var (time, rtt, _) = await PingOnceAsync(ct);
            times.Add(time);
            if (rtt is { } r)
                rtts.Add(r);
            progress?.Report(new SpeedProgress(SpeedPhase.Latency, 0, (i + 1d) / PingCount));
        }

        // Cloudflare's own TCP round trip is the cleanest ping; without it, fall back to the
        // request time minus the server's processing time. Jitter comes from the request times,
        // where a constant offset cancels out.
        var ping = rtts.Count > 0 ? SpeedMath.Median(rtts) : SpeedMath.Median(times);
        return (server, ping, SpeedMath.Jitter(times));
    }

    private async Task<(double Time, double? Rtt, SpeedTestServer Server)> PingOnceAsync(
        CancellationToken ct
    )
    {
        var watch = Stopwatch.StartNew();
        using var response = await _http.GetAsync(
            "__down?bytes=0",
            HttpCompletionOption.ResponseHeadersRead,
            ct
        );
        watch.Stop();

        string Header(string name) =>
            response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : "";

        var (serverMs, rtt) = SpeedMath.ParseServerTiming(Header("Server-Timing"));
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new SpeedTestLimitedException();

        var server = new SpeedTestServer(
            Uri.UnescapeDataString(Header("city")),
            Header("country"),
            Header("colo"),
            Header("asn"),
            Header("cf-meta-ip")
        );
        return (Math.Max(0, watch.Elapsed.TotalMilliseconds - serverMs), rtt, server);
    }

    private async Task<double> MeasureAsync(
        SpeedPhase phase,
        int streams,
        Func<Counter, CancellationToken, Task> worker,
        IProgress<SpeedProgress>? progress,
        CancellationToken ct
    )
    {
        var counter = new Counter();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workers = Enumerable
            .Range(0, streams)
            .Select(_ => RunUntilStoppedAsync(worker, counter, stop.Token))
            .ToList();

        var samples = new List<(double Seconds, long Bytes)>();
        var watch = Stopwatch.StartNew();
        // Stops early once the speed has settled, so a test on a fast line does not use more
        // data than it needs (Cloudflare throttles addresses that download too much).
        while (
            watch.Elapsed < PhaseLength
            && !(watch.Elapsed >= MinPhaseLength && SpeedMath.IsSteady(samples))
            && !counter.Limited
        )
        {
            await Task.Delay(200, ct);
            var now = (watch.Elapsed.TotalSeconds, counter.Bytes);
            samples.Add(now);

            var second = samples.LastOrDefault(s => s.Seconds <= now.Item1 - 1);
            var live = SpeedMath.ToMbps(now.Item2 - second.Bytes, now.Item1 - second.Seconds);
            progress?.Report(new SpeedProgress(phase, live, watch.Elapsed / PhaseLength));
        }

        await stop.CancelAsync();
        await Task.WhenAll(workers);
        ct.ThrowIfCancellationRequested();
        // A throttled run measures the limit, not the line; report it instead of a wrong number.
        if (counter.Limited)
            throw new SpeedTestLimitedException();
        return SpeedMath.SteadyMbps(samples, WarmupSeconds);
    }

    private static async Task RunUntilStoppedAsync(
        Func<Counter, CancellationToken, Task> worker,
        Counter counter,
        CancellationToken stop
    )
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await worker(counter, stop);
            }
            catch (OperationCanceledException) { }
            catch (HttpRequestException)
            {
                // A dropped connection; wait a moment and open another.
                await Task.Delay(200, CancellationToken.None);
            }
            catch (IOException) { }

            if (counter.Limited)
                await Task.Delay(1000, CancellationToken.None);
        }
    }

    private async Task DownloadAsync(Counter counter, CancellationToken ct)
    {
        using var response = await _http.GetAsync(
            $"__down?bytes={DownloadChunk}",
            HttpCompletionOption.ResponseHeadersRead,
            ct
        );
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            counter.Limited = true;
            return;
        }
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            counter.Add(read);
    }

    private async Task UploadAsync(Counter counter, CancellationToken ct)
    {
        using var content = new CountingContent(UploadChunk, counter);
        using var response = await _http.PostAsync("__up", content, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            counter.Limited = true;
    }

    private static async ValueTask<Stream> ConnectIPv4Async(
        SocketsHttpConnectionContext context,
        CancellationToken ct
    )
    {
        var addresses = await Dns.GetHostAddressesAsync(
            context.DnsEndPoint.Host,
            System.Net.Sockets.AddressFamily.InterNetwork,
            ct
        );
        var socket = new System.Net.Sockets.Socket(
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Tcp
        )
        {
            NoDelay = true,
        };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
            return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed class Counter
    {
        private long _bytes;
        public long Bytes => Interlocked.Read(ref _bytes);

        public void Add(long bytes) => Interlocked.Add(ref _bytes, bytes);

        /// <summary>The server answered 429: this address is being throttled.</summary>
        public volatile bool Limited;
    }

    /// <summary>A body of zero bytes that counts what has actually been handed to the socket.</summary>
    private sealed class CountingContent(int length, Counter counter) : HttpContent
    {
        private static readonly byte[] Block = new byte[64 * 1024];

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken ct
        )
        {
            var left = length;
            while (left > 0)
            {
                var n = Math.Min(left, Block.Length);
                await stream.WriteAsync(Block.AsMemory(0, n), ct);
                counter.Add(n);
                left -= n;
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long size)
        {
            size = length;
            return true;
        }
    }
}

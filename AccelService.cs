using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace OsuNetworkAccel;

internal sealed class AccelService
{
    private const string ProbeHost = "osu.ppy.sh";
    private const string ManagedBlockBegin = "# >>> osu-network-accel begin >>>";
    private const string ManagedBlockEnd = "# <<< osu-network-accel end <<<";
    private const int Port = 443;
    private const int Ipv4SamplesPerRange = 2;
    private const int TcpProbeConcurrency = 16;
    private const int HttpProbeConcurrency = 6;
    private const int HttpProbeCount = 8;
    private static readonly TimeSpan TcpProbeTimeout = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan HttpProbeTimeout = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan OverallSpeedTestTimeout = TimeSpan.FromSeconds(25);

    private static readonly string[] defaultDomains =
    {
        "osu.ppy.sh",
        "api.ppy.sh",
        "assets.ppy.sh",
        "a.ppy.sh",
        "b.ppy.sh",
        "c.ppy.sh",
        "i.ppy.sh",
        "s.ppy.sh",
    };

    private static readonly string[] cloudflareIpv4Ranges =
    {
        "173.245.48.0/20",
        "103.21.244.0/22",
        "103.22.200.0/22",
        "103.31.4.0/22",
        "141.101.64.0/18",
        "108.162.192.0/18",
        "190.93.240.0/20",
        "188.114.96.0/20",
        "197.234.240.0/22",
        "198.41.128.0/17",
        "162.158.0.0/15",
        "104.16.0.0/12",
        "172.64.0.0/17",
        "172.64.128.0/18",
        "172.64.192.0/19",
        "172.64.224.0/22",
        "172.64.229.0/24",
        "172.64.230.0/23",
        "172.64.232.0/21",
        "172.64.240.0/21",
        "172.64.248.0/21",
        "172.65.0.0/16",
        "172.66.0.0/16",
        "172.67.0.0/16",
        "131.0.72.0/22",
    };

    public event Action<string>? LogEmitted;

    public string HostsFilePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");

    public string StateDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsuNetworkAccel");

    public string LastResultPath => Path.Combine(StateDirectory, "last-speed-test.json");

    public async Task<OperationResult> AccelerateAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(StateDirectory);

        using var overallTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overallTimeout.CancelAfter(OverallSpeedTestTimeout);

        DateTimeOffset generatedAt = DateTimeOffset.Now;
        string? previousIp = TryLoadLastReport()?.SelectedIp;
        List<ProbeCandidate> candidates = buildCandidates(previousIp);

        log($"开始测速：候选 IP {candidates.Count} 个，探测目标 {ProbeHost}:443。");

        List<TcpProbeResult> tcpResults;
        try
        {
            tcpResults = await probeTcpCandidatesAsync(candidates, overallTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (overallTimeout.IsCancellationRequested)
        {
            return new OperationResult(false, $"测速超时：{OverallSpeedTestTimeout.TotalSeconds:F0} 秒内没有完成 TCP 初筛。");
        }

        if (tcpResults.Count == 0)
            return new OperationResult(false, "测速失败：所有候选 IP 的 TCP 连接都失败了。");

        List<TcpProbeResult> finalists = tcpResults
            .OrderBy(result => result.Latency)
            .Take(HttpProbeCount)
            .ToList();

        log($"TCP 初筛成功 {tcpResults.Count} 个，进入 HTTP 复测 {finalists.Count} 个。");

        List<HttpProbeResult> httpResults;
        try
        {
            httpResults = await probeHttpCandidatesAsync(finalists, overallTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (overallTimeout.IsCancellationRequested)
        {
            return new OperationResult(false, $"测速超时：{OverallSpeedTestTimeout.TotalSeconds:F0} 秒内没有完成 HTTP 复测。");
        }

        if (httpResults.Count == 0)
            return new OperationResult(false, "测速失败：TCP 可达，但 HTTP/TLS 复测全部失败。");

        HttpProbeResult winner = httpResults
            .OrderBy(result => result.HttpLatency)
            .ThenBy(result => result.TcpLatency)
            .First();

        HostsFileUpdateResult updateResult = HostsFileManager.UpsertManagedBlock(
            HostsFilePath,
            defaultDomains,
            winner.Ip,
            generatedAt);

        await flushDnsAsync().ConfigureAwait(false);

        SpeedTestReport report = new(
            generatedAt,
            ProbeHost,
            winner.Ip,
            defaultDomains,
            tcpResults.Count,
            httpResults.Count,
            finalists.Count,
            httpResults
                .OrderBy(result => result.HttpLatency)
                .ThenBy(result => result.TcpLatency)
                .Take(5)
                .Select(result => new SpeedTestReportItem(
                    result.Ip,
                    result.Cidr,
                    result.TcpLatency.TotalMilliseconds,
                    result.HttpLatency.TotalMilliseconds,
                    result.StatusCode))
                .ToArray());

        saveReport(report);

        string summary = new StringBuilder()
            .AppendLine($"已写入 hosts：{HostsFilePath}")
            .AppendLine($"最佳 IP：{winner.Ip}  |  TCP {winner.TcpLatency.TotalMilliseconds:F0} ms  |  HTTP {winner.HttpLatency.TotalMilliseconds:F0} ms  |  状态 {winner.StatusCode}")
            .AppendLine($"映射域名：{string.Join(", ", defaultDomains)}")
            .AppendLine(updateResult.ReplacedExistingBlock ? "已替换旧的 osu-network-accel 管理块。" : "已新增 osu-network-accel 管理块。")
            .Append($"测速报告已保存到：{LastResultPath}")
            .ToString();

        log(summary.Replace(Environment.NewLine, " "));
        return new OperationResult(true, summary);
    }

    public async Task<OperationResult> RestoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HostsFileRemoveResult result = HostsFileManager.RemoveManagedBlock(HostsFilePath);
        await flushDnsAsync().ConfigureAwait(false);

        string message = result.Removed
            ? $"已从 hosts 中移除 osu-network-accel 管理块：{HostsFilePath}"
            : $"hosts 中没有找到 osu-network-accel 管理块：{HostsFilePath}";

        log(message);
        return new OperationResult(true, message);
    }

    public SpeedTestReport? TryLoadLastReport()
    {
        if (!File.Exists(LastResultPath))
            return null;

        try
        {
            return JsonSerializer.Deserialize<SpeedTestReport>(File.ReadAllText(LastResultPath, Encoding.UTF8));
        }
        catch
        {
            return null;
        }
    }

    private void log(string message) => LogEmitted?.Invoke(message);

    private void saveReport(SpeedTestReport report)
    {
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
        });

        File.WriteAllText(LastResultPath, json, Encoding.UTF8);
    }

    private List<ProbeCandidate> buildCandidates(string? previousIp)
    {
        Dictionary<string, ProbeCandidate> candidates = new(StringComparer.Ordinal);

        if (IPAddress.TryParse(previousIp, out _))
            candidates[previousIp!] = new ProbeCandidate("last-selected", previousIp!);

        foreach (string cidr in cloudflareIpv4Ranges)
        {
            foreach (string ip in sampleIpv4Range(cidr, Ipv4SamplesPerRange))
                candidates.TryAdd(ip, new ProbeCandidate(cidr, ip));
        }

        return candidates.Values.ToList();
    }

    private static IEnumerable<string> sampleIpv4Range(string cidr, int sampleCount)
    {
        if (!tryParseIpv4Cidr(cidr, out uint network, out int prefixLength))
            yield break;

        ulong hostCount = 1UL << (32 - prefixLength);
        ulong minOffset = hostCount > 2 ? 1UL : 0UL;
        ulong maxExclusive = hostCount > 2 ? hostCount - 1UL : hostCount;

        if (maxExclusive <= minOffset)
        {
            yield return formatIpv4(network);
            yield break;
        }

        HashSet<ulong> offsets = new();

        while (offsets.Count < sampleCount && offsets.Count < (int)(maxExclusive - minOffset))
        {
            ulong nextOffset = (ulong)Random.Shared.NextInt64((long)minOffset, (long)maxExclusive);
            if (!offsets.Add(nextOffset))
                continue;

            yield return formatIpv4(network + (uint)nextOffset);
        }
    }

    private static async Task<List<TcpProbeResult>> probeTcpCandidatesAsync(IReadOnlyCollection<ProbeCandidate> candidates, CancellationToken cancellationToken)
    {
        ConcurrentBag<TcpProbeResult> results = new();
        using SemaphoreSlim throttler = new(TcpProbeConcurrency);

        List<Task> tasks = candidates.Select(async candidate =>
        {
            await throttler.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                TcpProbeResult result = await probeTcpAsync(candidate, cancellationToken).ConfigureAwait(false);
                if (result.Success)
                    results.Add(result);
            }
            finally
            {
                throttler.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.ToList();
    }

    private static async Task<TcpProbeResult> probeTcpAsync(ProbeCandidate candidate, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(candidate.Ip, out IPAddress? address))
            return TcpProbeResult.Failed(candidate, "invalid ip");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TcpProbeTimeout);

        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            using TcpClient client = new(address.AddressFamily);
            client.NoDelay = true;

            await client.ConnectAsync(address, Port, timeout.Token).ConfigureAwait(false);
            stopwatch.Stop();
            return TcpProbeResult.Succeeded(candidate, stopwatch.Elapsed);
        }
        catch (Exception error)
        {
            stopwatch.Stop();
            return TcpProbeResult.Failed(candidate, describeProbeError(error));
        }
    }

    private static async Task<List<HttpProbeResult>> probeHttpCandidatesAsync(IReadOnlyCollection<TcpProbeResult> candidates, CancellationToken cancellationToken)
    {
        ConcurrentBag<HttpProbeResult> results = new();
        using SemaphoreSlim throttler = new(HttpProbeConcurrency);

        List<Task> tasks = candidates.Select(async candidate =>
        {
            await throttler.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                HttpProbeResult? result = await probeHttpAsync(candidate, cancellationToken).ConfigureAwait(false);
                if (result != null)
                    results.Add(result);
            }
            finally
            {
                throttler.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.ToList();
    }

    private static async Task<HttpProbeResult?> probeHttpAsync(TcpProbeResult candidate, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(candidate.Ip, out IPAddress? address))
            return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HttpProbeTimeout);

        try
        {
            using TcpClient client = new(address.AddressFamily);
            client.NoDelay = true;
            await client.ConnectAsync(address, Port, timeout.Token).ConfigureAwait(false);

            using NetworkStream networkStream = client.GetStream();
            using SslStream sslStream = new(
                networkStream,
                false,
                (_, certificate, _, errors) =>
                    certificate != null && (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0);

            Stopwatch stopwatch = Stopwatch.StartNew();

            await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = ProbeHost,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, timeout.Token).ConfigureAwait(false);

            byte[] requestBytes = Encoding.ASCII.GetBytes(
                $"HEAD / HTTP/1.1\r\nHost: {ProbeHost}\r\nUser-Agent: OsuNetworkAccel-SpeedTest\r\nConnection: close\r\n\r\n");

            await sslStream.WriteAsync(requestBytes, timeout.Token).ConfigureAwait(false);
            await sslStream.FlushAsync(timeout.Token).ConfigureAwait(false);

            string responseHeader = await readResponseHeaderAsync(sslStream, timeout.Token).ConfigureAwait(false);
            stopwatch.Stop();

            int statusCode = parseStatusCode(responseHeader);
            if (statusCode <= 0 || statusCode >= 500)
                return null;

            return new HttpProbeResult(candidate.Candidate.Cidr, candidate.Ip, candidate.Latency, stopwatch.Elapsed, statusCode);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> readResponseHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[256];
        StringBuilder builder = new();

        while (builder.Length < 8192)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
                break;

            builder.Append(Encoding.ASCII.GetString(buffer, 0, read));

            if (builder.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                break;
        }

        return builder.ToString();
    }

    private static int parseStatusCode(string responseHeader)
    {
        string? statusLine = responseHeader
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .FirstOrDefault(line => line.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase));

        if (statusLine == null)
            return 0;

        string[] parts = statusLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && int.TryParse(parts[1], out int statusCode)
            ? statusCode
            : 0;
    }

    private async Task flushDnsAsync()
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "ipconfig",
            Arguments = "/flushdns",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        process.Start();
        string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "刷新 DNS 缓存失败。" : stderr.Trim());

        if (!string.IsNullOrWhiteSpace(stdout))
            log(stdout.Trim());
    }

    private static bool tryParseIpv4Cidr(string cidr, out uint network, out int prefixLength)
    {
        network = 0;
        prefixLength = 0;

        string[] parts = cidr.Split('/');
        if (parts.Length != 2)
            return false;

        if (!IPAddress.TryParse(parts[0], out IPAddress? address) || address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        if (!int.TryParse(parts[1], out prefixLength) || prefixLength is < 0 or > 32)
            return false;

        uint value = parseIpv4(address);
        uint mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        network = value & mask;
        return true;
    }

    private static uint parseIpv4(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24)
             | ((uint)bytes[1] << 16)
             | ((uint)bytes[2] << 8)
             | bytes[3];
    }

    private static string formatIpv4(uint value)
        => $"{(value >> 24) & 255}.{(value >> 16) & 255}.{(value >> 8) & 255}.{value & 255}";

    private static string describeProbeError(Exception error)
        => error switch
        {
            OperationCanceledException => "timeout",
            SocketException socketError => $"socket {socketError.SocketErrorCode}",
            _ => $"{error.GetType().Name}: {error.Message}",
        };

    private static class HostsFileManager
    {
        public static HostsFileUpdateResult UpsertManagedBlock(string hostsFilePath, IReadOnlyList<string> domains, string ip, DateTimeOffset generatedAt)
        {
            TextWithEncoding text = ReadText(hostsFilePath);
            bool replacedExistingBlock = TryStripManagedBlock(text.Lines, out List<string> cleanedLines);

            List<string> lines = cleanedLines;
            TrimTrailingBlankLines(lines);

            if (lines.Count > 0)
                lines.Add(string.Empty);

            lines.Add(ManagedBlockBegin);
            lines.Add($"# Generated at {generatedAt:yyyy-MM-dd HH:mm:ss zzz}");

            foreach (string domain in domains)
                lines.Add($"{ip} {domain}");

            lines.Add(ManagedBlockEnd);

            WriteText(hostsFilePath, text.Encoding, lines);
            return new HostsFileUpdateResult(replacedExistingBlock);
        }

        public static HostsFileRemoveResult RemoveManagedBlock(string hostsFilePath)
        {
            TextWithEncoding text = ReadText(hostsFilePath);
            bool removed = TryStripManagedBlock(text.Lines, out List<string> cleanedLines);

            if (!removed)
                return new HostsFileRemoveResult(false);

            TrimTrailingBlankLines(cleanedLines);
            WriteText(hostsFilePath, text.Encoding, cleanedLines);
            return new HostsFileRemoveResult(true);
        }

        private static bool TryStripManagedBlock(List<string> sourceLines, out List<string> cleanedLines)
        {
            cleanedLines = new List<string>(sourceLines.Count);
            bool insideManagedBlock = false;
            bool removed = false;

            foreach (string line in sourceLines)
            {
                if (string.Equals(line, ManagedBlockBegin, StringComparison.Ordinal))
                {
                    insideManagedBlock = true;
                    removed = true;
                    continue;
                }

                if (string.Equals(line, ManagedBlockEnd, StringComparison.Ordinal))
                {
                    insideManagedBlock = false;
                    continue;
                }

                if (!insideManagedBlock)
                    cleanedLines.Add(line);
            }

            return removed;
        }

        private static TextWithEncoding ReadText(string path)
        {
            if (!File.Exists(path))
                return new TextWithEncoding(Encoding.Default, new List<string>());

            using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new(stream, Encoding.Default, detectEncodingFromByteOrderMarks: true);
            string content = reader.ReadToEnd();

            List<string> lines = content
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .ToList();

            if (lines.Count > 0 && lines[^1].Length == 0)
                lines.RemoveAt(lines.Count - 1);

            return new TextWithEncoding(reader.CurrentEncoding, lines);
        }

        private static void WriteText(string path, Encoding encoding, IReadOnlyList<string> lines)
        {
            string directory = Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException("无法确定 hosts 文件所在目录。");

            Directory.CreateDirectory(directory);

            string content = string.Join(Environment.NewLine, lines);
            if (lines.Count > 0)
                content += Environment.NewLine;

            File.WriteAllText(path, content, encoding);
        }

        private static void TrimTrailingBlankLines(List<string> lines)
        {
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
                lines.RemoveAt(lines.Count - 1);
        }
    }
}

internal sealed record OperationResult(bool Success, string Message);

internal sealed record SpeedTestReport(
    DateTimeOffset GeneratedAt,
    string ProbeHost,
    string SelectedIp,
    IReadOnlyList<string> Domains,
    int TcpSuccessCount,
    int HttpSuccessCount,
    int HttpRetestCount,
    IReadOnlyList<SpeedTestReportItem> TopResults);

internal sealed record SpeedTestReportItem(string Ip, string Cidr, double TcpLatencyMs, double HttpLatencyMs, int StatusCode);
internal sealed record TextWithEncoding(Encoding Encoding, List<string> Lines);
internal sealed record HostsFileUpdateResult(bool ReplacedExistingBlock);
internal sealed record HostsFileRemoveResult(bool Removed);
internal sealed record ProbeCandidate(string Cidr, string Ip);
internal sealed record HttpProbeResult(string Cidr, string Ip, TimeSpan TcpLatency, TimeSpan HttpLatency, int StatusCode);

internal sealed class TcpProbeResult
{
    public ProbeCandidate Candidate { get; }
    public string Ip => Candidate.Ip;
    public bool Success { get; }
    public TimeSpan Latency { get; }
    public string? FailureReason { get; }

    private TcpProbeResult(ProbeCandidate candidate, bool success, TimeSpan latency, string? failureReason)
    {
        Candidate = candidate;
        Success = success;
        Latency = latency;
        FailureReason = failureReason;
    }

    public static TcpProbeResult Succeeded(ProbeCandidate candidate, TimeSpan latency)
        => new(candidate, true, latency, null);

    public static TcpProbeResult Failed(ProbeCandidate candidate, string failureReason)
        => new(candidate, false, TimeSpan.MaxValue, failureReason);
}

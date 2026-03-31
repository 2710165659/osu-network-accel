using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace OsuNetworkAccel;

internal static class Program
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

    public static async Task<int> Main(string[] args)
    {
        try
        {
            CommandLineOptions options = CommandLineOptions.Parse(args);

            if (options.Command == Command.Help)
            {
                PrintHelp();
                return 0;
            }

            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("这个工具当前只支持 Windows。");
                return 1;
            }

            if (RequiresAdministrator(options) && !IsRunningAsAdministrator())
            {
                Console.Error.WriteLine("需要管理员权限才能修改系统 hosts。请通过提供的 bat 运行，或手动以管理员身份启动。");
                return 1;
            }

            Directory.CreateDirectory(options.StateDirectory);

            return options.Command switch
            {
                Command.Accelerate => await RunAccelerateAsync(options).ConfigureAwait(false),
                Command.Restore => await RunRestoreAsync(options).ConfigureAwait(false),
                _ => 1,
            };
        }
        catch (CommandLineException error)
        {
            Console.Error.WriteLine(error.Message);
            Console.Error.WriteLine();
            PrintHelp();
            return 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"执行失败：{error.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAccelerateAsync(CommandLineOptions options)
    {
        using var overallTimeout = new CancellationTokenSource(OverallSpeedTestTimeout);
        DateTimeOffset generatedAt = options.GeneratedAt;

        string? previousIp = LoadLastSelectedIp(options.LastResultPath);
        List<ProbeCandidate> candidates = BuildCandidates(previousIp);

        Console.WriteLine($"开始测速：候选 IP {candidates.Count} 个，探测目标 {ProbeHost}:443。");

        List<TcpProbeResult> tcpResults;
        try
        {
            tcpResults = await ProbeTcpCandidatesAsync(candidates, overallTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (overallTimeout.IsCancellationRequested)
        {
            Console.Error.WriteLine($"测速超时：{OverallSpeedTestTimeout.TotalSeconds:F0} 秒内没有完成 TCP 初筛。");
            return 1;
        }

        if (tcpResults.Count == 0)
        {
            Console.Error.WriteLine("测速失败：所有候选 IP 的 TCP 连接都失败了。");
            return 1;
        }

        List<TcpProbeResult> finalists = tcpResults
            .OrderBy(result => result.Latency)
            .Take(HttpProbeCount)
            .ToList();

        Console.WriteLine($"TCP 初筛成功 {tcpResults.Count} 个，进入 HTTP 复测 {finalists.Count} 个。");

        List<HttpProbeResult> httpResults;
        try
        {
            httpResults = await ProbeHttpCandidatesAsync(finalists, overallTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (overallTimeout.IsCancellationRequested)
        {
            Console.Error.WriteLine($"测速超时：{OverallSpeedTestTimeout.TotalSeconds:F0} 秒内没有完成 HTTP 复测。");
            return 1;
        }

        if (httpResults.Count == 0)
        {
            Console.Error.WriteLine("测速失败：TCP 可达，但 HTTP/TLS 复测全部失败。");
            return 1;
        }

        HttpProbeResult winner = httpResults
            .OrderBy(result => result.HttpLatency)
            .ThenBy(result => result.TcpLatency)
            .First();

        HostsFileUpdateResult updateResult = HostsFileManager.UpsertManagedBlock(
            options.HostsFilePath,
            defaultDomains,
            winner.Ip,
            generatedAt);

        if (options.FlushDns)
            await FlushDnsAsync().ConfigureAwait(false);

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

        SaveReport(options.LastResultPath, report);

        Console.WriteLine();
        Console.WriteLine($"已写入 hosts：{options.HostsFilePath}");
        Console.WriteLine($"最佳 IP：{winner.Ip}  |  TCP {winner.TcpLatency.TotalMilliseconds:F0} ms  |  HTTP {winner.HttpLatency.TotalMilliseconds:F0} ms  |  状态 {winner.StatusCode}");
        Console.WriteLine($"映射域名：{string.Join(", ", defaultDomains)}");
        Console.WriteLine(updateResult.ReplacedExistingBlock
            ? "已替换旧的 osu-network-accel 管理块。"
            : "已新增 osu-network-accel 管理块。");
        Console.WriteLine($"测速报告已保存到：{options.LastResultPath}");

        return 0;
    }

    private static async Task<int> RunRestoreAsync(CommandLineOptions options)
    {
        HostsFileRemoveResult result = HostsFileManager.RemoveManagedBlock(options.HostsFilePath);

        if (options.FlushDns)
            await FlushDnsAsync().ConfigureAwait(false);

        Console.WriteLine(result.Removed
            ? $"已从 hosts 中移除 osu-network-accel 管理块：{options.HostsFilePath}"
            : $"hosts 中没有找到 osu-network-accel 管理块：{options.HostsFilePath}");

        return 0;
    }

    private static List<ProbeCandidate> BuildCandidates(string? previousIp)
    {
        Dictionary<string, ProbeCandidate> candidates = new(StringComparer.Ordinal);

        if (IPAddress.TryParse(previousIp, out _))
            candidates[previousIp!] = new ProbeCandidate("last-selected", previousIp!);

        foreach (string cidr in cloudflareIpv4Ranges)
        {
            foreach (string ip in SampleIpv4Range(cidr, Ipv4SamplesPerRange))
                candidates.TryAdd(ip, new ProbeCandidate(cidr, ip));
        }

        return candidates.Values.ToList();
    }

    private static IEnumerable<string> SampleIpv4Range(string cidr, int sampleCount)
    {
        if (!TryParseIpv4Cidr(cidr, out uint network, out int prefixLength))
            yield break;

        ulong hostCount = 1UL << (32 - prefixLength);
        ulong minOffset = hostCount > 2 ? 1UL : 0UL;
        ulong maxExclusive = hostCount > 2 ? hostCount - 1UL : hostCount;

        if (maxExclusive <= minOffset)
        {
            yield return FormatIpv4(network);
            yield break;
        }

        HashSet<ulong> offsets = new();

        while (offsets.Count < sampleCount && offsets.Count < (int)(maxExclusive - minOffset))
        {
            ulong nextOffset = (ulong)Random.Shared.NextInt64((long)minOffset, (long)maxExclusive);
            if (!offsets.Add(nextOffset))
                continue;

            yield return FormatIpv4(network + (uint)nextOffset);
        }
    }

    private static async Task<List<TcpProbeResult>> ProbeTcpCandidatesAsync(IReadOnlyCollection<ProbeCandidate> candidates, CancellationToken cancellationToken)
    {
        ConcurrentBag<TcpProbeResult> results = new();
        using SemaphoreSlim throttler = new(TcpProbeConcurrency);

        List<Task> tasks = candidates.Select(async candidate =>
        {
            await throttler.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                TcpProbeResult result = await ProbeTcpAsync(candidate, cancellationToken).ConfigureAwait(false);
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

    private static async Task<TcpProbeResult> ProbeTcpAsync(ProbeCandidate candidate, CancellationToken cancellationToken)
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
            return TcpProbeResult.Failed(candidate, DescribeProbeError(error));
        }
    }

    private static async Task<List<HttpProbeResult>> ProbeHttpCandidatesAsync(IReadOnlyCollection<TcpProbeResult> candidates, CancellationToken cancellationToken)
    {
        ConcurrentBag<HttpProbeResult> results = new();
        using SemaphoreSlim throttler = new(HttpProbeConcurrency);

        List<Task> tasks = candidates.Select(async candidate =>
        {
            await throttler.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                HttpProbeResult? result = await ProbeHttpAsync(candidate, cancellationToken).ConfigureAwait(false);
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

    private static async Task<HttpProbeResult?> ProbeHttpAsync(TcpProbeResult candidate, CancellationToken cancellationToken)
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

            string responseHeader = await ReadResponseHeaderAsync(sslStream, timeout.Token).ConfigureAwait(false);
            stopwatch.Stop();

            int statusCode = ParseStatusCode(responseHeader);
            if (statusCode <= 0 || statusCode >= 500)
                return null;

            return new HttpProbeResult(candidate.Candidate.Cidr, candidate.Ip, candidate.Latency, stopwatch.Elapsed, statusCode);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> ReadResponseHeaderAsync(Stream stream, CancellationToken cancellationToken)
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

    private static int ParseStatusCode(string responseHeader)
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

    private static async Task FlushDnsAsync()
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
            Console.WriteLine(stdout.Trim());
    }

    private static void SaveReport(string path, SpeedTestReport report)
    {
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
        });

        File.WriteAllText(path, json, Encoding.UTF8);
    }

    private static string? LoadLastSelectedIp(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            SpeedTestReport? report = JsonSerializer.Deserialize<SpeedTestReport>(File.ReadAllText(path, Encoding.UTF8));
            return report?.SelectedIp;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseIpv4Cidr(string cidr, out uint network, out int prefixLength)
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

        uint value = ParseIpv4(address);
        uint mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        network = value & mask;
        return true;
    }

    private static uint ParseIpv4(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24)
             | ((uint)bytes[1] << 16)
             | ((uint)bytes[2] << 8)
             | bytes[3];
    }

    private static string FormatIpv4(uint value)
        => $"{(value >> 24) & 255}.{(value >> 16) & 255}.{(value >> 8) & 255}.{value & 255}";

    private static string DescribeProbeError(Exception error)
        => error switch
        {
            OperationCanceledException => "timeout",
            SocketException socketError => $"socket {socketError.SocketErrorCode}",
            _ => $"{error.GetType().Name}: {error.Message}",
        };

    private static bool RequiresAdministrator(CommandLineOptions options)
        => string.Equals(
            Path.GetFullPath(options.HostsFilePath),
            Path.GetFullPath(CommandLineOptions.DefaultHostsFilePath),
            StringComparison.OrdinalIgnoreCase);

    [SupportedOSPlatform("windows")]
    private static bool IsRunningAsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("osu-network-accel");
        Console.WriteLine();
        Console.WriteLine("用法：");
        Console.WriteLine("  accelerate             测速并把优选 IP 写入 hosts");
        Console.WriteLine("  restore                从 hosts 移除本工具写入的管理块");
        Console.WriteLine();
        Console.WriteLine("可选参数：");
        Console.WriteLine("  --hosts-file <path>    指定 hosts 文件路径，默认是系统 hosts");
        Console.WriteLine("  --app-root <path>      指定状态文件目录根路径，默认是当前目录");
        Console.WriteLine("  --no-flushdns          修改 hosts 后不执行 ipconfig /flushdns");
    }

    private sealed record CommandLineOptions(Command Command, string HostsFilePath, string AppRoot, bool FlushDns)
    {
        public static string DefaultHostsFilePath { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");

        public DateTimeOffset GeneratedAt => DateTimeOffset.Now;

        public string StateDirectory => Path.Combine(AppRoot, ".state");

        public string LastResultPath => Path.Combine(StateDirectory, "last-speed-test.json");

        public static CommandLineOptions Parse(string[] args)
        {
            if (args.Length == 0)
                return new CommandLineOptions(Command.Help, DefaultHostsFilePath, Directory.GetCurrentDirectory(), true);

            Command command = args[0].ToLowerInvariant() switch
            {
                "accelerate" => Command.Accelerate,
                "restore" => Command.Restore,
                "help" => Command.Help,
                "--help" => Command.Help,
                "/?" => Command.Help,
                _ => throw new CommandLineException($"未知命令：{args[0]}")
            };

            string hostsFilePath = DefaultHostsFilePath;
            string appRoot = Directory.GetCurrentDirectory();
            bool flushDns = true;

            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--hosts-file":
                        if (i + 1 >= args.Length)
                            throw new CommandLineException("--hosts-file 缺少路径参数。");

                        hostsFilePath = Path.GetFullPath(args[++i]);
                        break;

                    case "--app-root":
                        if (i + 1 >= args.Length)
                            throw new CommandLineException("--app-root 缺少路径参数。");

                        appRoot = Path.GetFullPath(args[++i]);
                        break;

                    case "--no-flushdns":
                        flushDns = false;
                        break;

                    default:
                        throw new CommandLineException($"未知参数：{args[i]}");
                }
            }

            return new CommandLineOptions(command, hostsFilePath, appRoot, flushDns);
        }
    }

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

    private sealed record TextWithEncoding(Encoding Encoding, List<string> Lines);
    private sealed record HostsFileUpdateResult(bool ReplacedExistingBlock);
    private sealed record HostsFileRemoveResult(bool Removed);
    private sealed record ProbeCandidate(string Cidr, string Ip);
    private sealed record HttpProbeResult(string Cidr, string Ip, TimeSpan TcpLatency, TimeSpan HttpLatency, int StatusCode);
    private sealed record SpeedTestReport(
        DateTimeOffset GeneratedAt,
        string ProbeHost,
        string SelectedIp,
        IReadOnlyList<string> Domains,
        int TcpSuccessCount,
        int HttpSuccessCount,
        int HttpRetestCount,
        IReadOnlyList<SpeedTestReportItem> TopResults);
    private sealed record SpeedTestReportItem(string Ip, string Cidr, double TcpLatencyMs, double HttpLatencyMs, int StatusCode);

    private sealed class TcpProbeResult
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

    private enum Command
    {
        Help,
        Accelerate,
        Restore,
    }

    private sealed class CommandLineException(string message) : Exception(message);
}

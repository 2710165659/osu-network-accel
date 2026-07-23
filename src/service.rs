use chrono::{DateTime, Local};
use native_tls::TlsConnector;
use rand::Rng;
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};
use std::fs;
use std::io::{Read, Write};
use std::net::{IpAddr, Ipv4Addr, SocketAddr, TcpStream};
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{Duration, Instant};

const PROBE_HOST: &str = "osu.ppy.sh";
const BEGIN: &str = "# >>> osu-network-accel begin >>>";
const END: &str = "# <<< osu-network-accel end <<<";
const DOMAINS: &[&str] = &[
    "osu.ppy.sh",
    "api.ppy.sh",
    "assets.ppy.sh",
    "a.ppy.sh",
    "b.ppy.sh",
    "c.ppy.sh",
    "i.ppy.sh",
    "s.ppy.sh",
];
const RANGES: &[&str] = &[
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
];

#[derive(Clone)]
pub struct AccelService {
    state_dir: PathBuf,
    hosts_path: PathBuf,
}

#[derive(Debug)]
pub struct OperationResult {
    pub success: bool,
    pub message: String,
}

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct SpeedTestReport {
    pub generated_at: DateTime<Local>,
    pub probe_host: String,
    pub selected_ip: String,
    pub domains: Vec<String>,
    pub tcp_success_count: usize,
    pub http_success_count: usize,
    pub http_retest_count: usize,
    pub top_results: Vec<ReportItem>,
}

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct ReportItem {
    pub ip: String,
    pub cidr: String,
    pub tcp_latency_ms: f64,
    pub http_latency_ms: f64,
    pub status_code: u16,
}

#[derive(Clone)]
struct Candidate {
    cidr: String,
    ip: Ipv4Addr,
}
#[derive(Clone)]
struct TcpResult {
    candidate: Candidate,
    latency: Duration,
}
#[derive(Clone)]
struct HttpResult {
    tcp: TcpResult,
    latency: Duration,
    status: u16,
}

impl AccelService {
    pub fn new() -> Self {
        let local = std::env::var_os("LOCALAPPDATA")
            .map(PathBuf::from)
            .unwrap_or_else(std::env::temp_dir);
        let system = std::env::var_os("SystemRoot")
            .map(PathBuf::from)
            .unwrap_or_else(|| PathBuf::from(r"C:\Windows"));
        Self {
            state_dir: local.join("OsuNetworkAccel"),
            hosts_path: system.join(r"System32\drivers\etc\hosts"),
        }
    }
    pub fn state_directory(&self) -> &Path {
        &self.state_dir
    }
    pub fn last_result_path(&self) -> PathBuf {
        self.state_dir.join("last-speed-test.json")
    }
    pub fn last_report(&self) -> Option<SpeedTestReport> {
        serde_json::from_slice(&fs::read(self.last_result_path()).ok()?).ok()
    }

    pub fn accelerate_with_logger<F>(&self, log: F) -> OperationResult
    where
        F: Fn(String),
    {
        if let Err(e) = fs::create_dir_all(&self.state_dir) {
            return fail(format!("无法创建报告目录：{e}"));
        }
        let started = Instant::now();
        let candidates =
            build_candidates(self.last_report().and_then(|r| r.selected_ip.parse().ok()));
        log(format!(
            "开始测速：候选 IP {} 个，探测目标 {PROBE_HOST}:443。",
            candidates.len()
        ));
        let mut tcp = parallel_probe(candidates, 16, probe_tcp);
        if tcp.is_empty() {
            return fail("测速失败：所有候选 IP 的 TCP 连接都失败了。".into());
        }
        tcp.sort_by_key(|r| r.latency);
        let finalists: Vec<_> = tcp.iter().take(8).cloned().collect();
        log(format!(
            "TCP 初筛成功 {} 个，进入 HTTP 复测 {} 个。",
            tcp.len(),
            finalists.len()
        ));
        if started.elapsed() > Duration::from_secs(25) {
            return fail("测速超时：25 秒内没有完成 TCP 初筛。".into());
        }
        let mut http = parallel_probe(finalists.clone(), 6, probe_http);
        if http.is_empty() {
            return fail("测速失败：TCP 可达，但 HTTP/TLS 复测全部失败。".into());
        }
        http.sort_by_key(|r| (r.latency, r.tcp.latency));
        let winner = &http[0];
        let generated_at = Local::now();
        let replaced = match upsert_hosts(&self.hosts_path, winner.tcp.candidate.ip, generated_at) {
            Ok(v) => v,
            Err(e) => return fail(format!("写入 hosts 失败：{e}")),
        };
        if let Err(e) = flush_dns() {
            return fail(e);
        }
        log("已成功刷新 DNS 缓存。".into());
        let report = SpeedTestReport {
            generated_at,
            probe_host: PROBE_HOST.into(),
            selected_ip: winner.tcp.candidate.ip.to_string(),
            domains: DOMAINS.iter().map(|s| (*s).into()).collect(),
            tcp_success_count: tcp.len(),
            http_success_count: http.len(),
            http_retest_count: finalists.len(),
            top_results: http
                .iter()
                .take(5)
                .map(|r| ReportItem {
                    ip: r.tcp.candidate.ip.to_string(),
                    cidr: r.tcp.candidate.cidr.clone(),
                    tcp_latency_ms: r.tcp.latency.as_secs_f64() * 1000.0,
                    http_latency_ms: r.latency.as_secs_f64() * 1000.0,
                    status_code: r.status,
                })
                .collect(),
        };
        if let Err(e) = fs::write(
            self.last_result_path(),
            serde_json::to_vec_pretty(&report).unwrap(),
        ) {
            return fail(format!("保存测速报告失败：{e}"));
        }
        let result = OperationResult { success: true, message: format!("已写入 hosts：{}\n最佳 IP：{}  |  TCP {:.0} ms  |  HTTP {:.0} ms  |  状态 {}\n映射域名：{}\n{}\n测速报告已保存到：{}", self.hosts_path.display(), winner.tcp.candidate.ip, winner.tcp.latency.as_secs_f64()*1000.0, winner.latency.as_secs_f64()*1000.0, winner.status, DOMAINS.join(", "), if replaced { "已替换旧的 osu-network-accel 管理块。" } else { "已新增 osu-network-accel 管理块。" }, self.last_result_path().display()) };
        result
    }

    pub fn restore_with_logger<F>(&self, log: F) -> OperationResult
    where
        F: Fn(String),
    {
        let removed = match remove_hosts(&self.hosts_path) {
            Ok(v) => v,
            Err(e) => return fail(format!("恢复 hosts 失败：{e}")),
        };
        if let Err(e) = flush_dns() {
            return fail(e);
        }
        log("已成功刷新 DNS 缓存。".into());
        let result = OperationResult {
            success: true,
            message: format!(
                "hosts 中{} osu-network-accel 管理块：{}",
                if removed { "已移除" } else { "没有找到" },
                self.hosts_path.display()
            ),
        };
        result
    }
}

fn fail(message: String) -> OperationResult {
    OperationResult {
        success: false,
        message,
    }
}

fn build_candidates(previous: Option<Ipv4Addr>) -> Vec<Candidate> {
    let mut values = HashMap::new();
    if let Some(ip) = previous {
        values.insert(
            ip,
            Candidate {
                cidr: "last-selected".into(),
                ip,
            },
        );
    }
    for cidr in RANGES {
        for ip in sample_range(cidr, 2) {
            values.entry(ip).or_insert_with(|| Candidate {
                cidr: (*cidr).into(),
                ip,
            });
        }
    }
    values.into_values().collect()
}

fn sample_range(cidr: &str, count: usize) -> Vec<Ipv4Addr> {
    let Some((address, prefix)) = cidr.split_once('/') else {
        return vec![];
    };
    let (Ok(ip), Ok(prefix)) = (address.parse::<Ipv4Addr>(), prefix.parse::<u32>()) else {
        return vec![];
    };
    if prefix > 32 {
        return vec![];
    }
    let raw = u32::from(ip);
    let mask = if prefix == 0 {
        0
    } else {
        u32::MAX << (32 - prefix)
    };
    let network = raw & mask;
    let hosts = 1u64 << (32 - prefix);
    let min = if hosts > 2 { 1 } else { 0 };
    let max = if hosts > 2 { hosts - 1 } else { hosts };
    let mut rng = rand::rng();
    let mut offsets = HashSet::new();
    while offsets.len() < count && offsets.len() < (max - min) as usize {
        offsets.insert(rng.random_range(min..max));
    }
    offsets
        .into_iter()
        .map(|v| Ipv4Addr::from(network.wrapping_add(v as u32)))
        .collect()
}

fn parallel_probe<I, O, F>(items: Vec<I>, concurrency: usize, probe: F) -> Vec<O>
where
    I: Send + Sync + 'static,
    O: Send + 'static,
    F: Fn(&I) -> Option<O> + Send + Sync + Copy + 'static,
{
    let items = Arc::new(items);
    let next = Arc::new(Mutex::new(0usize));
    let output = Arc::new(Mutex::new(Vec::new()));
    let mut workers = Vec::new();
    for _ in 0..concurrency.min(items.len()) {
        let items = items.clone();
        let next = next.clone();
        let output = output.clone();
        workers.push(thread::spawn(move || loop {
            let index = {
                let mut n = next.lock().unwrap();
                if *n >= items.len() {
                    break;
                }
                let i = *n;
                *n += 1;
                i
            };
            if let Some(v) = probe(&items[index]) {
                output.lock().unwrap().push(v);
            }
        }));
    }
    for worker in workers {
        let _ = worker.join();
    }
    Arc::try_unwrap(output).ok().unwrap().into_inner().unwrap()
}

fn probe_tcp(candidate: &Candidate) -> Option<TcpResult> {
    let start = Instant::now();
    TcpStream::connect_timeout(
        &SocketAddr::new(IpAddr::V4(candidate.ip), 443),
        Duration::from_millis(1200),
    )
    .ok()
    .map(|_| TcpResult {
        candidate: candidate.clone(),
        latency: start.elapsed(),
    })
}

fn probe_http(candidate: &TcpResult) -> Option<HttpResult> {
    let tcp = TcpStream::connect_timeout(
        &SocketAddr::new(IpAddr::V4(candidate.candidate.ip), 443),
        Duration::from_millis(2500),
    )
    .ok()?;
    tcp.set_read_timeout(Some(Duration::from_millis(2500)))
        .ok()?;
    tcp.set_write_timeout(Some(Duration::from_millis(2500)))
        .ok()?;
    let start = Instant::now();
    let connector = TlsConnector::new().ok()?;
    let mut tls = connector.connect(PROBE_HOST, tcp).ok()?;
    tls.write_all(b"HEAD / HTTP/1.1\r\nHost: osu.ppy.sh\r\nUser-Agent: OsuNetworkAccel-SpeedTest\r\nConnection: close\r\n\r\n").ok()?;
    let mut data = Vec::new();
    let mut buf = [0u8; 256];
    while data.len() < 8192 {
        let n = tls.read(&mut buf).ok()?;
        if n == 0 {
            break;
        }
        data.extend_from_slice(&buf[..n]);
        if data.windows(4).any(|w| w == b"\r\n\r\n") {
            break;
        }
    }
    let line = String::from_utf8_lossy(&data);
    let status = line
        .lines()
        .next()?
        .split_whitespace()
        .nth(1)?
        .parse::<u16>()
        .ok()?;
    if status >= 500 {
        return None;
    }
    Some(HttpResult {
        tcp: candidate.clone(),
        latency: start.elapsed(),
        status,
    })
}

fn upsert_hosts(path: &Path, ip: Ipv4Addr, now: DateTime<Local>) -> std::io::Result<bool> {
    let content = fs::read_to_string(path).unwrap_or_default();
    let (mut lines, replaced) = strip_block(&content);
    while lines.last().is_some_and(|s| s.trim().is_empty()) {
        lines.pop();
    }
    if !lines.is_empty() {
        lines.push(String::new());
    }
    lines.push(BEGIN.into());
    lines.push(format!(
        "# Generated at {}",
        now.format("%Y-%m-%d %H:%M:%S %:z")
    ));
    for domain in DOMAINS {
        lines.push(format!("{ip} {domain}"));
    }
    lines.push(END.into());
    fs::write(path, format!("{}\r\n", lines.join("\r\n")))?;
    Ok(replaced)
}
fn remove_hosts(path: &Path) -> std::io::Result<bool> {
    let content = fs::read_to_string(path)?;
    let (mut lines, removed) = strip_block(&content);
    if removed {
        while lines.last().is_some_and(|s| s.trim().is_empty()) {
            lines.pop();
        }
        fs::write(
            path,
            if lines.is_empty() {
                String::new()
            } else {
                format!("{}\r\n", lines.join("\r\n"))
            },
        )?;
    }
    Ok(removed)
}
fn strip_block(content: &str) -> (Vec<String>, bool) {
    let mut inside = false;
    let mut removed = false;
    let mut lines = Vec::new();
    for line in content.lines() {
        if line == BEGIN {
            inside = true;
            removed = true;
            continue;
        }
        if line == END {
            inside = false;
            continue;
        }
        if !inside {
            lines.push(line.to_string());
        }
    }
    (lines, removed)
}
fn flush_dns() -> Result<(), String> {
    let output = Command::new("ipconfig")
        .arg("/flushdns")
        .output()
        .map_err(|e| format!("刷新 DNS 缓存失败：{e}"))?;
    if output.status.success() {
        Ok(())
    } else {
        Err(format!(
            "刷新 DNS 缓存失败：{}",
            String::from_utf8_lossy(&output.stderr).trim()
        ))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn managed_block_is_stripped_without_touching_other_lines() {
        let input="127.0.0.1 localhost\n# >>> osu-network-accel begin >>>\n1.2.3.4 osu.ppy.sh\n# <<< osu-network-accel end <<<\n# custom";
        let (lines, removed) = strip_block(input);
        assert!(removed);
        assert_eq!(lines, vec!["127.0.0.1 localhost", "# custom"]);
    }
    #[test]
    fn samples_belong_to_range() {
        for ip in sample_range("104.16.0.0/12", 2) {
            assert_eq!(
                u32::from(ip) & 0xfff0_0000,
                u32::from(Ipv4Addr::new(104, 16, 0, 0))
            );
        }
    }
}

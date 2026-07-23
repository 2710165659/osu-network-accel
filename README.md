# osu-network-accel

![home](image.png)

一个使用原生 Rust 编写的 Windows GUI 小工具，通过修改 `hosts` 为 osu! 选择当前更快的 Cloudflare IP。

## 功能

- 每个 Cloudflare IPv4 网段随机抽样 2 个 IP
- TCP 443 并发初筛
- 对前 8 个候选进行 `osu.ppy.sh` TLS/HTTP 复测
- 将当前最快 IP 写入 Windows `hosts`
- 只管理自身标记的 hosts 区块，可一键恢复
- 在 `%LocalAppData%\OsuNetworkAccel\last-speed-test.json` 保存测速报告

默认映射 `osu.ppy.sh`、`api.ppy.sh`、`assets.ppy.sh`、`a.ppy.sh`、`b.ppy.sh`、`c.ppy.sh`、`i.ppy.sh` 和 `s.ppy.sh`。

## 本地开发

需要稳定版 Rust MSVC 工具链和 Visual Studio C++ Build Tools。

```powershell
cargo test
cargo run
```

Release 构建通过 Windows manifest 请求管理员权限，因为写入系统 `hosts` 和刷新 DNS 缓存需要提升权限；Debug 构建不嵌入提权 manifest，便于本地测试。

## 发布

构建单文件 Windows 可执行程序：

```powershell
powershell -ExecutionPolicy Bypass -File .\publish-win-x64.ps1
```

产物位于 `publish\osu-network-accel-win-x64\OsuNetworkAccel.exe`，目标机器无需安装 .NET 或 Rust。

推送 `v` 开头的 tag（例如 `v1.0.0`）会触发 GitHub Actions，构建单个 `OsuNetworkAccel.exe` 并创建 GitHub Release：

```powershell
git tag v1.0.0
git push origin v1.0.0
```

# osu-network-accel

一个通过修改 Windows `hosts` 来加速 `osu!` 网络访问的小工具。

- 使用同一批 Cloudflare IPv4 网段做候选
- 每个网段随机抽样 2 个 IP
- 先做 TCP 443 初筛
- 再对前 8 个候选做 `osu.ppy.sh` 的 TLS/HTTP 复测
- 选出当前更快的 IP，写入 `hosts`

## 提供内容

- `OsuNetworkAccel.csproj`
  - 根目录项目文件
- `Program.cs`
  - 根目录主程序
- `publish-win-x64.bat`
  - 一键打包为可分发的 Windows 自包含版本
- `packaging`
  - 发布目录附带的脚本模板

## 默认加速域名

程序会把同一个优选 IP 写到下面这些域名：

- `osu.ppy.sh`
- `api.ppy.sh`
- `assets.ppy.sh`
- `a.ppy.sh`
- `b.ppy.sh`
- `c.ppy.sh`
- `i.ppy.sh`
- `s.ppy.sh`

## 使用方式

### 打包发布

如果你要把程序发给别人用，先双击：

```bat
publish-win-x64.bat
```

打包完成后，可分发目录默认是：

```text
publish\osu-network-accel-win-x64
```

这个目录里会包含：

- `OsuNetworkAccel.exe`
- `accelerate-osu-network.bat`
- `restore-osu-network.bat`

这是自包含发布版本，目标机器不需要安装 .NET Runtime，也不需要 C# 开发环境。最终分发目录只保留这 3 个文件。

### 一键加速

打包后，在发布目录里双击：

```bat
accelerate-osu-network.bat
```

发布版里的脚本会直接调用同目录下的 `OsuNetworkAccel.exe`，脚本会自动申请管理员权限，然后：

1. 进行测速
2. 把优选 IP 写入系统 `hosts`
3. 刷新 DNS 缓存

### 恢复原本网络

打包后，在发布目录里双击：

```bat
restore-osu-network.bat
```

它只会删除本工具写入的 `hosts` 管理块，不会动你原本其它的 `hosts` 内容。

## 本地命令行使用

### 构建

```powershell
dotnet build .\OsuNetworkAccel.csproj -c Release
```

### 加速

```powershell
dotnet run --project .\OsuNetworkAccel.csproj -c Release -- accelerate
```

### 恢复

```powershell
dotnet run --project .\OsuNetworkAccel.csproj -c Release -- restore
```

### 可选参数

- `--hosts-file <path>`
  - 指定自定义 `hosts` 文件，便于测试
- `--app-root <path>`
  - 指定状态目录根路径
- `--no-flushdns`
  - 修改 `hosts` 后不刷新 DNS

## 输出

- 最新测速报告：`.state\last-speed-test.json`

报告里会记录：

- 选中的 IP
- 参与复测的结果
- TCP / HTTP 延迟
- HTTP 状态码

## 注意事项

- 需要管理员权限，因为会修改系统 `hosts`
- 这是 `hosts` 级加速，核心是“强制把 osu 域名解析到当前更快的 Cloudflare IP”
- 如果你当前网络对某些 Cloudflare 节点本来就差，测速结果也可能不理想
- 恢复脚本只移除本项目生成的管理块，所以相对安全

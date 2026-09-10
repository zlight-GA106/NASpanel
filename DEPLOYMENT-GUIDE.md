# Storage Station 部署指南

> 本文保留的是旧版 HTTPS 部署方案。当前版本默认使用 HTTP，无需证书，请按 [最新 IIS 部署指南](deploy/DEPLOYMENT.md) 部署。

适用对象：运行 Windows Server 2022 Datacenter 的八盘位归档服务器。
技术栈：ASP.NET Core 8 / C# 12、SQLite、LibreHardwareMonitor、smartctl、SignalR、原生 HTML/CSS/JS、ECharts、noVNC。
本指南依据仓库中的 `README.md`、`deploy/` 脚本、`StorageStation.Web` 源码与配置整理。

> 说明：开发机上的模拟页面不代表已部署到磁盘站。生产环境始终使用真实硬件提供器，不会自动把真实硬件故障替换成模拟数据。

## 0. 架构、端口与服务

```text
管理电脑浏览器（HTTPS / WSS）
        │ 443
        ▼
IIS :443（唯一网络入口，SNI + HTTPS）
  ├─ /、/css、/js、/vendor          → C:\StorageStation\wwwroot 静态资源
  ├─ /api、/hubs                    → 127.0.0.1:5180（认证 + 反向代理）
  └─ /novnc/websockify              → 127.0.0.1:5180（认证 + Origin 校验后转发）
                                          │
                                          ▼
                                  127.0.0.1:6080 websockify
                                          │
                                          ▼
                                  127.0.0.1:5900 VNC Server
```

| 端口 | 用途 | 绑定要求 |
|---:|---|---|
| 443 | IIS HTTPS，唯一对外入口 | 仅管理网段 |
| 5180 | StorageStation Kestrel | 固定 `127.0.0.1`，代码写死 |
| 6080 | websockify VNC 桥接 | 仅 `127.0.0.1` |
| 5900 | VNC Server | 尽量仅 `127.0.0.1` |

| Windows 服务 | 说明 |
|---|---|
| `StorageStationService` | 主服务，`LocalSystem`，延迟自动启动，失败自动重启 |
| `StorageStationWebsockify` | 可选，NSSM 包装的 websockify |

关键约束：

- Kestrel 在代码中固定监听 `127.0.0.1:5180`，配置 `Kestrel:Endpoints` 会被启动时拒绝。
- 生产环境拒绝非 HTTPS 请求，只信任来自本机回环代理的转发头（`X-Forwarded-For` / `X-Forwarded-Proto`）。
- 远程 WebSocket 必须先经过 ASP.NET Cookie 认证，不要改成 IIS 直连 6080，否则会绕过登录校验。

## 1. 部署前提

| 项目 | 要求 |
|---|---|
| 操作系统 | Windows Server 2022 Datacenter（不能作为群晖 DSM / Linux 原生应用安装） |
| 权限 | 管理员 Windows PowerShell 5.1 |
| 运行时 | ASP.NET Core 8 Runtime（由 .NET 8 Hosting Bundle 提供） |
| 硬件访问 | smartmontools 7.x（支持 JSON）、管理员级权限（LHM 驱动 / SMART 需要） |
| 可选 | Python + NSSM（websockify 服务）、TightVNC / UltraVNC（网页远程桌面） |

服务器不需要 Node.js，也不需要 .NET SDK；浏览器依赖已随发布包分发。

示例值（按实际环境替换）：

| 项目 | 示例 |
|---|---|
| 安装目录 | `C:\StorageStation` |
| 服务器固定内网 IP | `192.168.1.50` |
| 管理网段 | `192.168.1.0/24` |
| 访问域名 | `storage.home.arpa`（或组织 DNS 名称） |

## 2. 在开发机发布

```powershell
# 如 vendor 资源缺失，先执行（需要 Node.js）：
# .\deploy\update-assets.ps1

.\deploy\publish.ps1
```

产物默认在 `artifacts/publish`（`win-x64`、依赖 .NET 8 运行时、self-contained=false）。发布包包含：

- `StorageStation.Web.exe`、依赖 DLL、`appsettings.json`
- `wwwroot/`（含 `web.config`、noVNC、ECharts、SignalR 等本地资源）
- `deploy/`（安装脚本与本文档源文件）、`tools/smartmontools/README.md`

发布包**不包含**开发配置、数据库、Cookie 密钥和运行日志。

## 3. 复制到服务器

把发布目录内容复制到 `C:\StorageStation`（或打成 `StorageStation-win-x64.zip` 后解压）。确认目录下直接包含：

```text
C:\StorageStation\StorageStation.Web.exe
C:\StorageStation\appsettings.json
C:\StorageStation\wwwroot\index.html
C:\StorageStation\deploy\install-service.ps1
C:\StorageStation\deploy\install-iis.ps1
```

不要只复制 `wwwroot`，也不要从源码目录复制开发数据库。

## 4. 安装系统依赖

在服务器管理员 PowerShell 中启用 IIS 功能：

```powershell
Install-WindowsFeature Web-Server,Web-Static-Content,Web-Default-Doc,Web-WebSockets,Web-Mgmt-Console
```

如需重启，重启后继续。然后依次安装官方 Windows x64 安装程序：

1. [.NET 8 Hosting Bundle](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)（ASP.NET Core Runtime 页面的 Hosting Bundle，最新 8.0.x）
2. [IIS URL Rewrite 2](https://www.iis.net/downloads/microsoft/url-rewrite)
3. [IIS Application Request Routing 3 (ARR)](https://www.iis.net/downloads/microsoft/application-request-routing)
4. [smartmontools](https://www.smartmontools.org/)（包含 `smartctl.exe` 的 Windows 版本）

若先装 Hosting Bundle 再装 IIS，需要修复或重装 Hosting Bundle。重新打开管理员 PowerShell 验证：

```powershell
dotnet --list-runtimes
```

应同时出现 `Microsoft.NETCore.App 8.0.x` 与 `Microsoft.AspNetCore.App 8.0.x`。

如果脚本被“下载标记”阻止：

```powershell
Get-ChildItem -LiteralPath C:\StorageStation\deploy -Filter *.ps1 | Unblock-File
```

若提示“禁止运行脚本”且无组织策略限制，可仅在当前终端执行：

```powershell
Set-ExecutionPolicy -Scope Process RemoteSigned
```

## 5. 配置 smartctl 路径

在 `C:\StorageStation` 新建 `appsettings.Local.json`（注意后缀是 `.json` 而不是 `.json.txt`），路径按实际安装位置修改：

```json
{
  "AllowedHosts": "localhost;127.0.0.1;storage.home.arpa",
  "SmartCtl": {
    "Path": "C:\\Program Files\\smartmontools\\bin\\smartctl.exe",
    "TimeoutSeconds": 20
  }
}
```

验证磁盘可见（设备名以扫描结果为准，服务自身不接受用户传入的设备路径）：

```powershell
& 'C:\Program Files\smartmontools\bin\smartctl.exe' --scan-open -j
& 'C:\Program Files\smartmontools\bin\smartctl.exe' -x -j /dev/pd0
```

`AllowedHosts` / `SmartCtl:Path` / `SmartCtl:TimeoutSeconds` 属于启动级配置，不在 Web 设置 API 暴露；运行中的其他设置保存在 SQLite，优先级高于 JSON 文件。

RAID 卡没有开放物理盘透传时，软件无法显示全部物理盘 SMART，需要控制器支持的透传方案。

## 6. 初始化生产管理员

```powershell
Set-Location C:\StorageStation
$env:ASPNETCORE_ENVIRONMENT = 'Production'
.\StorageStation.Web.exe --init-admin
```

- 提示输入 12–256 字符密码，输入不回显；只保存 PasswordHasher 哈希。
- 首次用户名为 `Admin`；生产环境**不会创建默认密码**。
- 该命令只初始化数据库，不启动硬件 Worker。

忘记密码时：先停止服务，再执行同一命令重置（已有用户名会保留）：

```powershell
Stop-Service StorageStationService
Set-Location C:\StorageStation
$env:ASPNETCORE_ENVIRONMENT = 'Production'
.\StorageStation.Web.exe --init-admin
Start-Service StorageStationService
```

不要把密码写入 JSON、命令参数或部署脚本。

## 7. 注册主服务

```powershell
.\deploy\install-service.ps1 -InstallDirectory C:\StorageStation
Get-Service StorageStationService
```

脚本行为：

- 校验 `StorageStation.Web.exe` 与 `Data/monitor.db`（必须先完成第 6 步）。
- 创建 `Data`、`logs`，ACL 限制为 `SYSTEM` 与 `Administrators` 可写。
- 注册 `StorageStationService`：`LocalSystem`、自动延迟启动、失败后按 10s / 30s / 60s 重启。
- 设置服务环境变量 `ASPNETCORE_ENVIRONMENT=Production` 并启动服务。

状态应为 `Running`，服务随系统启动，不需要保持 PowerShell 窗口。

## 8. HTTPS 证书与 IIS

### 8.1 准备证书

使用组织 CA / 正式证书时，将证书与私钥导入“本地计算机 → 个人”，证书主机名匹配管理站 DNS：

```powershell
Get-ChildItem Cert:\LocalMachine\My | Select-Object Subject,Thumbprint,NotAfter,HasPrivateKey
```

纯内网且无现成证书时，可创建自签名证书（管理电脑需按第 8.4 步信任）：

```powershell
$stationCert = New-SelfSignedCertificate `
  -Type SSLServerAuthentication `
  -DnsName 'storage.home.arpa' `
  -CertStoreLocation 'Cert:\LocalMachine\My' `
  -FriendlyName 'Storage Station HTTPS' `
  -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
  -KeyExportPolicy NonExportable `
  -NotAfter (Get-Date).AddYears(2)

Export-Certificate -Cert $stationCert -FilePath C:\StorageStation\storage.home.arpa.cer
$stationCert.Thumbprint
```

### 8.2 配置 IIS

```powershell
Set-Location C:\StorageStation
.\deploy\install-iis.ps1 `
  -InstallDirectory C:\StorageStation `
  -Hostname storage.home.arpa `
  -CertificateThumbprint $stationCert.Thumbprint
```

脚本行为：

- 检查证书存在且有私钥；检查 URL Rewrite 2 与 ARR 3 已安装（否则报错退出）。
- 启用 ARR 代理、保留 Host、允许设置 `HTTP_X_FORWARDED_PROTO`。
- 创建 `StorageStation` 应用池（No Managed Code）与 HTTPS 443 SNI 站点。
- 站点物理目录为 **`C:\StorageStation\wwwroot`**（不是程序目录），使 SQLite、配置、日志、密钥不在静态发布树中。
- 复制 `deploy/iis-web.config` 到 `wwwroot/web.config`，并授予应用池读取权限。
- 更新 `appsettings.Local.json` 的 `AllowedHosts` 并重启主服务。

脚本遇到同名站点会停止，不会覆盖已有站点；已有 IIS 环境请先检查绑定与 ARR 全局设置。

### 8.3 web.config 行为

`deploy/iis-web.config` 负责：

- 非 HTTPS 请求返回 403。
- `api`、`hubs`、`novnc/websockify` 重写到 `http://127.0.0.1:5180`，并覆盖 `X-Forwarded-Proto=https`。
- `/storage/1..8` → `disk.html`，`/login`、`/system` 等 → 对应 `.html`。
- 安全响应头（CSP、HSTS、X-Frame-Options、nosniff 等）、禁用缓存、请求体上限 64 KB。

不要把 publish 根目录中 ASP.NET 自动生成的 `web.config` 当作本项目 IIS 站点配置。

### 8.4 管理电脑配置 hosts 与证书信任

若网络已有 DNS 记录，跳过 hosts。否则在**管理电脑**（管理员权限记事本）编辑：

```text
C:\Windows\System32\drivers\etc\hosts
```

添加（保留原有内容）：

```text
192.168.1.50 storage.home.arpa
```

把服务器导出的 `.cer` 复制到管理电脑，核对指纹后以浏览器用户导入信任库：

```powershell
Get-PfxCertificate -FilePath C:\Temp\storage.home.arpa.cer | Select-Object Subject,Thumbprint
Import-Certificate -FilePath C:\Temp\storage.home.arpa.cer -CertStoreLocation Cert:\CurrentUser\Root
```

只导入公钥证书，私钥保留在服务器；关闭并重开浏览器。每台需访问的管理电脑都要配置。自签名证书到期前需重新生成/绑定并分发新公钥证书。

## 9. 防火墙与监听检查

在**磁盘站**执行，网段替换为实际管理网段：

```powershell
New-NetFirewallRule -DisplayName 'Storage Station HTTPS' `
  -Direction Inbound -Action Allow -Protocol TCP `
  -LocalPort 443 -RemoteAddress 192.168.1.0/24

New-NetFirewallRule -DisplayName 'Storage Station internal ports' `
  -Direction Inbound -Action Block -Protocol TCP `
  -LocalPort 5180,5900,6080
```

检查已有范围更宽的 IIS/HTTPS 允许规则；新增窄规则不会自动缩小旧规则。验证监听：

```powershell
Get-NetTCPConnection -State Listen |
  Where-Object LocalPort -in 443,5180,5900,6080 |
  Select-Object LocalAddress,LocalPort,OwningProcess
```

`5180` / `6080` 应只出现 `127.0.0.1`，`5900` 也应尽量只绑定回环。不需要路由器公网端口转发。完成后从另一台管理电脑确认：443 可访问，三个内部端口不可访问。

## 10. 可选：启用网页远程桌面（noVNC）

不需要网页远程桌面时可先跳过，之后再配置。

1. 安装 [TightVNC Server](https://www.tightvnc.com/) 或 [UltraVNC](https://uvnc.com/)，注册为 Windows 服务，设置独立 VNC 密码，端口 5900，启用 loopback-only / localhost 限制并验证实际监听地址。若产品只能监听所有地址，用 Windows 防火墙阻止 5900 外部访问。
2. 安装 [Python](https://www.python.org/downloads/windows/) 与 [NSSM](https://nssm.cc/download)。
3. 安装 websockify 桥接：

```powershell
Set-Location C:\StorageStation
.\deploy\install-websockify.ps1 `
  -InstallDirectory C:\StorageStation `
  -NssmPath C:\Tools\nssm\win64\nssm.exe

Get-Service StorageStationWebsockify
```

脚本创建 `tools\websockify-venv`、安装固定版本 `websockify==0.13.0`，并通过 NSSM 注册 `StorageStationWebsockify` 服务（延迟启动、退出重启），监听 `127.0.0.1:6080` → `127.0.0.1:5900`。不加 `-NssmPath` 时只安装依赖并打印前台启动命令：

```powershell
& 'C:\StorageStation\tools\websockify-venv\Scripts\python.exe' -m websockify 127.0.0.1:6080 127.0.0.1:5900
```

不要把裸 Python 程序用 `sc create` 注册成不支持 SCM 协议的服务。VNC 密码只存在当前浏览器会话内存，不写入数据库、不放在 URL 中。`GET /api/remote/status` 可检查 5900 / 6080 可用性。

## 11. 首次登录与硬件配置

浏览器打开 `https://storage.home.arpa/`，用 `Admin` 和第 6 步设置的密码登录。开发页面的 `http://localhost:5180` 不是远程访问地址。

1. 确认页面没有“开发模式 / 模拟硬件”提示。
2. **系统页**：检查实际 CPU、内存与已发现传感器（名称、类型、Identifier、可写能力会写入服务日志）。
3. **设置页**：绑定 CPU 温度、机箱风扇 RPM 与可写 Control Identifier（CPU 自动发现使用最热 CPU 温度，不硬编码名称）。
4. **设置页**：按序列号把服务器发现的磁盘分别绑定到 BAY 01–08；先核对实物盘位，不能重复分配，未绑定盘显示在“未分配磁盘”。
5. 确认主板可控后再勾选软件风扇控制，保存后到风扇页开启自动曲线。第一次上机观察 PWM 与实际 RPM 是否对应。
6. **用户账户**：修改用户名（1–40 个字母/汉字/数字/`. _ -`）、重置密码（12–256 字符）、上传头像（≤5 MB，浏览器裁剪为 96×96 PNG）。凭据变更会断开旧会话与实时连接。
7. 左上角铅笔按钮或设置页可修改主机显示名称（1–80 字符，仅控制台显示，不改 Windows 计算机名/DNS/证书）。

若主板只暴露 RPM、不暴露可写 Control，UI 保持只读并说明原因，继续使用 BIOS 控制风扇，监控功能不受影响。

## 12. 日常维护

### 12.1 状态与重启

```powershell
Get-Service StorageStationService
Restart-Service StorageStationService
```

日志：`C:\StorageStation\logs\storage-station-YYYYMMDD.log`（按日滚动，单文件 10 MB，最多 30 个）。

### 12.2 备份

```powershell
Stop-Service StorageStationService
# 备份整个 Data 目录与 appsettings.Local.json
Start-Service StorageStationService
```

SQLite 使用 WAL：服务运行时不要只复制单个 `.db` 文件；停服后备份整个 `Data` 目录，或使用 SQLite 正规 backup 工具。Cookie 密钥在 `Data/keys`（Windows 下用机器 DPAPI 加密），迁移机器后旧会话需重新登录。

### 12.3 升级

1. 停止主服务，备份 `Data` 整个目录与 `appsettings.Local.json`。
2. 用新发布文件覆盖程序文件，**保留** `Data` / `logs` / `appsettings.Local.json`。
3. 启动服务，不要重复运行首次安装脚本。

### 12.4 卸载

```powershell
.\deploy\uninstall-service.ps1   # 仅移除服务注册，保留文件
```

websockify 按 NSSM 正常 stop/remove 流程移除。网站、证书与防火墙规则由管理员按实际环境处理。

### 12.5 默认运行参数

| 项目 | 默认 | 范围 / 行为 |
|---|---:|---|
| 实时推送 | 1000 ms | 1000–10000 ms |
| 磁盘温度 | 30 s | 15–300 s，`-i -A -j` |
| 完整 SMART | 300 s | 60–3600 s，`-x -j` |
| 系统历史写库 | 10 s | 保留 30 天 |
| SMART 历史 | 完整采样时 | 保留 365 天 |
| 事件保留 | 365 天 | 确认不删除 |
| 清理 | 每 6 小时 | 小批量删除 + WAL checkpoint |
| 磁盘告警 | 45 / 50 °C | Warning / Critical |
| 手动风扇 | 最长 60 min | 重启不恢复，超时回自动 |
| 周/月自检 | 默认关闭 | UTC ISO 周/月，每盘每周期最多一次 |

风扇默认曲线：≤32°C→30%、35°C→35%、40°C→50%、45°C→70%、≥50°C→100%（线性插值）。CPU ≥70°C 最低 70%；CPU ≥80°C 或硬盘 ≥50°C 立即 100%；温度缺失/过期时软件模式输出 100%；失速（PWM>30% 且 RPM=0 持续 10 s）锁定保护 100%。

> 风扇安全提示：进程被强制终止、系统崩溃、驱动死锁或断电时，用户态代码无法保证恢复 BIOS 默认控制。BIOS 应设置保守风扇策略；老主板若无法可靠交还控制，应保持 BIOS 模式或使用带硬件 watchdog 的独立 PWM 控制器。

## 13. 故障排查

| 现象 | 检查 |
|---|---|
| 生产登录失败 | 是否用 Production 执行过 `--init-admin`；数据库不在 System32；服务环境不是 Development |
| 400 HTTPS required | 是否经 IIS HTTPS 访问；ARR 是否覆盖 `X-Forwarded-Proto`、保留 Host |
| IIS 400 无效 Host | `appsettings.Local.json` 的 `AllowedHosts` 是否包含浏览器域名 |
| IIS 500.19 / 500.50 | URL Rewrite / ARR 是否安装；WebSocket 与 allowedServerVariables 是否启用 |
| IIS 502 | `StorageStationService` 是否运行；127.0.0.1:5180 是否监听；查看 `logs` |
| 域名打不开 | hosts / DNS；管理网段与 443 防火墙规则 |
| 证书错误 | 地址与证书域名是否一致、是否过期、公钥证书是否导入当前用户信任库 |
| SignalR 一直重连 | WebSocket Protocol、代理路由、Cookie/Origin、证书信任、登录是否过期 |
| CPU / RPM 为 `--` | 服务权限、LHM 驱动、主板芯片支持；检查 `/api/sensors` 与日志 |
| 能读 RPM 不能控制 | 常见硬件限制；配置可写 Identifier，不存在则使用 BIOS |
| 风扇一直 100% | 安全原因、温度过期、已映射盘离线、RPM 失速保护 |
| SMART 暂不可用 | smartctl 路径、管理员权限、USB bridge 类型、RAID 透传、20 s 超时 |
| 没有真实硬盘 | 先在管理员 PowerShell 验证 smartctl 扫描，再查 SATA/USB/RAID 透传 |
| BAY 空 / 磁盘未分配 | 设置页按序列号绑定，核对机箱实际槽位 |
| SMART PASSED 仍告警 | 检查 Pending、Uncorrectable、重映射增长、温度、自检、CRC |
| 自检显示英文 | smartctl 原始状态被完整保留，属性与厂商字段不翻译 |
| 远程桌面不可用 | VNC :5900、websockify :6080、VNC 密码、IIS 转发；核对 `GET /api/remote/status` |
| 改 JSON 后设置不变 | 已保存的 StationSettings 优先，从设置页修改；启动级路径/Host 仍读文件 |
| 设置保存后风扇变 BIOS | 预期行为：换绑前恢复硬件默认，去风扇页重新选择自动模式 |

## 14. 安全与运行约束

- 唯一网络入口是 IIS 443，5180 / 5900 / 6080 全部限制回环；不要新增公网转发。
- 生产 Cookie：HttpOnly / Secure / SameSite=Strict，8 小时过期、不自动续期；SignalR 与远程 WebSocket 随登录过期关闭。
- 写操作 API 需要 CSRF Token（`GET /api/auth/csrf`，登录后重新获取，放入 `X-CSRF-TOKEN` Header），Cookie 一起发送。
- 登录限流：每 IP 每分钟 8 次；改密每分钟 5 次。
- 所有 smartctl 参数由服务端构造，前端只提交已发现的磁盘 ID 与固定自检类型。
- 自检仅调用 `smartctl -t short` / `-t long`，不带 `-C`，不执行擦除/格式化/修复；自检不能替代备份。
- 单管理员账户，无注册、多用户、邮件找回；密码用 PasswordHasher 哈希存储。
- 不要为了显示传感器而关闭系统安全功能（驱动签名、内存完整性等）。

## 15. 部署验收清单

- [ ] `dotnet --list-runtimes` 同时有 NETCore.App 8.0.x 与 AspNetCore.App 8.0.x
- [ ] `smartctl --scan-open -j` 能列出物理盘
- [ ] `--init-admin` 已用 Production 执行，生产无默认密码
- [ ] `StorageStationService` 为 Running 且为延迟自动启动
- [ ] `https://<域名>/` 打开无证书错误，登录成功
- [ ] 页面无“开发模式 / 模拟硬件”提示
- [ ] `Get-NetTCPConnection` 确认 5180 / 6080（及 5900）仅回环，443 仅管理网段可达
- [ ] BAY 01–08 已按实物绑定序列号，传感器与风扇可写 Identifier 已配置
- [ ] 备份方案覆盖整个 `Data` 目录与 `appsettings.Local.json`
- [ ] （可选）`StorageStationWebsockify` Running，网页远程桌面可连接

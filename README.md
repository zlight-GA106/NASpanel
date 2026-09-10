# Storage Station

[下载 Windows x64 部署包](artifacts/StorageStation-win-x64.zip) · [IIS 部署指南（默认 HTTP，无需证书）](deploy/DEPLOYMENT.md)

Windows Server 2022 Datacenter 单机服务器管理控制台，支持可配置的 1–64 个磁盘架盘位。ASP.NET Core 8 / C# 12、SQLite、LibreHardwareMonitor、smartctl、SignalR、原生 HTML/CSS/JavaScript、ECharts 和 noVNC。

这是一套包含真实硬件提供器与部署脚本的工程。开发环境默认使用模拟硬件，生产环境始终使用真实提供器；不自动把真实硬件故障替换成模拟数据。没有 Docker、Grafana、SPA 框架或 Node.js 后端。

## 开发环境启动

需要 Windows、.NET 8 SDK 或能构建 net8.0 的更新 SDK，以及 .NET 8 ASP.NET Core 运行时。浏览器依赖已经放在 `wwwroot`，正常构建和运行不需要 npm。

```powershell
dotnet restore StorageStation.sln
dotnet build StorageStation.sln
dotnet test StorageStation.sln
dotnet run --project StorageStation.Web --launch-profile Development
```

访问 **http://127.0.0.1:5180/**。

开发账户：`Admin` / `StorageStation!Dev2026`。这个默认账户只在 Development 的独立数据库初始化，**生产环境不会创建默认密码**。

Development 提供变化的 CPU、内存、温度、RPM、8 块模拟磁盘；BAY 03 有稳定重映射扇区警告，BAY 06 有稳定 CRC 警告。模拟自检约 1 分钟。页面明确标注模拟环境。只读传感器与真实设备控制不会在开发模式启动。

## 已实现

- 总览、系统、存储、磁盘详情、风扇、远程控制、事件、设置、登录共 9 个独立页面。
- SignalR 实时系统推送、断线重连，后台缓存响应 REST 请求。
- LHM 递归硬件与传感器发现，Identifier 绑定，CPU 温度优先选择 CPU Package 并排除 Distance to TjMax，风扇自动选择有效 RPM，Windows 内存数据。
- smartctl JSON 扫描、稳定身份、固定八盘位、未分配磁盘、三次扫描缺席判定离线、恢复事件。
- HDD / SATA SSD / NVMe 健康判断、完整属性、温度及 SMART 历史、短/扩展自检、默认关闭的周期自检。
- 自动曲线插值、防抖、手动到期、紧急输出、失速保护、退出恢复 BIOS，以及只读风扇兼容。
- SQLite 历史、事件确认、持久配置、后台清理；Windows System 警告/错误事件采集。
- Cookie 登录、密码哈希、CSRF、登录限流、认证后的 noVNC WebSocket 转发。
- 单管理员用户名修改、当前密码验证后的密码重置、头像上传；凭据变更使旧会话和连接失效。
- 本地浏览器资源、IIS 配置、发布和 Windows Service 脚本。

当前开发机上的运行检查使用模拟提供器。生产服务器仍需逐台核对 SMART、主板可写控制器与风扇对应关系；代码不保证不受支持的主板一定能控制风扇。

## 工程布局

```text
StorageStation.sln
StorageStation.Web/
  Program.cs                  服务注册、管道与启动
  Models/                     DTO 与配置校验
  Hardware/                   LHM、传感器发现、IFanController、模拟硬件
  Storage/                    smartctl 进程、JSON 解析、模拟磁盘
  Services/                   风扇状态机、磁盘健康、缓存、设置、事件
  Workers/                    独立后台采集、控制、清理与计划任务
  Data/StationDatabase.cs     SQLite 访问与 schema 初始化
  Security/                   管理员密码哈希
  Endpoints/                  登录、管理 API、noVNC 代理
  Hubs/                       SignalR
  wwwroot/                    独立 HTML、CSS、JS 和本地 vendor 资源
StorageStation.Tests/         仅关键逻辑测试，无浏览器测试框架
deploy/                       发布、服务、IIS、websockify 脚本
tools/smartmontools/          安装说明
```

## 网络与认证

```text
浏览器 HTTP / WS（可选 HTTPS / WSS）
        │
        ▼
IIS :8080                        默认网络入口
  /、/css、/js、/vendor           发布目录 wwwroot 的静态资源
  /api、/hubs                     → 127.0.0.1:5180
  /novnc/core、/novnc/vendor      noVNC 静态资源
  /novnc/websockify               → 127.0.0.1:5180（认证 + Origin 校验）
                                      │
                                      ▼
                                 127.0.0.1:6080 websockify
                                      │
                                      ▼
                                 127.0.0.1:5900 VNC Server
```

远程路径特意先经过 ASP.NET 认证。直接从 IIS 转发到 websockify 会绕过控制台 Cookie 验证，因此不要把该路径改成直接转发 6080。

登录 Cookie 为 HttpOnly / SameSite=Strict，8 小时过期、不自动续期；通过 HTTPS 访问时使用 Secure Cookie。SignalR 与远程 WebSocket 会在登录过期时关闭。操作 API 要求 CSRF Token；SignalR 的协商请求是受认证的连接过程，单独检查 Origin。所有 smartctl 参数由服务端构造，前端仅提交已发现的磁盘 ID 与固定自检类型。

Kestrel 在代码中固定只监听 `127.0.0.1:5180`，拒绝额外 `Kestrel:Endpoints` 配置。默认 `Security:RequireHttps=false`，生产环境支持 IIS HTTP，无需域名或证书。IIS 保留 Host，并根据实际入口协议覆盖 `X-Forwarded-Proto=http/https`；后端只信任本机代理的转发头。需要 HTTPS 时再明确启用，认证 Cookie 与 CSRF Cookie 会随协议匹配。

## Windows Server / IIS 部署

**默认使用 HTTP，安装后访问 `http://服务器IP:8080/`，无需配置证书或域名。** 完整逐步教程：[IIS 部署指南](deploy/DEPLOYMENT.md)。

1. 运行 `deploy/publish.ps1` 发布，或使用已经打包的 `artifacts/StorageStation-win-x64.zip`。将发布目录内容解压到服务器 `C:\StorageStation`。
2. 启用 IIS、静态内容和 WebSocket，安装 .NET 8 Hosting Bundle、URL Rewrite 2、ARR 3、smartmontools。安装顺序和官方下载链接见部署指南。
3. 在程序目录创建 `appsettings.Local.json`，填写实际的 smartctl 路径：

```json
{
  "AllowedHosts": "*",
  "Security": { "RequireHttps": false },
  "SmartCtl": {
    "Path": "C:\\Program Files\\smartmontools\\bin\\smartctl.exe",
    "TimeoutSeconds": 20
  }
}
```

4. 在服务器管理员 Windows PowerShell 中初始化生产管理员、安装主服务及 IIS 站点：

```powershell
Set-Location C:\StorageStation
$env:ASPNETCORE_ENVIRONMENT = 'Production'
.\StorageStation.Web.exe --init-admin
.\deploy\install-service.ps1 -InstallDirectory C:\StorageStation
.\deploy\install-iis.ps1 -InstallDirectory C:\StorageStation
New-NetFirewallRule -DisplayName 'Storage Station HTTP' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 8080 -RemoteAddress LocalSubnet
```

IIS 站点物理目录为 `C:\StorageStation\wwwroot`，应用池使用 No Managed Code。后端由 `StorageStationService` 承载，固定回环 5180，不能把程序根目录直接作为静态网站根目录，也不需要 IIS 再启动第二份后端。

主服务以 LocalSystem 自动延迟启动并配置故障重启。正常停止时会尝试将风扇控制交还 BIOS；强制终止或断电时无法保证执行恢复代码，因此第一次配置时应核对主板与风扇对应关系。

安装脚本默认使用空主机名和端口 8080，直接支持 IP 访问；需要 80 端口时传入 `-Port 80`，已有端口冲突会明确报错，不停止其他网站。传入 `-CertificateThumbprint` 才启用 HTTPS，未指定端口时切换为 443，并设置 `Security:RequireHttps=true`。从旧版 HTTPS 配置改为 HTTP 的步骤见部署指南。

网页远程桌面可随后安装 VNC Server 和 `deploy/install-websockify.ps1`，无需改动监控后端；配置细节同样见部署指南。

### 局域网唤醒

在与磁盘站相同局域网的 Windows 电脑上运行独立脚本：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Wake-StorageStation.ps1
```

脚本默认查找 `192.168.100.145/24`，先通过网络邻居表自动取得 MAC，再向磁盘站网段广播 Wake-on-LAN 魔术包；MAC 不写死在脚本中。当前电脑与磁盘站不在同一子网时，首次运行会通过 Windows SSH 查询磁盘站网卡并要求输入 SSH 密码。成功发现后，IP/MAC 保存到 `%LOCALAPPDATA%\StorageStation\wol-target.json`，以后磁盘站关机时直接使用缓存，不再需要 SSH。地址或掩码改变时可传入 `-ServerIp 新地址 -TargetPrefixLength 前缀长度 -SshUser 用户名`，并在目标开机时运行一次以更新缓存。跨子网唤醒还要求路由器允许把定向广播转发到磁盘站网段；主板 BIOS/UEFI 与网卡也需启用 Wake-on-LAN。

TightVNC 已配置为接受控制台账户密码。标准 VNC 认证只校验密码的前 8 个字符，所以在远程桌面弹窗中输入完整控制台密码可以连接，但第 9 位以后的字符不会增加 VNC 认证强度。

## 首次硬件配置

1. 打开 `http://服务器IP:8080/`，使用生产 Admin 登录。
2. 系统页查看所有已发现传感器；名称、类型、Identifier 和可写能力会显示，首次发现也写入服务日志。
3. 设置页绑定 CPU 温度、机箱风扇 RPM、对应可写 Control Identifier。自动发现优先使用 CPU Package 和大于 0 的风扇 RPM；LibreHardwareMonitor 0.9.6 的底层传感器读取需要安装官方签名的 [PawnIO](https://github.com/namazso/PawnIO.Setup/releases/latest)。
4. 设置页把服务器发现的序列号分别绑定到 BAY 01–08。未绑定盘仍会显示在“未分配磁盘”中。不能将一块盘重复分配到多个 BAY。
5. 确认主板可控再勾选软件风扇控制；保存后去风扇页开启自动曲线。第一次上机建议观察 PWM 与实际 RPM 是否正确对应。

点击左上角主机名称旁的铅笔按钮即可重命名，也可在设置页的“主机显示名称”旁点击“重命名”。名称为 1–80 字符，保存到数据库，顶部、面包屑和浏览器标题会同步更新，刷新与服务重启后保留。此操作只修改控制台显示名称，不修改 Windows 计算机名、DNS 或证书，也不切换风扇模式。

设置页的“用户账户”支持三个独立操作，不触发风扇设置变更：

- **重命名用户**：修改实际登录用户名，支持 1–40 个字母、汉字、数字及 `. _ -`。当前页面保持登录，其他旧会话失效，下次登录使用新用户名。
- **重置密码**：输入当前密码、新密码和确认密码，新密码为 12–256 字符；成功后所有设备需重新登录，已建立的实时监控与远程桌面连接会断开。忘记当前密码时，在服务器停止主服务后使用 `--init-admin` 重置，已有用户名保留，命令提示会显示它。
- **上传头像**：选择不超过 5 MB 的 PNG、JPG 或 WebP，浏览器居中裁剪为 96 × 96 PNG 后上传；服务端检查大小、格式、尺寸与压缩数据。头像保存在 SQLite，随 Data 目录备份，不写入网站静态目录。

升级时会自动迁移旧版 Admin 密码哈希，保留原密码；旧版登录 Cookie 需重新登录一次。仍为单管理员账户，不增加注册、多用户或邮件找回服务。

ASUS B85M-K 可能只暴露 RPM 而不暴露可写 Control。此时 UI 保持只读并说明原因，其他监控继续运行。硬件控制通过 `IFanController` 分离；将来可实现 SerialFanController，不需要改写温控业务逻辑。

默认关闭 LHM 的 Storage 轮询，由 smartctl 独立管理磁盘 I/O，避免每秒更新硬件时唤醒磁盘。部分板载传感器受驱动签名、内存完整性或芯片支持限制时显示 `--`；不要为了显示传感器而盲目关闭系统安全功能。

## 配置与保留策略

首次启动读取 `appsettings.json`，再叠加环境配置、本地配置、环境变量。运行中设置保存到 SQLite `settings`，下次启动优先使用已保存的 StationSettings。`AllowedHosts` / `Security:RequireHttps` / `SmartCtl:Path` / `SmartCtl:TimeoutSeconds` 属于服务启动配置，不在 Web 设置 API 暴露。

生产数据库：`Data/monitor.db`；开发数据库：`Data/monitor.development.db`。Cookie 密钥：`Data/keys`，Windows 下用机器 DPAPI 加密。

| 项目 | 默认 | 范围 / 行为 |
|---|---:|---|
| 实时推送 | 1000 ms | 1000–10000 ms |
| CPU / 内存采集 | 随实时周期 | 温度、主板、RPM 慢数据约 2 秒 |
| 磁盘温度 | 30 秒 | 15–300 秒，使用较轻的 `-i -A -j` |
| 完整 SMART | 300 秒 | 60–3600 秒，`-x -j`；首次发现和运行中自检可提前刷新 |
| 系统历史写库 | 10 秒 | 默认保留 30 天 |
| SMART 历史写库 | 完整采样时 | 默认保留 365 天 |
| 事件保留 | 365 天 | 确认不删除 |
| 清理 | 每 6 小时 | 小批量删除、WAL checkpoint |
| 历史 API | 按时间桶聚合 | 每曲线约 720 点，避免全量传输 |
| 日志 | 按日滚动 | 每文件最多 10 MB，最多 30 个文件 |
| 磁盘告警 | 45 / 50°C | Warning / Critical |
| 手动 | 最长 60 分钟 | 重启不恢复旧手动低速，超时回自动 |
| 周/月自检 | 默认关闭 | UTC ISO 周/月；开启后依次执行，每周期每盘最多一次计划尝试 |

自检计划会在启用后的下一次检查开始执行，按周/月记账，不提供复杂日历编辑。失败尝试写事件且同周期不自动反复重试，可手动重试。没有重复的操作系统计划任务调度层。

### 短自检、扩展自检与文件安全

两者都是由硬盘固件执行的 SMART 诊断，具体支持与检查范围取决于硬盘：

| 类型 | 检查内容 | 大致耗时 |
|---|---|---|
| 短自检 | 快速检查电气、机械及部分读取能力 | 通常几分钟，ATA 硬盘一般少于 10 分钟 |
| 扩展自检 | 更全面的读取与介质检查，机械硬盘通常覆盖整个盘面 | 数十分钟到数小时，大容量或繁忙硬盘可能更久 |

程序仅调用后台 `smartctl -t short` / `-t long`，不带独占前台参数 `-C`，不执行擦除、格式化或修复命令。这些自检不会主动覆盖或删除用户文件，也不校验文件内容是否与原始备份一致。检测期间仍可访问文件，但读写速度可能降低，繁忙 I/O 也可能延长测试时间。扩展自检适合在空闲时逐盘运行。

若硬盘已出现异响、掉盘、读错误或重要文件没有备份，应先备份或抢救数据，再进行长时间检测；自检通过也不能保证以后不故障。开发模式的模拟自检不读取真实硬盘。

依据：[smartctl 官方手册的自检参数说明](https://github.com/smartmontools/smartmontools/blob/main/src/smartctl.8.in)、[Seagate 对硬盘固件自检与数据安全的说明](https://www.seagate.com/ca/en/manuals/software/seatools-bootable/using-seatools/)。

系统历史和 SMART 属性不存在时显示未知，不伪造采样。完整 SMART 健康使用最近两次历史样本加当前样本判断持续增长；温度刷新不会把同一个样本当作新的劣化样本。

### 风扇默认参数

| 温度 | PWM |
|---:|---:|
| ≤32°C | 30% |
| 35°C | 35% |
| 40°C | 50% |
| 45°C | 70% |
| ≥50°C | 100% |

中间线性插值，37.5°C → 42.5%。默认温度源是最高硬盘温度，可改为 CPU 或 MAX(CPU, 硬盘)。所有已知在线磁盘参与最高温度，已映射盘离线/温度过期会使软件控制进入保护。

- CPU ≥70°C：最低 70%；CPU ≥80°C 或硬盘 ≥50°C：立即 100%。紧急保护可突破用户配置的正常最大 PWM。
- 必需温度缺失/过期：软件模式输出 100%。BIOS 模式保持硬件控制权，不偷偷切入软件模式。
- 升速回差 1°C / 等待 3 秒，降速回差 2°C / 等待 30 秒，正常每次 PWM 变化最多 10%。紧急保护绕过延迟和步长。
- 首次进入软件自动控制从 100% 安全起步，再按降速规则稳定下来。
- PWM >30%、RPM=0 持续 10 秒：严重事件；软件模式锁定保护 100%，直到检测到 RPM 恢复。
- 可配置最低安全 RPM；不使用统一的激进 RPM 阈值。当前没有自动学习历史转速基准。

## 主要 API

所有管理 API 需要登录。写操作通过 `GET /api/auth/csrf` 获取 Token（登录后重新获取），再放入 `X-CSRF-TOKEN` Header；Cookie 也必须一起发送。

```text
GET  /api/auth/csrf
POST /api/auth/login                  { username, password }
GET  /api/auth/me
PUT  /api/auth/username               { username }
PUT  /api/auth/password               { currentPassword, newPassword }
PUT  /api/auth/avatar                 { image: "data:image/png;base64,..." }
POST /api/auth/logout
GET  /api/system
GET  /api/system/history?hours=24
GET  /api/sensors
GET  /api/disks
GET  /api/disks/{id}
GET  /api/disks/{id}/history?hours=720
POST /api/disks/{id}/selftest/short
POST /api/disks/{id}/selftest/long
GET  /api/fans
PUT  /api/fans/chassis/mode            { mode: "auto" | "bios" }
PUT  /api/fans/chassis/speed           { percent: 65, minutes: 30 }
PUT  /api/fans/chassis/curve           { curve: [...], temperatureSource: "disk" }
GET  /api/events?level=warning&page=1
POST /api/events/{id}/acknowledge
GET  /api/settings
PUT  /api/settings/display-name       { displayName: "归档磁盘站" }
PUT  /api/settings                    完整 StationSettings
PUT  /api/settings/bays/{1..8}         { serial: "..." | null }
GET  /api/remote/status
WS   /hubs/realtime                   system / disks 消息
WS   /novnc/websockify                认证后的 VNC WebSocket
```

盘位绑定存序列号；历史使用型号与序列号/WWN 的哈希 ID。更换 Windows PhysicalDrive 编号不会改变历史身份。没有序列号或 WWN 的设备会报告读取失败，不以不稳定路径冒充永久身份。

## 备份、升级、卸载

```powershell
Stop-Service StorageStationService
# 确认服务停止后，备份 Data 整个目录与 appsettings.Local.json。
# 覆盖新的发布文件，保留 Data / logs / appsettings.Local.json。
Start-Service StorageStationService
```

SQLite 使用 WAL。服务运行时不要只复制单个 `.db` 文件；停服后备份整个 Data 目录，或使用 SQLite 正规 backup 工具。迁移机器时机器 DPAPI 密钥可能无法解密旧 Cookie 密钥，应重新建立登录会话。数据库 schema 目前版本 1，后续结构升级应新增显式迁移。

主服务卸载：`deploy/uninstall-service.ps1`，仅移除服务注册，保留文件。websockify 服务按 NSSM 正常 stop/remove 流程移除。网站、证书与防火墙规则由管理员按实际环境管理，卸载脚本不递归删除数据或重置整个 IIS。

## 常见故障

| 现象 | 检查 |
|---|---|
| 生产登录失败 | 确认用 Production 执行过 `--init-admin`，数据库不在 System32，服务不是 Development。 |
| 提示强制 HTTPS | 默认已关闭；检查 appsettings.Local.json 或环境变量是否仍将 Security:RequireHttps 设为 true，并检查 IIS 是否仍启用“要求 SSL”。 |
| IIS 400 无效 Host | 按 IP 访问时将 AllowedHosts 设为 *，或明确包含实际访问的 IP / 主机名。 |
| IIS 500.19 / 500.50 | URL Rewrite / ARR 是否安装，WebSocket 功能与 allowedServerVariables 是否启用。 |
| IIS 502 | StorageStationService 是否运行，127.0.0.1:5180 是否监听；查看 `logs`。 |
| SignalR 一直重连 | WebSocket Protocol、代理路由、Cookie 和 Origin，X-Forwarded-Proto 是否与实际协议一致，登录是否过期。 |
| CPU / RPM 为 `--` | 服务权限、LHM 驱动、主板芯片支持情况；检查 `/api/sensors` 和日志。 |
| 可以读 RPM 但不能控制 | 常见硬件限制。配置可写 Identifier，若不存在则使用 BIOS。 |
| 风扇一直 100% | 查看安全原因、温度是否过期、是否有离线已映射盘、RPM 是否失速。 |
| SMART 暂不可用 | smartctl 路径、管理员权限、USB bridge 类型、RAID 透传、20 秒超时与设备响应。 |
| BAY 空 / 磁盘未分配 | 在设置页按序列号绑定，核对机箱实际槽位，不按 PhysicalDrive 序号猜测。 |
| SMART PASSED 仍然告警 | 检查 Pending、Uncorrectable、重映射增长、温度、自检和 CRC，PASSED 不是唯一健康标准。 |
| 自检显示英文 | smartctl 原始自检状态被完整保留，属性名和厂商协议字段不翻译。 |
| 远程桌面服务不可用 | VNC :5900、websockify :6080、密码、IIS 转发，核对 `GET /api/remote/status`。 |
| 改 JSON 后设置不变 | 已保存的 StationSettings 优先，从设置页修改；启动级路径/Host 仍从文件读取。 |
| 设置页保存成功后变 BIOS | 预期行为：换绑前恢复硬件默认，去风扇页重新选择自动模式。 |

## 必要验证与依赖来源

`dotnet test` 覆盖关键曲线插值、紧急保护、防抖/步长、SMART 规则/趋势、NVMe 寿命、稳定身份、盘位迁移与唯一绑定、温度选择，以及账户迁移/凭据变更使旧会话失效和头像输入校验。另有基本手工启动、登录、CSRF、非法 PWM、静态页、SignalR 与 noVNC 不可用场景检查；没有引入额外测试平台。

浏览器依赖版本锁在 `package-lock.json`。需要升级/重新生成资源时安装 Node.js 并运行 `deploy/update-assets.ps1`，然后重新发布；运行服务本身不需要 Node.js。noVNC 的原始模块和第三方许可证随资源一起分发。

参考实现依据：[LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)、[smartctl 官方源码与手册](https://github.com/smartmontools/smartmontools)、[noVNC RFB API](https://novnc.com/noVNC/docs/API.html)、[微软 IIS ARR 反向代理文档](https://learn.microsoft.com/en-us/iis/extensions/url-rewrite-module/reverse-proxy-with-url-rewrite-v2-and-application-request-routing)。

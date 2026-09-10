# IIS 部署指南：默认 HTTP，无需证书

适用于 Windows Server 2022 Datacenter。默认安装后访问 **http://服务器IP:8080/**，不用配置域名、DNS 或证书。登录和用户账户功能照常使用。

以下除注明“管理电脑”的部分外，都在磁盘站服务器的 **管理员 Windows PowerShell 5.1** 中执行。示例服务器地址 `192.168.1.50` 请换成实际内网 IP。

## 1. 解压发布包

把最新版 `StorageStation-win-x64.zip` 复制到服务器，解压到 `C:\StorageStation`。确认下面文件直接存在，不要多套一层 publish 目录：

```text
C:\StorageStation\StorageStation.Web.exe
C:\StorageStation\appsettings.json
C:\StorageStation\wwwroot\index.html
C:\StorageStation\deploy\install-service.ps1
C:\StorageStation\deploy\install-iis.ps1
```

服务器不需要安装 Node.js、Visual Studio 或 .NET SDK；前端文件和程序已经编译打包。

## 2. 启用 IIS

执行：

```powershell
Install-WindowsFeature Web-Server,Web-Static-Content,Web-Default-Doc,Web-WebSockets,Web-Mgmt-Console
```

如果返回结果要求重启，重启后继续。

## 3. 安装四项依赖

使用官方 Windows x64 安装程序，依次安装：

| 组件 | 用途及下载 |
|---|---|
| .NET 8 Hosting Bundle | [官方下载](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)，选择 ASP.NET Core Runtime 下的 Windows Hosting Bundle，使用最新 8.0.x。 |
| IIS URL Rewrite 2 | [官方下载](https://www.iis.net/downloads/microsoft/url-rewrite)，处理页面及 API 路由。 |
| IIS ARR 3 | [官方下载](https://www.iis.net/downloads/microsoft/application-request-routing)，将 API / WebSocket 请求转发到后台服务。 |
| smartmontools | [官方下载](https://www.smartmontools.org/wiki/Download)，读取硬盘 SMART。 |

如果 Hosting Bundle 安装在 IIS 之前，需要修复或重装 Hosting Bundle。完成后重新打开管理员 PowerShell：

```powershell
dotnet --list-runtimes
```

应看到 `Microsoft.NETCore.App 8.0.x` 和 `Microsoft.AspNetCore.App 8.0.x`。

## 4. 配置 smartctl 路径

在 `C:\StorageStation` 新建 `appsettings.Local.json`，输入：

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

确保文件名不是 `.json.txt`。smartctl 路径按实际安装位置修改。

验证能否发现硬盘：

```powershell
& 'C:\Program Files\smartmontools\bin\smartctl.exe' --scan-open -j
```

应返回带有设备列表的 JSON。硬件 RAID / USB 转接器是否允许读取物理磁盘 SMART，取决于控制器的透传支持。

## 5. 初始化管理员并安装后端服务

```powershell
Set-Location C:\StorageStation
$env:ASPNETCORE_ENVIRONMENT = 'Production'
.\StorageStation.Web.exe --init-admin
```

输入你自己的 12–256 字符密码，输入时不会显示字符。首次登录用户名为 `Admin`；生产环境没有默认密码。

接着运行：

```powershell
.\deploy\install-service.ps1 -InstallDirectory C:\StorageStation
Get-Service StorageStationService
```

状态应为 `Running`。服务自动随系统启动，不需要一直开着终端或双击 exe。

若下载的脚本被阻止，确认它们来自本项目后执行：

```powershell
Get-ChildItem -LiteralPath C:\StorageStation\deploy -Filter *.ps1 | Unblock-File
```

若错误明确为“禁止运行脚本”，且服务器未受组织策略限制，可仅在当前终端运行 `Set-ExecutionPolicy -Scope Process RemoteSigned` 后重试。

## 6. 一条命令创建 IIS 站点

```powershell
.\deploy\install-iis.ps1 -InstallDirectory C:\StorageStation
```

**不需要填写 Hostname 或 CertificateThumbprint。** 脚本会完成：

- 创建 `StorageStation` 网站和同名应用池。
- 网站绑定 `http`、端口 `8080`、空主机名，支持直接用 IP 访问。
- 网站目录为 `C:\StorageStation\wwwroot`，应用池为 No Managed Code。
- 启用 ARR 代理、静态路由、SignalR 与远程桌面 WebSocket 转发。
- 设置 `Security:RequireHttps=false`，取消站点“要求 SSL”。

默认 8080 可避开 IIS 自带 Default Web Site 常用的 80 端口。如果确认 80 端口空闲，可以安装时改用：

```powershell
.\deploy\install-iis.ps1 -InstallDirectory C:\StorageStation -Port 80
```

这两个安装命令选一个执行即可。脚本不会覆盖同名网站，也不会停止其他网站；遇到端口占用时选择其他空闲端口。5180、5900、6080 是内部服务端口，不用作 IIS 端口。

可以打开 `inetmgr` 查看：网站 → StorageStation → 绑定，应该是 `http / 8080 / 空主机名`；“SSL 设置”中“要求 SSL”应未勾选。

## 7. 放行 IIS 端口并访问

默认安装在服务器执行：

```powershell
New-NetFirewallRule -DisplayName 'Storage Station HTTP' `
  -Direction Inbound -Action Allow -Protocol TCP `
  -LocalPort 8080 -RemoteAddress LocalSubnet
```

如果安装时用了其他端口，把 8080 换成实际端口。跨网段访问时，将 `LocalSubnet` 换成实际管理电脑 IP 或管理网段。

在另一台管理电脑浏览器访问：

```text
http://192.168.1.50:8080/
```

将 `192.168.1.50` 换成服务器 IP；80 端口可省略 `:80`。不要使用 `https://`，也不要把另一台电脑的 `localhost` 当成服务器地址。HTTP 方式用于受信任的内网。

使用 `Admin` 和第 5 步设置的密码登录。默认浏览器资源、Cookie 登录、CSRF、SignalR 均可通过 HTTP 工作。后台 5180 仍固定只监听 `127.0.0.1`，外部访问走 IIS。

## 8. 首次配置硬件和用户账户

1. 确认网页没有“开发模式 / 模拟硬件”提示。
2. 系统页检查真实 CPU、内存及传感器。
3. 设置页按硬盘序列号绑定 BAY 01–08，核对实物盘位。
4. 绑定 CPU 温度与机箱风扇 RPM。软件风扇控制默认关闭，确认主板可写控制器与目标风扇对应后再启用。
5. “用户账户”中可以重命名登录用户、重置密码和上传头像；顶部铅笔用于修改主机显示名称。

主板只提供 RPM、不提供可写 Control 时，保持 BIOS 风扇控制即可。真实硬件、IIS 和 VNC 仍需要在目标服务器检查；开发环境使用模拟数据。

## 9. 可选：网页远程桌面

如果暂时只要监控，可稍后配置这一步。

安装 [TightVNC Server](https://www.tightvnc.com/download.php) 或已有可信 VNC 服务，注册为 Windows 服务，设置独立 VNC 密码，允许 localhost 并限制为 loopback 连接，使用 5900 端口。普通 VNC 密码认证可用于 HTTP 入口；依赖浏览器安全上下文的某些扩展功能可能需要 HTTPS。

安装 [Python](https://www.python.org/downloads/windows/) 和 [NSSM](https://nssm.cc/download)，确认 `python --version` 能运行，再执行（NSSM 路径按实际位置修改）：

```powershell
Set-Location C:\StorageStation
.\deploy\install-websockify.ps1 `
  -InstallDirectory C:\StorageStation `
  -NssmPath C:\Tools\nssm\win64\nssm.exe

Get-Service StorageStationWebsockify
```

该桥接服务只监听 `127.0.0.1:6080`。在网页“远程控制”输入 VNC 密码后连接。对外开放的仍只有 IIS 端口，5900 / 6080 不作为远程直连入口。

## 10. 已经部署了旧版 HTTPS 时如何改成 HTTP

如果还没在服务器安装过旧版，跳过此节。

1. 停止主服务，备份整个 `C:\StorageStation\Data` 和 `appsettings.Local.json`，再用新版发布文件覆盖程序。保留 Data / logs / 本地配置。
2. 在 `appsettings.Local.json` 中设置 `"Security": { "RequireHttps": false }` 和 `"AllowedHosts": "*"`，同时保留原来的 SmartCtl 等配置。若服务环境变量曾设置 `Security__RequireHttps=true`，也需取消该覆盖。
3. 打开 IIS 管理器，选中原 StorageStation 网站，在“绑定”中添加 `http / 8080 / 空主机名`，在“SSL 设置”取消“要求 SSL”。不再需要的该站点 HTTPS 绑定可移除。
4. 使用新版本的路由配置覆盖该站点配置：

```powershell
Copy-Item -LiteralPath C:\StorageStation\deploy\iis-web.config `
  -Destination C:\StorageStation\wwwroot\web.config -Force
Start-Service StorageStationService
```

5. 按第 7 步开放 8080，然后用 `http://服务器IP:8080/` 登录。

新版同时去掉了旧版强制 HTTPS 的 IIS 规则和 HSTS 响应头。如果旧域名被浏览器记住了 HSTS，先使用服务器 IP 访问，避免被自动改成 HTTPS。通过旧 HTTPS 地址保存的 Secure Cookie 在 HTTP 地址下需要重新登录；若同一 IP / 主机名出现登录后又退回登录页，先清除该站点的旧 Cookie 再登录。已有站点不用重新运行首次安装脚本。

## 11. 常用排错和维护

```powershell
Get-Service StorageStationService
Get-NetTCPConnection -State Listen |
  Where-Object LocalPort -in 8080,5180,5900,6080 |
  Select-Object LocalAddress,LocalPort,OwningProcess
```

| 现象 | 检查 |
|---|---|
| 打不开网页 | URL 是否为 http://服务器IP:8080，IIS 站点是否启动、防火墙是否开放正确端口。 |
| IIS 500.19 | URL Rewrite / ARR 是否安装，站点目录是否为 wwwroot，相关 IIS 配置节是否锁定。 |
| IIS 502 | 主服务是否 Running，127.0.0.1:5180 是否监听。 |
| 仍提示 HTTPS 或 403.4 | 检查本地配置 / 环境变量的 RequireHttps、IIS“要求 SSL”、是否覆盖了新版 web.config。 |
| 提示无效 Host | AllowedHosts 是否为 * 或包含实际访问的 IP / 主机名。 |
| 反复登录或实时连接失败 | 检查使用新版 IIS 代理规则：HTTP 请求的 X-Forwarded-Proto 必须为 http。 |
| 主服务无法启动 | 检查 .NET 8 运行时、JSON 语法、管理员初始化；查看 C:\StorageStation\logs 最新日志。 |
| 没有磁盘 | 检查 smartctl 路径和管理员终端扫描结果，确认控制器支持透传。 |

忘记密码时，在磁盘站管理员 PowerShell 中执行：

```powershell
Stop-Service StorageStationService
Set-Location C:\StorageStation
$env:ASPNETCORE_ENVIRONMENT = 'Production'
.\StorageStation.Web.exe --init-admin
Start-Service StorageStationService
```

当前用户名会显示在提示中。密码重置后所有旧会话失效；数据库备份也会包含用户名与头像。

## 可选：以后再开启 HTTPS

默认关闭。已有证书且明确需要 HTTPS 时，首次安装可额外传入 `-CertificateThumbprint`；未指定端口时使用 443。脚本仅在这种情况下设置 `Security:RequireHttps=true`、“要求 SSL”和 HSTS。现有网站可手动添加 HTTPS 绑定并开启对应配置，不必重复创建网站。

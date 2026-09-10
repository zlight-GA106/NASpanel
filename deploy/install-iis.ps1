#Requires -RunAsAdministrator
param(
    [Parameter(Mandatory=$true)][string]$InstallDirectory,
    [ValidatePattern('^[A-Za-z0-9.-]*$')][string]$Hostname = '',
    [ValidateRange(1,65535)][int]$Port = 8080,
    [ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$CertificateThumbprint,
    [string]$SiteName = 'StorageStation'
)
$ErrorActionPreference = 'Stop'
$useHttps = -not [string]::IsNullOrWhiteSpace($CertificateThumbprint)
if ($useHttps -and -not $PSBoundParameters.ContainsKey('Port')) { $Port = 443 }
if ($Port -in @(5180,5900,6080)) { throw '该端口保留给内部服务，请为 IIS 选择其他端口，例如 8080。' }
Import-Module ServerManager
Install-WindowsFeature Web-Server,Web-Static-Content,Web-Default-Doc,Web-WebSockets,Web-Mgmt-Console | Out-Null
Import-Module WebAdministration
$directory = (Resolve-Path -LiteralPath $InstallDirectory).Path
$webRoot = Join-Path $directory 'wwwroot'
if (-not (Test-Path -LiteralPath "$webRoot/index.html")) { throw '网站文件不存在，请先 publish。' }
if ($useHttps) {
    if (-not (Test-Path -LiteralPath "Cert:/LocalMachine/My/$CertificateThumbprint")) { throw '未找到指定的 HTTPS 证书。默认 HTTP 安装无需传入 CertificateThumbprint。' }
    if ((Get-Item "Cert:/LocalMachine/My/$CertificateThumbprint").HasPrivateKey -ne $true) { throw '证书缺少私钥。' }
}
$appcmd = "$env:windir/System32/inetsrv/appcmd.exe"
$modules = (& $appcmd list modules) -join "`n"
if ($modules -notmatch 'RewriteModule' -or $modules -notmatch 'ApplicationRequestRouting') { throw '请先安装 IIS URL Rewrite 2 与 ARR 3，见 README。' }
& $appcmd unlock config -section:system.webServer/webSocket | Out-Null
if ($LASTEXITCODE -ne 0) { throw '解锁 IIS WebSocket 配置节失败' }
if (Test-Path "IIS:/Sites/$SiteName") { throw "IIS 网站 $SiteName 已存在，请手动检查后更新。" }
$protocol = if ($useHttps) { 'https' } else { 'http' }
$conflictingBinding = Get-WebBinding -Protocol $protocol | Where-Object {
    $_.bindingInformation -match ":${Port}:([^:]*)$" -and ([string]::IsNullOrEmpty($Hostname) -or [string]::IsNullOrEmpty($Matches[1]) -or $Matches[1] -eq $Hostname)
}
if ($conflictingBinding) { throw "IIS 已有网站使用端口 $Port 的相同或通配主机绑定；请使用 -Port 指定空闲端口。" }
& $appcmd set config -section:system.webServer/proxy /enabled:true /preserveHostHeader:true /reverseRewriteHostInResponseHeaders:false /commit:apphost | Out-Null
if ($LASTEXITCODE -ne 0) { throw '启用 ARR 代理失败' }
$allowed = Get-WebConfiguration 'system.webServer/rewrite/allowedServerVariables/add' -PSPath 'MACHINE/WEBROOT/APPHOST'
if (-not ($allowed | Where-Object { $_.name -eq 'HTTP_X_FORWARDED_PROTO' })) {
    Add-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter 'system.webServer/rewrite/allowedServerVariables' -Name '.' -Value @{name='HTTP_X_FORWARDED_PROTO'}
}
Copy-Item -LiteralPath "$PSScriptRoot/iis-web.config" -Destination "$webRoot/web.config" -Force
if ($useHttps) {
    [xml]$webConfig = Get-Content -LiteralPath "$webRoot/web.config" -Raw
    $header = $webConfig.CreateElement('add')
    $header.SetAttribute('name', 'Strict-Transport-Security')
    $header.SetAttribute('value', 'max-age=31536000')
    $webConfig.configuration.'system.webServer'.httpProtocol.customHeaders.AppendChild($header) | Out-Null
    $webConfig.Save("$webRoot/web.config")
}
if (-not (Test-Path "IIS:/AppPools/$SiteName")) { New-WebAppPool -Name $SiteName | Out-Null }
Set-ItemProperty "IIS:/AppPools/$SiteName" -Name managedRuntimeVersion -Value ''
if ($useHttps) {
    $sslFlags = if ($Hostname) { 1 } else { 0 }
    New-Website -Name $SiteName -PhysicalPath $webRoot -ApplicationPool $SiteName -Port $Port -HostHeader $Hostname -Ssl -SslFlags $sslFlags | Out-Null
    (Get-WebBinding -Name $SiteName -Protocol https).AddSslCertificate($CertificateThumbprint, 'My')
} else {
    New-Website -Name $SiteName -PhysicalPath $webRoot -ApplicationPool $SiteName -Port $Port -HostHeader $Hostname | Out-Null
}
Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $SiteName -Filter 'system.webServer/security/access' -Name sslFlags -Value $(if ($useHttps) { 'Ssl' } else { 'None' })
& icacls.exe $webRoot /grant ('IIS AppPool\' + $SiteName + ':(OI)(CI)RX') /T | Out-Null
if ($LASTEXITCODE -ne 0) { throw '设置网站读取权限失败' }
$localFile = Join-Path $directory 'appsettings.Local.json'
$local = if (Test-Path -LiteralPath $localFile) { Get-Content -LiteralPath $localFile -Raw -Encoding UTF8 | ConvertFrom-Json } else { [pscustomobject]@{} }
$allowedHosts = if ($Hostname) { "localhost;127.0.0.1;$Hostname" } else { '*' }
$local | Add-Member -NotePropertyName AllowedHosts -NotePropertyValue $allowedHosts -Force
if (-not $local.Security) { $local | Add-Member -NotePropertyName Security -NotePropertyValue ([pscustomobject]@{}) -Force }
$local.Security | Add-Member -NotePropertyName RequireHttps -NotePropertyValue $useHttps -Force
$local | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $localFile -Encoding UTF8
if (Get-Service StorageStationService -ErrorAction SilentlyContinue) { Restart-Service StorageStationService }
$accessHost = if ($Hostname) { $Hostname } else { '服务器IP' }
Write-Host "IIS 已配置：${protocol}://${accessHost}:${Port}/ 。请给管理网段开放端口 $Port。"

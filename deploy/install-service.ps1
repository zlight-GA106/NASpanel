#Requires -RunAsAdministrator
param([Parameter(Mandatory=$true)][string]$InstallDirectory)
$ErrorActionPreference = 'Stop'
$directory = (Resolve-Path -LiteralPath $InstallDirectory).Path
$exe = Join-Path $directory 'StorageStation.Web.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "未找到 $exe，请先发布工程。" }
if (Get-Service -Name 'StorageStationService' -ErrorAction SilentlyContinue) { throw '服务已存在；请按 README 的升级流程停止并更新。' }
if (-not (Test-Path -LiteralPath (Join-Path $directory 'Data/monitor.db'))) { throw '请先在生产环境运行 StorageStation.Web.exe --init-admin 初始化管理员。' }
New-Item -ItemType Directory -Force -Path "$directory/Data", "$directory/logs" | Out-Null
# Data/keys and database must be writable only by Administrators and the service identity.
foreach ($path in @("$directory/Data", "$directory/logs")) {
    & icacls.exe $path /inheritance:r | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "移除 ACL 继承失败：$path" }
    & icacls.exe $path /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "设置 ACL 失败：$path" }
}
New-Service -Name 'StorageStationService' -BinaryPathName ('"' + $exe + '"') -DisplayName 'Storage Station' -Description '本地服务器硬件、SMART 与风扇控制台' -StartupType Automatic | Out-Null
Set-ItemProperty -LiteralPath 'HKLM:/SYSTEM/CurrentControlSet/Services/StorageStationService' -Name Environment -Type MultiString -Value @('ASPNETCORE_ENVIRONMENT=Production')
& sc.exe config StorageStationService start= delayed-auto | Out-Null
if ($LASTEXITCODE -ne 0) { throw '设置延迟启动失败' }
& sc.exe failure StorageStationService reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null
if ($LASTEXITCODE -ne 0) { throw '设置服务恢复策略失败' }
Start-Service -Name 'StorageStationService'
Write-Host '服务已安装，以 LocalSystem 运行。Kestrel 固定监听 127.0.0.1:5180。'

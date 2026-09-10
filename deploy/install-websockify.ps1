#Requires -RunAsAdministrator
param([Parameter(Mandatory=$true)][string]$InstallDirectory, [string]$Python = 'python', [string]$NssmPath)
$ErrorActionPreference = 'Stop'
$directory = (Resolve-Path -LiteralPath $InstallDirectory).Path
$venv = Join-Path $directory 'tools/websockify-venv'
& $Python -m venv $venv
if ($LASTEXITCODE -ne 0) { throw '创建 websockify Python 环境失败' }
$venvPython = Join-Path $venv 'Scripts/python.exe'
& $venvPython -m pip install 'websockify==0.13.0'
if ($LASTEXITCODE -ne 0) { throw '安装 websockify 失败' }
if (-not $NssmPath) {
    Write-Host 'websockify 已安装。前台启动命令：'
    Write-Host "& '$venvPython' -m websockify 127.0.0.1:6080 127.0.0.1:5900"
    Write-Host '长期运行时再次运行本脚本，传入官方 NSSM 的 -NssmPath 以注册 Windows 服务。'
    return
}
$nssm = (Resolve-Path -LiteralPath $NssmPath).Path
if (Get-Service StorageStationWebsockify -ErrorAction SilentlyContinue) { throw 'websockify 服务已存在，请先检查其配置。' }
& $nssm install StorageStationWebsockify $venvPython '-m websockify 127.0.0.1:6080 127.0.0.1:5900'
if ($LASTEXITCODE -ne 0) { throw 'NSSM 注册失败' }
& $nssm set StorageStationWebsockify AppDirectory $directory
& $nssm set StorageStationWebsockify Start SERVICE_DELAYED_AUTO_START
& $nssm set StorageStationWebsockify AppExit Default Restart
Start-Service StorageStationWebsockify
Write-Host 'websockify 服务已启动，仅监听 127.0.0.1:6080。'

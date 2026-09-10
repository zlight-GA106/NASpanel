#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$service = Get-Service -Name StorageStationService -ErrorAction SilentlyContinue
if ($service) {
    Stop-Service -Name StorageStationService
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(45))
    & sc.exe delete StorageStationService
    if ($LASTEXITCODE -ne 0) { throw '删除服务注册失败' }
}
Write-Host '服务已卸载。数据库、日志、网站和程序文件均保留。'

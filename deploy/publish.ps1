param([string]$Output = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/publish'))
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path -LiteralPath "$root/StorageStation.Web/wwwroot/vendor/echarts.min.js")) { throw '缺少浏览器依赖，请先运行 deploy/update-assets.ps1' }
dotnet publish "$root/StorageStation.Web/StorageStation.Web.csproj" -c Release -r win-x64 --self-contained false -o $Output
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
Copy-Item -LiteralPath "$PSScriptRoot/iis-web.config" -Destination (Join-Path $Output 'wwwroot/web.config') -Force
New-Item -ItemType Directory -Force -Path (Join-Path $Output 'deploy') | Out-Null
Get-ChildItem -LiteralPath $PSScriptRoot -File | Copy-Item -Destination (Join-Path $Output 'deploy') -Force
New-Item -ItemType Directory -Force -Path (Join-Path $Output 'tools/smartmontools') | Out-Null
Copy-Item -LiteralPath "$root/tools/smartmontools/README.md" -Destination (Join-Path $Output 'tools/smartmontools/README.md')
Copy-Item -LiteralPath "$root/README.md" -Destination (Join-Path $Output 'README.md')
Copy-Item -LiteralPath "$root/Wake-StorageStation.ps1" -Destination (Join-Path $Output 'Wake-StorageStation.ps1')
if (Test-Path -LiteralPath "$root/LICENSE") { Copy-Item -LiteralPath "$root/LICENSE" -Destination (Join-Path $Output 'LICENSE') }
Write-Host "发布完成：$Output"

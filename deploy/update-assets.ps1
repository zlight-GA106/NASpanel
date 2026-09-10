$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    npm ci --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' }
    $web = Join-Path $root 'StorageStation.Web/wwwroot'
    New-Item -ItemType Directory -Force -Path "$web/vendor", "$web/novnc", "$web/vendor/licenses" | Out-Null
    Copy-Item -LiteralPath 'node_modules/@microsoft/signalr/dist/browser/signalr.min.js' -Destination "$web/vendor/signalr.min.js"
    Copy-Item -LiteralPath 'node_modules/echarts/dist/echarts.min.js' -Destination "$web/vendor/echarts.min.js"
    Copy-Item -LiteralPath 'node_modules/@novnc/novnc/core' -Destination "$web/novnc" -Recurse -Force
    Copy-Item -LiteralPath 'node_modules/@novnc/novnc/vendor' -Destination "$web/novnc" -Recurse -Force
    Copy-Item -LiteralPath 'node_modules/@novnc/novnc/LICENSE.txt' -Destination "$web/vendor/licenses/noVNC.txt"
    Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/dotnet/aspnetcore/v8.0.29/LICENSE.txt' -OutFile "$web/vendor/licenses/SignalR.txt"
    Copy-Item -LiteralPath 'node_modules/echarts/LICENSE' -Destination "$web/vendor/licenses/ECharts.txt"
    Copy-Item -LiteralPath 'node_modules/echarts/NOTICE' -Destination "$web/vendor/licenses/ECharts-NOTICE.txt"
    Copy-Item -LiteralPath 'node_modules/echarts/licenses' -Destination "$web/vendor/licenses" -Recurse -Force
    Copy-Item -LiteralPath 'node_modules/@novnc/novnc/AUTHORS' -Destination "$web/vendor/licenses/noVNC-AUTHORS.txt"
} finally { Pop-Location }

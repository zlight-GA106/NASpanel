[CmdletBinding()]
param(
    [string]$ServerIp = '192.168.100.145',
    [string]$SshUser = 'Administrator',
    [ValidateRange(0, 32)]
    [int]$TargetPrefixLength = 24,
    [ValidateRange(1, 65535)]
    [int]$Port = 9
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Normalize-MacAddress {
    param([AllowNull()][string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    $clean = ($Value -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    if ($clean.Length -ne 12 -or $clean -match '^0{12}$' -or $clean -match '^F{12}$') {
        return $null
    }

    return (($clean -split '(.{2})' | Where-Object { $_ }) -join '-')
}

function Find-LiveMacAddress {
    param([string]$IpAddress)

    try {
        $ping = [System.Net.NetworkInformation.Ping]::new()
        try { $null = $ping.Send($IpAddress, 1000) } finally { $ping.Dispose() }
    } catch {
        # Ping may be blocked. The neighbor table can still contain a usable entry.
    }

    $neighbor = Get-NetNeighbor -AddressFamily IPv4 -IPAddress $IpAddress -ErrorAction SilentlyContinue |
        Where-Object { $_.State -ne 'Unreachable' } |
        Select-Object -First 1
    if ($null -ne $neighbor) {
        $mac = Normalize-MacAddress $neighbor.LinkLayerAddress
        if ($null -ne $mac) { return $mac }
    }

    $arpOutput = & "$env:SystemRoot\System32\arp.exe" -a $IpAddress 2>$null
    foreach ($line in $arpOutput) {
        if ($line -match [regex]::Escape($IpAddress) -and
            $line -match '(?i)([0-9a-f]{2}[-:]){5}[0-9a-f]{2}') {
            $mac = Normalize-MacAddress $Matches[0]
            if ($null -ne $mac) { return $mac }
        }
    }

    return $null
}

function Find-MacAddressViaSsh {
    param(
        [string]$IpAddress,
        [string]$UserName
    )

    $ssh = Get-Command ssh.exe -ErrorAction SilentlyContinue
    if ($null -eq $ssh) { return $null }

    Write-Host "The target is on another subnet. Trying SSH MAC discovery for $UserName@$IpAddress."
    $remoteCommand = '$nicIndex=(Get-NetIPAddress -AddressFamily IPv4 -IPAddress "{0}").InterfaceIndex; (Get-NetAdapter -InterfaceIndex $nicIndex).MacAddress' -f $IpAddress
    $output = & $ssh.Source -o ConnectTimeout=4 -o StrictHostKeyChecking=accept-new "$UserName@$IpAddress" $remoteCommand
    if ($LASTEXITCODE -ne 0) { return $null }

    foreach ($line in $output) {
        $mac = Normalize-MacAddress ([string]$line)
        if ($null -ne $mac) { return $mac }
    }
    return $null
}

function Get-BroadcastAddress {
    param(
        [System.Net.IPAddress]$TargetAddress,
        [int]$FallbackPrefixLength
    )

    $target = $TargetAddress.GetAddressBytes()
    $localAddresses = Get-NetIPAddress -AddressFamily IPv4 -AddressState Preferred -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' }

    foreach ($address in $localAddresses) {
        $local = [System.Net.IPAddress]::Parse($address.IPAddress).GetAddressBytes()
        $prefixLength = [int]$address.PrefixLength
        $sameSubnet = $true
        $broadcast = [byte[]]::new(4)

        for ($i = 0; $i -lt 4; $i++) {
            $remainingBits = $prefixLength - ($i * 8)
            $mask = if ($remainingBits -ge 8) {
                255
            } elseif ($remainingBits -le 0) {
                0
            } else {
                256 - [int][math]::Pow(2, 8 - $remainingBits)
            }

            if (($local[$i] -band $mask) -ne ($target[$i] -band $mask)) {
                $sameSubnet = $false
                break
            }
            $broadcast[$i] = [byte](($target[$i] -band $mask) -bor (255 - $mask))
        }

        if ($sameSubnet) { return [System.Net.IPAddress]::new($broadcast) }
    }

    $broadcast = [byte[]]::new(4)
    for ($i = 0; $i -lt 4; $i++) {
        $remainingBits = $FallbackPrefixLength - ($i * 8)
        $mask = if ($remainingBits -ge 8) {
            255
        } elseif ($remainingBits -le 0) {
            0
        } else {
            256 - [int][math]::Pow(2, 8 - $remainingBits)
        }
        $broadcast[$i] = [byte](($target[$i] -band $mask) -bor (255 - $mask))
    }
    return [System.Net.IPAddress]::new($broadcast)
}

try {
    $targetAddress = [System.Net.IPAddress]::Parse($ServerIp)
} catch {
    throw "Invalid IPv4 address: $ServerIp"
}
if ($targetAddress.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
    throw "Wake-on-LAN requires an IPv4 address: $ServerIp"
}

$cacheDirectory = Join-Path $env:LOCALAPPDATA 'StorageStation'
$cachePath = Join-Path $cacheDirectory 'wol-target.json'
$macAddress = Find-LiveMacAddress $ServerIp
$macSource = 'current neighbor table'

if ($null -eq $macAddress -and (Test-Path -LiteralPath $cachePath)) {
    $cached = Get-Content -LiteralPath $cachePath -Raw | ConvertFrom-Json
    if ([string]$cached.ip -eq $ServerIp) {
        $macAddress = Normalize-MacAddress ([string]$cached.mac)
        $macSource = 'local cache'
    }
}

if ($null -eq $macAddress) {
    $macAddress = Find-MacAddressViaSsh $ServerIp $SshUser
    $macSource = 'SSH query from target'
}

if ($null -eq $macAddress) {
    throw "Cannot discover the MAC address for $ServerIp. Run this script once while Storage Station is online so it can query the neighbor table or the target through SSH."
}

if ($macSource -ne 'local cache') {
    New-Item -ItemType Directory -Path $cacheDirectory -Force | Out-Null
    [ordered]@{
        ip = $ServerIp
        mac = $macAddress
        updatedAt = [DateTimeOffset]::Now.ToString('o')
    } | ConvertTo-Json | Set-Content -LiteralPath $cachePath -Encoding UTF8
}

$macBytes = [byte[]](($macAddress -split '-') | ForEach-Object { [Convert]::ToByte($_, 16) })
$packet = [byte[]]::new(102)
for ($i = 0; $i -lt 6; $i++) { $packet[$i] = 0xFF }
for ($repeat = 0; $repeat -lt 16; $repeat++) {
    [Array]::Copy($macBytes, 0, $packet, 6 + ($repeat * 6), 6)
}

$broadcastAddress = Get-BroadcastAddress $targetAddress $TargetPrefixLength
$udp = [System.Net.Sockets.UdpClient]::new()
try {
    $udp.EnableBroadcast = $true
    $sent = $udp.Send($packet, $packet.Length, $broadcastAddress.ToString(), $Port)
} finally {
    $udp.Dispose()
}

if ($sent -ne $packet.Length) { throw "Incomplete magic packet: $sent/$($packet.Length) bytes" }
Write-Host "Wake-on-LAN magic packet sent"
Write-Host "Target IP: $ServerIp"
Write-Host "MAC: $macAddress (source: $macSource)"
Write-Host "Broadcast: $broadcastAddress`:$Port"

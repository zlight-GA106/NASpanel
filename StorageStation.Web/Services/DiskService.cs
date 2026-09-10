using System.Collections.Concurrent;
using StorageStation.Web.Data;
using StorageStation.Web.Models;
using StorageStation.Web.Storage;
using Microsoft.AspNetCore.SignalR;
using StorageStation.Web.Hubs;

namespace StorageStation.Web.Services;

public sealed class DiskService(IDiskProvider provider, StateCache cache, StationDatabase db, SettingsService settings, EventService events, IHubContext<RealtimeHub> hub, IHostEnvironment env)
{
    private readonly ConcurrentDictionary<string, DiskDevice> devices = new();
    private readonly ConcurrentDictionary<string, int> missing = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> lastFull = new();
    private readonly Dictionary<string, int> missingSerial = [];
    private readonly SemaphoreSlim operations = new(1, 1);
    private bool initialized;
    public object View() => new { bays = cache.Bays, unassigned = cache.Disks.Values.Where(d => !cache.Bays.Any(b => b.Serial == d.Serial)).OrderBy(d => d.Model).ToArray() };
    public async Task Refresh(CancellationToken ct)
    {
        await operations.WaitAsync(ct);
        try
        {
            if (!initialized)
            {
                var latest = await db.Query("SELECT payload FROM disk_metrics WHERE id IN (SELECT MAX(id) FROM disk_metrics GROUP BY disk_id)", [], ct);
                foreach (var row in latest)
                {
                    var d = System.Text.Json.JsonSerializer.Deserialize<DiskStatus>((string)row["payload"]!, StationDatabase.Json)!;
                    cache.Disks[d.Id] = d with { Online = false, Health = Health.Unknown("等待启动扫描") };
                }
                initialized = true;
            }
            // Failed whole-host scans must not count as missing physical disks.
            var scanned = await provider.ScanAsync(ct);
            var seen = new ConcurrentDictionary<string, bool>();
            var knownPaths = devices.ToArray();
            await Parallel.ForEachAsync(scanned, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct }, async (device, token) =>
            {
                var priorId = knownPaths.FirstOrDefault(p => p.Value == device).Key;
                try
                {
                    var full = priorId is null || !lastFull.TryGetValue(priorId, out var last) || DateTimeOffset.UtcNow - last >= TimeSpan.FromSeconds(settings.Current.SmartIntervalSeconds) || (cache.Disks.TryGetValue(priorId, out var testing) && testing.SelfTest.Running);
                    var disk = await provider.ReadAsync(device, full, token);
                    if (!full && disk.Id != priorId) { disk = await provider.ReadAsync(device, true, token); full = true; }
                    seen[disk.Id] = true; missing[disk.Id] = 0; devices[disk.Id] = device;
                    cache.Disks.TryGetValue(disk.Id, out var old);
                    if (!full && old is not null) disk = old with { Online = true, Temperature = disk.Temperature, TemperatureTimestamp = disk.TemperatureTimestamp, ReadError = disk.ReadError };
                    var history = await db.RecentDisks(disk.Id, full ? 2 : 3, token);
                    // Temperature-only updates must not count the latest full sample twice and erase a deterioration alert.
                    if (!full && history.Length > 0) history = history[..^1];
                    disk = disk with { Health = DiskHealthService.Evaluate(disk, history, settings.Current) };
                    cache.Disks[disk.Id] = disk;
                    if (old?.ReadError == true && !disk.ReadError)
                        await events.Transition("disk-read-" + device.Path, "info", disk.Serial, "READ_RECOVERED", "SMART 数据采集恢复", token);
                    if (full) { await db.SaveDisk(disk, token); lastFull[disk.Id] = DateTimeOffset.UtcNow; }
                    await db.TouchBay(disk, token);
                    if (old?.Online == false) await events.Transition("disk-online-" + disk.Id, "info", disk.Serial, "ONLINE", "磁盘恢复在线", token);
                    if (old?.Health.Level != disk.Health.Level || !Enumerable.SequenceEqual(old.Health.Messages, disk.Health.Messages))
                        await events.Transition("disk-health-" + disk.Id, disk.Health.Level == "critical" ? "critical" : disk.Health.Level is "warning" or "unknown" ? "warning" : "info", disk.Serial, "SMART_HEALTH", string.Join("；", disk.Health.Messages), token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Device still scanned but SMART failed: unknown, not offline.
                    if (priorId is not null && cache.Disks.TryGetValue(priorId, out var old))
                    {
                        seen[priorId] = true; missing[priorId] = 0;
                        cache.Disks[priorId] = old with { ReadError = true, Temperature = null, TemperatureTimestamp = null, Health = Health.Unknown("SMART 数据暂不可用") };
                    }
                    await events.Transition("disk-read-" + device.Path, "warning", "SMART", "READ_FAILED", $"{device.Path}：{ex.Message}", token);
                }
            });
            foreach (var pair in cache.Disks.ToArray().Where(p => !seen.ContainsKey(p.Key)))
            {
                var count = missing.AddOrUpdate(pair.Key, 1, (_, n) => Math.Min(3, n + 1));
                if (count >= 3)
                {
                    devices.TryRemove(pair.Key, out _);
                    cache.Disks[pair.Key] = pair.Value with { Online = false, Temperature = null, TemperatureTimestamp = null, Health = new("offline", ["连续三次扫描未发现磁盘"]) };
                    await events.Transition("disk-online-" + pair.Key, "critical", pair.Value.Serial, "OFFLINE", "磁盘连续三次扫描未出现，已标记离线", ct);
                }
            }
            if (env.IsDevelopment() && await db.GetSetting("mock-bays-initialized", ct) is null)
            {
                for (var i = 1; i <= 8; i++) await db.MapBay(i, $"DEMO-SERIAL-{i:00}", ct);
                await db.SetSetting("mock-bays-initialized", "true", ct);
            }
            foreach (var mapping in await db.Bays(ct))
            {
                if (mapping["serial"] is not string serial) continue;
                var present = cache.Disks.Values.Any(d => d.Serial == serial && seen.ContainsKey(d.Id));
                missingSerial[serial] = present ? 0 : Math.Min(3, missingSerial.GetValueOrDefault(serial) + 1);
            }
            await RefreshBays(ct);
            await hub.Clients.All.SendAsync("disks", View(), ct);
        }
        finally { operations.Release(); }
    }
    public async Task RefreshBays(CancellationToken ct)
    {
        var rows = await db.Bays(ct);
        cache.Bays = rows.Select(r =>
        {
            var serial = r["serial"] as string;
            var disk = cache.Disks.Values.FirstOrDefault(d => d.Serial == serial);
            return new BayState((int)(long)r["bay"]!, serial, disk, serial is null ? "empty" : disk?.Health.Level ?? (missingSerial.GetValueOrDefault(serial) >= 3 ? "offline" : "unknown"));
        }).ToArray();
    }
    public async Task MapBay(int bay, string? serial, CancellationToken ct)
    {
        if (bay is < 1 or > 8) throw new ArgumentException("盘位范围为 1–8");
        await operations.WaitAsync(ct);
        try
        {
            if (serial is not null && !cache.Disks.Values.Any(d => d.Serial == serial)) throw new ArgumentException("只能绑定服务器已发现的磁盘");
            if (serial is not null && (await db.Bays(ct)).Any(b => (long)b["bay"]! != bay && b["serial"] as string == serial)) throw new ArgumentException("该磁盘已分配到其他盘位，请先解除原绑定");
            await db.MapBay(bay, serial, ct); await RefreshBays(ct);
            await db.AddEvent("info", $"BAY {bay:00}", "MAPPING", serial is null ? "解除磁盘绑定" : $"绑定磁盘 {serial}", ct: ct);
            await hub.Clients.All.SendAsync("disks", View(), ct);
        }
        finally { operations.Release(); }
    }
    public async Task<string> SelfTest(string id, string kind, CancellationToken ct)
    {
        if (!devices.TryGetValue(id, out var device) || !cache.Disks.TryGetValue(id, out var disk) || !disk.Online) throw new ArgumentException("磁盘离线或未发现");
        var result = await provider.SelfTestAsync(device, id, kind, ct);
        cache.Disks.AddOrUpdate(id, disk, (_, d) => d with { SelfTest = new(result, true, null, false) });
        lastFull.TryRemove(id, out _);
        await db.AddEvent("info", disk.Serial, "SELF_TEST", result, ct: ct);
        return result;
    }
}

using Microsoft.AspNetCore.SignalR;
using StorageStation.Web.Data;
using StorageStation.Web.Hardware;
using StorageStation.Web.Hubs;
using StorageStation.Web.Models;
using StorageStation.Web.Services;

namespace StorageStation.Web.Workers;

public sealed class HardwareWorker(IHardwareProvider provider, StateCache cache, SettingsService settings, StationDatabase db, IHubContext<RealtimeHub> hub, ILogger<HardwareWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var saved = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                try
                {
                    var (snapshot, sensors) = await provider.ReadAsync(stoppingToken);
                    cache.Hardware = snapshot; cache.Sensors = sensors;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "硬件采样失败");
                    cache.Hardware = cache.Hardware with { CpuUsage = null, CpuTemperature = null, FanRpm = null, FanPercent = null, MemoryUsage = null, MemoryUsedGb = null, Error = "硬件采样失败，请检查权限和驱动" };
                }
                await hub.Clients.All.SendAsync("system", cache.SystemView(settings.Current.DisplayName), stoppingToken);
                if (DateTimeOffset.UtcNow - saved >= TimeSpan.FromSeconds(10))
                {
                    await db.SaveSystem(cache.Hardware with { Timestamp = DateTimeOffset.UtcNow }, cache.Fan, stoppingToken);
                    saved = DateTimeOffset.UtcNow;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "实时推送或历史记录失败"); }
            await Task.Delay(settings.Current.RealtimeIntervalMs, stoppingToken);
        }
    }
}
public sealed class SmartWorker(DiskService disks, StateCache cache, SettingsService settings, EventService events, ILogger<SmartWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await disks.Refresh(stoppingToken);
                if (cache.SmartError is not null) await events.Transition("smart-scan", "info", "SMART", "SCAN_OK", "SMART 扫描成功", stoppingToken);
                cache.SmartError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "SMART 扫描失败");
                cache.SmartError = "SMART 扫描暂不可用，请检查 smartctl 路径与权限";
                try { await events.Transition("smart-scan", "warning", "SMART", "SCAN_FAILED", "SMART 扫描失败：" + ex.Message, stoppingToken); }
                catch (Exception logError) when (logError is not OperationCanceledException) { logger.LogError(logError, "记录扫描异常失败"); }
            }
            await Task.Delay(TimeSpan.FromSeconds(settings.Current.DiskTemperatureIntervalSeconds), stoppingToken);
        }
    }
}
public sealed class FanWorker(FanControlService fan, StateCache cache, ILogger<FanWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await fan.Tick(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "风扇控制异常，尝试恢复 BIOS");
                    cache.Fan = cache.Fan with { Health = new("critical", ["风扇控制异常，正在恢复 BIOS"]), SafetyReason = "控制器异常" };
                    try { await fan.Recover(stoppingToken); }
                    catch (Exception restore) when (restore is not OperationCanceledException) { logger.LogCritical(restore, "恢复 BIOS 失败"); }
                }
                await Task.Delay(1000, stoppingToken);
            }
        }
        finally { await fan.SafeStop(); }
    }
}
public sealed class HealthMonitorWorker(StateCache cache, SettingsService settings, EventService events, ILogger<HealthMonitorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var issues = new List<(string Level, string Message)>();
                var h = cache.Hardware;
                if (cache.SmartError is { } smartError) issues.Add(("unknown", smartError));
                if (h.CpuTemperature >= settings.Current.Fan.CpuEmergencyTemperature) issues.Add(("critical", "CPU 温度过高"));
                else if (h.CpuTemperature >= settings.Current.Fan.CpuBoostTemperature) issues.Add(("warning", "CPU 高温"));
                if (h.MemoryUsage >= 95) issues.Add(("warning", "内存使用率超过 95%"));
                if (h.Error is not null || h.CpuTemperature is null || DateTimeOffset.UtcNow - h.Timestamp > TimeSpan.FromSeconds(30)) issues.Add(("unknown", "硬件采样缺失或过期"));
                if (cache.Fan.Health.Level != "healthy") issues.Add((cache.Fan.Health.Level, string.Join("；", cache.Fan.Health.Messages)));
                foreach (var bay in cache.Bays.Where(x => x.Serial is not null && x.Status != "healthy")) issues.Add((bay.Status == "offline" ? "critical" : bay.Status, $"BAY {bay.Bay:00}：" + string.Join("；", bay.Disk?.Health.Messages ?? ["等待磁盘发现"])));
                foreach (var disk in cache.Disks.Values.Where(d => !cache.Bays.Any(b => b.Serial == d.Serial) && d.Health.Level != "healthy")) issues.Add((disk.Health.Level == "offline" ? "critical" : disk.Health.Level, $"未分配磁盘 {disk.Model}：{string.Join("；", disk.Health.Messages)}"));
                var level = issues.Any(i => i.Level == "critical") ? "critical" : issues.Any(i => i.Level == "warning") ? "warning" : issues.Count > 0 ? "unknown" : "healthy";
                cache.Health = new(level, issues.Count == 0 ? ["所有已检测组件运行正常"] : issues.Select(i => i.Message).ToArray());
                await events.Transition("system-health", level == "critical" ? "critical" : level == "healthy" ? "info" : "warning", "SYSTEM", "HEALTH", string.Join("；", cache.Health.Messages), stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "健康状态计算失败"); }
            await Task.Delay(5000, stoppingToken);
        }
    }
}
public sealed class MetricsCleanupWorker(StationDatabase db, SettingsService settings, ILogger<MetricsCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await db.Cleanup(settings.Current, stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "历史数据清理失败"); }
            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }
}

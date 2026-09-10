using StorageStation.Web.Data;
using StorageStation.Web.Services;
namespace StorageStation.Web.Workers;

public sealed class SelfTestScheduleWorker(StationDatabase db, SettingsService settings, StateCache cache, DiskService disks, ILogger<SelfTestScheduleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            try
            {
                if (cache.Disks.Values.Any(d => d.SelfTest.Running)) continue; // only one scheduled test per server at a time.
                var now = DateTimeOffset.UtcNow;
                var month = now.ToString("yyyy-MM");
                var week = System.Globalization.ISOWeek.GetYear(now.UtcDateTime) + "-" + System.Globalization.ISOWeek.GetWeekOfYear(now.UtcDateTime);
                foreach (var d in cache.Disks.Values.Where(d => d.Online).OrderBy(d => d.Id))
                {
                    string? kind = null, period = null;
                    if (settings.Current.MonthlyLongTest && await db.GetSetting("schedule-long-" + d.Id, stoppingToken) != month) { kind = "long"; period = month; }
                    else if (settings.Current.WeeklyShortTest && await db.GetSetting("schedule-short-" + d.Id, stoppingToken) != week) { kind = "short"; period = week; }
                    if (kind is null) continue;
                    // Persist attempt BEFORE invoking hardware: a crash must not repeatedly start a test.
                    await db.SetSetting($"schedule-{kind}-{d.Id}", period!, stoppingToken);
                    try { await disks.SelfTest(d.Id, kind, stoppingToken); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { await db.AddEvent("warning", d.Serial, "SCHEDULE_FAILED", "计划自检未启动：" + ex.Message, ct: stoppingToken); }
                    break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "计划自检失败"); }
        }
    }
}

using System.Diagnostics.Eventing.Reader;
using StorageStation.Web.Data;
namespace StorageStation.Web.Workers;

public sealed class WindowsEventWorker(StationDatabase db, IHostEnvironment env, ILogger<WindowsEventWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        if (!OperatingSystem.IsWindows() || env.IsDevelopment()) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var stored = await db.GetSetting("windows-event-since", stoppingToken);
                var since = DateTimeOffset.TryParse(stored, out var previous) ? previous : DateTimeOffset.UtcNow.AddMinutes(-5);
                var until = DateTimeOffset.UtcNow;
                var query = new EventLogQuery("System", PathType.LogName, $"*[System[(Level=1 or Level=2 or Level=3) and TimeCreated[@SystemTime > '{since.UtcDateTime:O}' and @SystemTime <= '{until.UtcDateTime:O}']]]");
                using var reader = new EventLogReader(query);
                for (var count = 0; count < 200; count++)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    using var entry = reader.ReadEvent();
                    if (entry is null) { await db.SetSetting("windows-event-since", until.ToString("O"), stoppingToken); break; }
                    string message;
                    try { message = entry.FormatDescription() ?? "无事件说明"; } catch (EventLogException) { message = "无法读取事件说明"; }
                    await db.AddEvent(entry.Level <= 2 ? "critical" : "warning", "Windows / " + entry.ProviderName, "WIN_" + entry.Id,
                        message.Length > 4000 ? message[..4000] : message, $"RecordId={entry.RecordId}; TimeCreated={entry.TimeCreated:O}", stoppingToken);
                    await db.SetSetting("windows-event-since", new DateTimeOffset(entry.TimeCreated?.ToUniversalTime() ?? until.UtcDateTime).ToString("O"), stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "读取 Windows System 事件失败"); }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}

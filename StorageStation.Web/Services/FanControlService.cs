using StorageStation.Web.Hardware;
using StorageStation.Web.Models;
namespace StorageStation.Web.Services;

public sealed class FanControlService(IFanController controller, SettingsService settings, StateCache cache, EventService events, ILogger<FanControlService> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly FanEngine engine = new();
    private string mode = "auto";
    private double manualPercent = 100;
    private DateTimeOffset? manualUntil, stalledSince;
    private bool softwareActive;
    private bool stalledLatched;
    public async Task SetMode(string value, CancellationToken ct)
    {
        if (value is not ("auto" or "bios")) throw new ArgumentException("模式必须为 auto 或 bios；手动模式请指定转速和时限");
        await gate.WaitAsync(ct);
        try
        {
            if (value != "bios" && (!settings.Current.Fan.Enabled || !await controller.CanControlAsync(ct))) throw new NotSupportedException("风扇控制未启用或硬件不支持");
            await controller.SetDefaultAsync(ct);
            softwareActive = false; mode = value; manualUntil = null; engine.Reset();
        }
        finally { gate.Release(); }
        await events.Transition("fan-mode", "info", "FAN", "MODE", $"风扇模式切换为 {value}", ct);
    }
    public async Task SetManual(double percent, int minutes, CancellationToken ct)
    {
        var s = settings.Current.Fan;
        if (!double.IsFinite(percent) || percent < s.MinimumPercent || percent > s.MaximumPercent || minutes is < 1 or > 60) throw new ArgumentException("PWM 必须在安全范围内，手动时限为 1–60 分钟");
        await gate.WaitAsync(ct);
        try
        {
            if (!s.Enabled || !await controller.CanControlAsync(ct)) throw new NotSupportedException("风扇控制未启用或硬件不支持");
            mode = "manual"; manualPercent = percent; manualUntil = DateTimeOffset.UtcNow.AddMinutes(minutes); engine.Reset();
        }
        finally { gate.Release(); }
        await events.Transition("fan-mode", "info", "FAN", "MANUAL", $"手动风扇 {percent}% / {minutes} 分钟", ct);
    }
    public async Task Tick(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var s = settings.Current;
            var h = cache.Hardware;
            var available = await controller.CanControlAsync(ct);
            if (manualUntil <= now && mode == "manual")
            {
                mode = "auto"; manualUntil = null; engine.Reset();
                await events.Transition("fan-mode", "info", "FAN", "MANUAL_EXPIRED", "手动控制到期，恢复自动曲线", ct);
            }
            var activeMode = !s.Fan.Enabled || !available ? "bios" : mode;
            double? target = null, output = h.FanPercent, temperature = null;
            string? safety = null;
            if (activeMode == "bios")
            {
                if (softwareActive) { await controller.SetDefaultAsync(ct); softwareActive = false; engine.Reset(); }
            }
            else
            {
                var disks = cache.Disks.Values.Where(d => d.Online).ToArray();
                var diskTemp = disks.Where(d => d.TemperatureTimestamp.HasValue && now - d.TemperatureTimestamp.Value < TimeSpan.FromSeconds(s.DiskTemperatureIntervalSeconds * 3)).Select(d => d.Temperature).Max();
                // Any known/mapped disk with unavailable temperature invalidates the cooling input.
                var diskFresh = disks.Length > 0 && disks.All(d => d.Temperature.HasValue && d.TemperatureTimestamp.HasValue && now - d.TemperatureTimestamp.Value < TimeSpan.FromSeconds(s.DiskTemperatureIntervalSeconds * 3)) && cache.Bays.All(b => b.Serial is null || b.Disk?.Online == true);
                var noDisksExpected = disks.Length == 0 && cache.Bays.All(b => b.Serial is null);
                var fresh = now - h.Timestamp < TimeSpan.FromSeconds(Math.Max(5, s.RealtimeIntervalMs / 1000d * 3)) && (s.Fan.TemperatureSource == "cpu" && noDisksExpected || diskFresh);
                var decision = engine.Decide(s.Fan, h.CpuTemperature, diskTemp, fresh, activeMode, manualPercent, now);
                target = decision.Target; output = decision.Output; temperature = decision.Temperature; safety = decision.SafetyReason;
            }
            var health = !available ? Health.Unknown("当前主板仅提供风扇转速读取，未检测到已绑定的可写控制器") : new Health("healthy", ["风扇运行正常"]);
            if (h.FanRpm > 0) stalledLatched = false;
            if (output > 30 && h.FanRpm == 0)
            {
                stalledSince ??= now;
                if (now - stalledSince >= TimeSpan.FromSeconds(s.Fan.StallSeconds))
                {
                    stalledLatched = true;
                }
            }
            else stalledSince = null;
            if (stalledLatched)
            {
                health = new("critical", ["风扇输出大于 30%，但 RPM 持续为 0"]);
                safety = "风扇失速保护";
                if (activeMode != "bios") output = target = 100;
            }
            if (health.Level != "critical" && s.Fan.MinimumSafeRpm is { } minimum && output >= 50 && h.FanRpm < minimum) health = new("warning", ["风扇 RPM 低于配置的最低安全值"]);
            if (health.Level != "critical" && safety is not null) health = new("warning", [safety]);
            if (health.Level == "healthy" && h.FanRpm is null) health = Health.Unknown("风扇转速传感器不可用");
            if (activeMode != "bios") { await controller.SetPercentAsync(output!.Value, ct); softwareActive = true; }
            cache.Fan = new("chassis", activeMode, h.FanRpm.HasValue, available, h.FanRpm, output, target, s.Fan.TemperatureSource, temperature, manualUntil, safety, health);
            await events.Transition("fan-health", health.Level == "critical" ? "critical" : health.Level is "warning" or "unknown" ? "warning" : "info", "FAN", "HEALTH", string.Join("；", health.Messages), ct);
        }
        finally { gate.Release(); }
    }
    public async Task Recover(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await controller.SetDefaultAsync(ct); softwareActive = false; mode = "bios"; manualUntil = null; engine.Reset();
        }
        finally { gate.Release(); }
    }
    public async Task SafeStop()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try { await Recover(timeout.Token); }
        catch (Exception ex) { logger.LogCritical(ex, "风扇恢复 BIOS 失败，需要人工检查"); }
    }
}

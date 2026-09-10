namespace StorageStation.Web.Models;

public record FanSettings
{
    public bool Enabled { get; init; } = false;
    public double MinimumPercent { get; init; } = 30;
    public double MaximumPercent { get; init; } = 100;
    public string TemperatureSource { get; init; } = "disk";
    public double CpuBoostTemperature { get; init; } = 70;
    public double CpuEmergencyTemperature { get; init; } = 80;
    public double DiskEmergencyTemperature { get; init; } = 50;
    public double RiseHysteresis { get; init; } = 1;
    public double FallHysteresis { get; init; } = 2;
    public int RiseDelaySeconds { get; init; } = 3;
    public int FallDelaySeconds { get; init; } = 30;
    public double MaximumStep { get; init; } = 10;
    public int StallSeconds { get; init; } = 10;
    public double? MinimumSafeRpm { get; init; }
    public CurvePoint[] Curve { get; init; } = [new(32, 30), new(35, 35), new(40, 50), new(45, 70), new(50, 100)];
}
public record StationSettings
{
    public string DisplayName { get; init; } = "Storage Station";
    public int RealtimeIntervalMs { get; init; } = 1000;
    public int SmartIntervalSeconds { get; init; } = 300;
    public int DiskTemperatureIntervalSeconds { get; init; } = 30;
    public int SystemRetentionDays { get; init; } = 30;
    public int DiskRetentionDays { get; init; } = 365;
    public int EventRetentionDays { get; init; } = 365;
    public double DiskWarningTemperature { get; init; } = 45;
    public double DiskCriticalTemperature { get; init; } = 50;
    public string? CpuTemperatureSensorId { get; init; }
    public string? FanRpmSensorId { get; init; }
    public string? FanControlSensorId { get; init; }
    public string WebSocketPath { get; init; } = "/novnc/websockify";
    public bool WeeklyShortTest { get; init; }
    public bool MonthlyLongTest { get; init; }
    public FanSettings Fan { get; init; } = new();

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 80) return "主机显示名称需为 1–80 字符";
        if (RealtimeIntervalMs is < 1000 or > 10000 || SmartIntervalSeconds is < 60 or > 3600 || DiskTemperatureIntervalSeconds is < 15 or > 300)
            return "采样周期超出允许范围：实时 1000–10000ms，SMART 60–3600s，温度 15–300s";
        if (SystemRetentionDays is < 1 or > 90 || DiskRetentionDays is < 30 or > 3650 || EventRetentionDays is < 30 or > 3650) return "历史保留时间超出范围";
        if (!double.IsFinite(DiskWarningTemperature) || !double.IsFinite(DiskCriticalTemperature) || DiskWarningTemperature < 30 || DiskCriticalTemperature > 70 || DiskWarningTemperature >= DiskCriticalTemperature) return "磁盘告警温度设置无效";
        if (WebSocketPath != "/novnc/websockify") return "V1 WebSocket 路径固定为 /novnc/websockify";
        if (Fan is null || Fan.Curve is null) return "缺少风扇配置";
        if (Fan.TemperatureSource is not ("disk" or "cpu" or "max")) return "不支持的温度来源";
        double[] values = [Fan.MinimumPercent, Fan.MaximumPercent, Fan.CpuBoostTemperature, Fan.CpuEmergencyTemperature, Fan.DiskEmergencyTemperature, Fan.RiseHysteresis, Fan.FallHysteresis, Fan.MaximumStep];
        if (values.Any(x => !double.IsFinite(x))) return "风扇数值必须有限";
        if (Fan.MinimumPercent < 30 || Fan.MaximumPercent > 100 || Fan.MaximumPercent < Fan.MinimumPercent) return "正常输出范围需为 30–100%；紧急保护总是输出 100%";
        if (Fan.CpuBoostTemperature < 40 || Fan.CpuEmergencyTemperature > 90 || Fan.CpuBoostTemperature >= Fan.CpuEmergencyTemperature || Fan.DiskEmergencyTemperature is < 40 or > 60) return "安全温度阈值无效";
        if (Fan.RiseHysteresis is < 0 or > 5 || Fan.FallHysteresis is < 0 or > 10 || Fan.RiseDelaySeconds is < 0 or > 10 || Fan.FallDelaySeconds is < 1 or > 120 || Fan.MaximumStep is <= 0 or > 20 || Fan.StallSeconds is < 3 or > 30) return "防抖 / 失速保护参数无效";
        if (Fan.MinimumSafeRpm is { } rpm && (!double.IsFinite(rpm) || rpm is < 0 or > 10000)) return "安全 RPM 无效";
        if (Fan.Curve.Length is < 2 or > 12) return "曲线需为 2–12 个温度点";
        for (var i = 0; i < Fan.Curve.Length; i++)
        {
            var p = Fan.Curve[i];
            if (p is null || !double.IsFinite(p.Temperature) || !double.IsFinite(p.Percent) || p.Temperature is < 0 or > 100 || p.Percent < Fan.MinimumPercent || p.Percent > Fan.MaximumPercent) return "曲线点超出允许范围";
            if (i > 0 && (p.Temperature <= Fan.Curve[i - 1].Temperature || p.Percent < Fan.Curve[i - 1].Percent)) return "温度必须严格递增，PWM 不得递减";
        }
        return null;
    }
}

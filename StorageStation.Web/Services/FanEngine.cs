using StorageStation.Web.Models;
namespace StorageStation.Web.Services;

public record FanDecision(double Output, double Target, double? Temperature, string? SafetyReason);

/// <summary>Pure state machine; caller supplies time so delays and emergency behavior are testable.</summary>
public sealed class FanEngine
{
    private double? output, referenceTemperature;
    private DateTimeOffset? pendingSince;
    private int pendingDirection;
    private bool transitioning;
    private bool recoveringOverride;
    public static double Interpolate(IReadOnlyList<CurvePoint> curve, double temperature)
    {
        if (temperature <= curve[0].Temperature) return curve[0].Percent;
        for (var i = 1; i < curve.Count; i++)
            if (temperature <= curve[i].Temperature)
                return curve[i - 1].Percent + (curve[i].Percent - curve[i - 1].Percent) *
                    (temperature - curve[i - 1].Temperature) / (curve[i].Temperature - curve[i - 1].Temperature);
        return curve[^1].Percent;
    }
    public void Reset() { output = referenceTemperature = null; pendingSince = null; pendingDirection = 0; transitioning = false; recoveringOverride = false; }
    public FanDecision Decide(FanSettings s, double? cpu, double? disk, bool sensorsFresh, string mode, double manualPercent, DateTimeOffset now)
    {
        double? temperature = s.TemperatureSource switch { "cpu" => cpu, "max" => cpu.HasValue && disk.HasValue ? Math.Max(cpu.Value, disk.Value) : null, _ => disk };
        string? safety = !sensorsFresh || !cpu.HasValue || !temperature.HasValue ? "温度缺失或过期，保护输出 100%" :
            cpu >= s.CpuEmergencyTemperature ? "CPU 高温紧急保护" : disk >= s.DiskEmergencyTemperature ? "硬盘高温紧急保护" : null;
        if (safety is not null)
        {
            output = 100; referenceTemperature = temperature; pendingSince = null; transitioning = false; recoveringOverride = true;
            return new(100, 100, temperature, safety);
        }
        var target = Math.Clamp(mode == "manual" ? manualPercent : Interpolate(s.Curve, temperature!.Value), s.MinimumPercent, s.MaximumPercent);
        var boost = cpu >= s.CpuBoostTemperature;
        if (boost) target = Math.Max(70, target);
        if (output is null) { output = 100; referenceTemperature = temperature; } // start high, then settle safely.
        if (boost) recoveringOverride = true;
        if (boost && output < 70) { output = 70; pendingSince = null; }
        var direction = Math.Sign(target - output.Value);
        if (direction == 0) { pendingSince = null; transitioning = false; return new(output.Value, target, temperature, boost ? "CPU 高温：最低 70%" : null); }
        var delta = temperature!.Value - (referenceTemperature ?? temperature.Value);
        var passes = mode == "manual" || transitioning || recoveringOverride || (direction > 0 ? delta >= s.RiseHysteresis : delta <= -s.FallHysteresis) || (output == 100 && !boost);
        if (!passes) { pendingSince = null; return new(output.Value, target, temperature, boost ? "CPU 高温：最低 70%" : null); }
        if (pendingSince is null || pendingDirection != direction)
        {
            pendingSince = now; pendingDirection = direction; transitioning = false;
        }
        var delay = mode == "manual" ? 0 : direction > 0 ? s.RiseDelaySeconds : s.FallDelaySeconds;
        if (now - pendingSince >= TimeSpan.FromSeconds(delay))
        {
            transitioning = true;
            output += Math.Clamp(target - output.Value, -s.MaximumStep, s.MaximumStep);
            if (Math.Abs(target - output.Value) < 0.01) { referenceTemperature = temperature; transitioning = false; pendingSince = null; recoveringOverride = boost; }
        }
        return new(output.Value, target, temperature, boost ? "CPU 高温：最低 70%" : null);
    }
}

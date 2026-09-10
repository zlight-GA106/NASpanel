using LibreHardwareMonitor.Hardware;
using StorageStation.Web.Models;
namespace StorageStation.Web.Hardware;

public static class SensorDiscovery
{
    public static IEnumerable<IHardware> Flatten(IEnumerable<IHardware> hardware)
    {
        foreach (var h in hardware)
        {
            yield return h;
            foreach (var child in Flatten(h.SubHardware)) yield return child;
        }
    }
    public static SensorInfo Describe(ISensor s) => new(s.Identifier.ToString(), s.Name, s.SensorType.ToString(), s.Hardware.Name,
        s.Hardware.HardwareType.ToString(), s.Value is { } v && float.IsFinite(v) ? v : null, s.Control is not null);
    public static double? SelectTemperature(SensorInfo[] sensors, string? identifier)
    {
        if (!string.IsNullOrWhiteSpace(identifier)) return sensors.FirstOrDefault(x => x.Id == identifier && x.Type == "Temperature")?.Value;
        var cpu = sensors.Where(x => x.HardwareType == "Cpu" && x.Type == "Temperature" && x.Value is not null &&
            !x.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase)).ToArray();
        // Package is the most useful default. Fall back to the hottest real CPU temperature.
        return cpu.FirstOrDefault(x => x.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))?.Value ?? cpu.Select(x => x.Value).Max();
    }
}

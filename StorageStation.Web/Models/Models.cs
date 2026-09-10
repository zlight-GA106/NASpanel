namespace StorageStation.Web.Models;

public record Health(string Level, string[] Messages)
{
    public static Health Unknown(string message) => new("unknown", [message]);
}
public record SensorInfo(string Id, string Name, string Type, string Hardware, string HardwareType, double? Value, bool Writable);
public record HardwareSnapshot(DateTimeOffset Timestamp, string Hostname, string Os, double UptimeSeconds,
    string CpuName, double? CpuUsage, double? CpuTemperature, double? MemoryUsage, double? MemoryUsedGb,
    double? MemoryTotalGb, double? FanRpm, double? FanPercent, bool IsMock, string? Error)
{
    public static HardwareSnapshot Empty => new(DateTimeOffset.MinValue, Environment.MachineName,
        System.Runtime.InteropServices.RuntimeInformation.OSDescription, 0, "--", null, null, null, null, null, null, null, false, "等待硬件采样");
}
public record SmartAttribute(int Id, string Name, long? Raw, string RawText, int? Normalized, int? Worst, int? Threshold, string Status);
public record SelfTestInfo(string Status, bool Running, int? RemainingPercent, bool Failed);
public record DiskDevice(string Path, string? Type);
public record DiskStatus
{
    public required string Id { get; init; }
    public required string Serial { get; init; }
    public required string Model { get; init; }
    public string Kind { get; init; } = "HDD";
    public string Protocol { get; init; } = "SATA";
    public long? CapacityBytes { get; init; }
    public bool Online { get; init; } = true;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? TemperatureTimestamp { get; init; }
    public double? Temperature { get; init; }
    public long? PowerOnHours { get; init; }
    public long? PowerCycles { get; init; }
    public long? Reallocated { get; init; }
    public long? Pending { get; init; }
    public long? Uncorrectable { get; init; }
    public long? CrcErrors { get; init; }
    public long? PercentageUsed { get; init; }
    public long? MediaErrors { get; init; }
    public long? CriticalWarning { get; init; }
    public bool? OverallPassed { get; init; }
    public bool ReadError { get; init; }
    public bool IsMock { get; init; }
    public Health Health { get; init; } = Health.Unknown("等待 SMART 采样");
    public SmartAttribute[] Attributes { get; init; } = [];
    public Dictionary<string, string> Nvme { get; init; } = [];
    public SelfTestInfo SelfTest { get; init; } = new("未知", false, null, false);
}
public record BayState(int Bay, string? Serial, DiskStatus? Disk, string Status);
public record FanStatus(string Id, string Mode, bool CanReadRpm, bool CanControl, double? Rpm,
    double? Output, double? Target, string Source, double? Temperature, DateTimeOffset? ManualUntil,
    string? SafetyReason, Health Health);
public record EventRecord(long Id, DateTimeOffset Timestamp, string Level, string Source, string Code, string Message, string? Details, bool Acknowledged);
public record CurvePoint(double Temperature, double Percent);

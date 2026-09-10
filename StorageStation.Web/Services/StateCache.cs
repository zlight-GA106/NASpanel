using System.Collections.Concurrent;
using StorageStation.Web.Models;

namespace StorageStation.Web.Services;

public sealed class StateCache
{
    private HardwareSnapshot hardware = HardwareSnapshot.Empty;
    private SensorInfo[] sensors = [];
    private FanStatus fan = new("chassis", "bios", false, false, null, null, null, "disk", null, null, null, Health.Unknown("等待风扇采样"));
    private BayState[] bays = Enumerable.Range(1, 8).Select(i => new BayState(i, null, null, "empty")).ToArray();
    private Health health = Health.Unknown("正在初始化");
    private string? smartError = "等待 SMART 扫描";
    public string? SmartError { get => Volatile.Read(ref smartError); set => Volatile.Write(ref smartError, value); }
    public HardwareSnapshot Hardware { get => Volatile.Read(ref hardware); set => Volatile.Write(ref hardware, value); }
    public SensorInfo[] Sensors { get => Volatile.Read(ref sensors); set => Volatile.Write(ref sensors, value); }
    public FanStatus Fan { get => Volatile.Read(ref fan); set => Volatile.Write(ref fan, value); }
    public BayState[] Bays { get => Volatile.Read(ref bays); set => Volatile.Write(ref bays, value); }
    public Health Health { get => Volatile.Read(ref health); set => Volatile.Write(ref health, value); }
    public ConcurrentDictionary<string, DiskStatus> Disks { get; } = new();
    public object SystemView(string displayName) => new { status = Health.Level, health = Health, displayName, Hardware.Timestamp, Hardware.Hostname, Hardware.Os, Hardware.UptimeSeconds, Hardware.IsMock, Hardware.Error,
        cpu = new { name = Hardware.CpuName, usage = Hardware.CpuUsage, temperature = Hardware.CpuTemperature },
        memory = new { usage = Hardware.MemoryUsage, usedGb = Hardware.MemoryUsedGb, totalGb = Hardware.MemoryTotalGb }, fan = Fan,
        disks = new { totalBays = 8, online = Bays.Count(b => b.Disk?.Online == true), warning = Bays.Count(b => b.Disk?.Health.Level == "warning"), critical = Bays.Count(b => b.Disk?.Health.Level == "critical") } };
}

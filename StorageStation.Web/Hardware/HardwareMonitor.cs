using LibreHardwareMonitor.Hardware;
using StorageStation.Web.Models;
using StorageStation.Web.Services;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace StorageStation.Web.Hardware;

public sealed class HardwareMonitor(SettingsService settings, ILogger<HardwareMonitor> logger) : IHardwareProvider, IAsyncDisposable
{
    private readonly SemaphoreSlim access = new(1, 1);
    private Computer? computer;
    private Dictionary<string, ISensor> sensors = [];
    private readonly HashSet<IControl> touched = [];
    private readonly HashSet<string> logged = [];
    private DateTimeOffset lastSlow = DateTimeOffset.MinValue;
    private SensorInfo[] slow = [];

    public async Task<(HardwareSnapshot, SensorInfo[])> ReadAsync(CancellationToken ct)
    {
        await access.WaitAsync(ct);
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("真实硬件采集仅支持 Windows");
            if (computer is null)
            {
                computer = new Computer { IsCpuEnabled = true, IsMemoryEnabled = true, IsMotherboardEnabled = true, IsControllerEnabled = true,
                    IsStorageEnabled = false }; // smartctl owns storage I/O; avoid per-second disk wakeups.
                try { computer.Open(); } catch { computer.Close(); computer = null; throw; }
            }
            var hardware = SensorDiscovery.Flatten(computer.Hardware).ToArray();
            var now = DateTimeOffset.UtcNow;
            var refreshSlow = now - lastSlow >= TimeSpan.FromSeconds(2);
            var errors = new List<string>();
            var fresh = new List<SensorInfo>();
            foreach (var h in hardware)
            {
                if (!refreshSlow && h.HardwareType is not (HardwareType.Cpu or HardwareType.Memory))
                {
                    fresh.AddRange(slow.Where(x => x.Hardware == h.Name));
                    continue;
                }
                try { h.Update(); fresh.AddRange(h.Sensors.Select(SensorDiscovery.Describe)); }
                catch (Exception ex) { logger.LogWarning(ex, "硬件更新失败 {Hardware}", h.Name); errors.Add(h.Name); }
            }
            sensors = hardware.SelectMany(x => x.Sensors).GroupBy(x => x.Identifier.ToString()).ToDictionary(x => x.Key, x => x.First());
            var all = fresh.DistinctBy(x => x.Id).ToArray();
            if (refreshSlow) { lastSlow = now; slow = all; }
            foreach (var s in all.Where(s => logged.Add(s.Id))) logger.LogInformation("Sensor {Hardware} {Name} {Type} {Id} Writable={Writable}", s.Hardware, s.Name, s.Type, s.Id, s.Writable);
            var cpu = all.Where(s => s.HardwareType == "Cpu" && s.Type == "Load").ToArray();
            var load = cpu.FirstOrDefault(x => x.Name.Contains("Total", StringComparison.OrdinalIgnoreCase))?.Value ?? cpu.Select(x => x.Value).Average();
            var memory = ReadMemory();
            var conf = settings.Current;
            // Read-only fan discovery is safe. Writable output always requires an explicit identifier.
            var fan = string.IsNullOrWhiteSpace(conf.FanRpmSensorId) ? all.FirstOrDefault(x => x.Type == "Fan") : all.FirstOrDefault(x => x.Id == conf.FanRpmSensorId && x.Type == "Fan");
            var pwm = all.FirstOrDefault(x => x.Id == conf.FanControlSensorId && x.Type == "Control");
            return (new(now, Environment.MachineName, WindowsDescription(), Environment.TickCount64 / 1000d,
                hardware.FirstOrDefault(x => x.HardwareType == HardwareType.Cpu)?.Name ?? "--", load, SensorDiscovery.SelectTemperature(all, conf.CpuTemperatureSensorId),
                memory.Usage, memory.Used, memory.Total, fan?.Value, pwm?.Value, false, errors.Count > 0 ? "部分硬件不可用：" + string.Join("、", errors) : null), all);
        }
        finally { access.Release(); }
    }
    private IControl? BoundControl()
    {
        var id = settings.Current.FanControlSensorId;
        return id is not null && sensors.TryGetValue(id, out var sensor) && sensor.SensorType == SensorType.Control ? sensor.Control : null;
    }
    public async Task<bool> CanControl(CancellationToken ct)
    {
        await access.WaitAsync(ct);
        try { return BoundControl() is not null; } finally { access.Release(); }
    }
    public async Task SetPercent(double percent, CancellationToken ct)
    {
        if (!double.IsFinite(percent) || percent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        await access.WaitAsync(ct);
        try
        {
            var control = BoundControl() ?? throw new NotSupportedException("未绑定可写风扇控制器");
            // Return old controller to BIOS before applying a different binding.
            foreach (var old in touched.Where(x => x != control).ToArray()) { old.SetDefault(); touched.Remove(old); }
            if (percent < control.MinSoftwareValue || percent > control.MaxSoftwareValue) throw new NotSupportedException("目标输出超出硬件支持范围");
            touched.Add(control); // include partially failed writes in restoration.
            control.SetSoftware((float)percent);
        }
        finally { access.Release(); }
    }
    public async Task Restore(CancellationToken ct)
    {
        await access.WaitAsync(ct);
        try
        {
            List<Exception> failures = [];
            foreach (var control in touched.ToArray())
                try { control.SetDefault(); touched.Remove(control); }
                catch (Exception ex) { failures.Add(ex); logger.LogError(ex, "恢复 BIOS 风扇控制失败"); }
            if (failures.Count > 0) throw new AggregateException(failures);
        }
        finally { access.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await Restore(timeout.Token); } catch (Exception ex) { logger.LogError(ex, "退出时恢复 BIOS 失败"); }
        computer?.Close();
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
    private static (double? Usage, double? Used, double? Total) ReadMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? (status.Load, (status.TotalPhysical - status.AvailablePhysical) / 1073741824d, status.TotalPhysical / 1073741824d) : (null, null, null);
    }
    private static string WindowsDescription()
    {
        if (!OperatingSystem.IsWindows()) return RuntimeInformation.OSDescription;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return key?.GetValue("ProductName") is string name ? name + " · Build " + key.GetValue("CurrentBuildNumber") : RuntimeInformation.OSDescription;
        }
        catch { return RuntimeInformation.OSDescription; }
    }
}

public sealed class LibreHardwareMonitorFanController(HardwareMonitor monitor) : IFanController
{
    public Task<bool> CanControlAsync(CancellationToken ct) => monitor.CanControl(ct);
    public Task SetPercentAsync(double percent, CancellationToken ct) => monitor.SetPercent(percent, ct);
    public Task SetDefaultAsync(CancellationToken ct) => monitor.Restore(ct);
}

using System.Collections.Concurrent;
using StorageStation.Web.Models;
namespace StorageStation.Web.Storage;

public sealed class MockDiskProvider : IDiskProvider
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> tests = new();
    public Task<DiskDevice[]> ScanAsync(CancellationToken ct) => Task.FromResult(Enumerable.Range(1, 8).Select(i => new DiskDevice($"mock:{i}", "mock")).ToArray());
    public Task<DiskStatus> ReadAsync(DiskDevice device, bool full, CancellationToken ct)
    {
        var n = int.Parse(device.Path.Split(':')[1]);
        var temp = 32 + n % 4 * 2 + Math.Sin(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 80d + n);
        var id = $"mock-disk-{n:00}";
        var remaining = tests.TryGetValue(id, out var until) ? Math.Max(0, (until - DateTimeOffset.UtcNow).TotalSeconds) : 0;
        var reallocated = n == 3 ? 2 : 0;
        var crc = n == 6 ? 10 : 0;
        return Task.FromResult(new DiskStatus { Id = id, Serial = $"DEMO-SERIAL-{n:00}", Model = (n % 3) switch { 0 => "HGST HUS724040ALA640", 1 => "WDC WD40EFRX-68WT0N0", _ => "ST4000VN008-2DR166" },
            CapacityBytes = 4000787030016, Temperature = Math.Round(temp, 1), TemperatureTimestamp = DateTimeOffset.UtcNow, PowerOnHours = 9218 + n * 1311,
            PowerCycles = 400 + n * 37, Reallocated = reallocated, Pending = 0, Uncorrectable = 0, CrcErrors = crc, OverallPassed = true, IsMock = true,
            SelfTest = new(remaining > 0 ? "模拟自检运行中" : tests.ContainsKey(id) ? "模拟自检通过" : "模拟短自检通过", remaining > 0, remaining > 0 ? (int)(remaining / 60 * 100) : null, false),
            Attributes = [new(5, "Reallocated_Sector_Ct", reallocated, reallocated.ToString(), 200, 200, 140, "正常"), new(9, "Power_On_Hours", 9218 + n * 1311, (9218 + n * 1311).ToString(), 95, 95, 0, "正常"),
                new(194, "Temperature_Celsius", (long)temp, $"{temp:F0}", 110, 100, 0, "正常"), new(197, "Current_Pending_Sector", 0, "0", 200, 200, 0, "正常"), new(198, "Offline_Uncorrectable", 0, "0", 200, 200, 0, "正常"), new(199, "UDMA_CRC_Error_Count", crc, crc.ToString(), 200, 200, 0, "正常")] });
    }
    public Task<string> SelfTestAsync(DiskDevice device, string expectedId, string kind, CancellationToken ct)
    {
        if (tests.TryGetValue(expectedId, out var until) && until > DateTimeOffset.UtcNow) throw new InvalidOperationException("模拟自检已在运行");
        tests[expectedId] = DateTimeOffset.UtcNow.AddSeconds(60);
        return Task.FromResult("模拟自检已开始，约 1 分钟完成");
    }
}

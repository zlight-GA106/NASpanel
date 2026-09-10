using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StorageStation.Web.Models;

namespace StorageStation.Web.Storage;

public static class SmartParser
{
    public static JsonElement At(JsonElement e, params string[] path)
    {
        foreach (var part in path) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(part, out e)) return default; }
        return e;
    }
    public static long? Number(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var n) ? n : null;
    public static string? Str(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    public static bool? Bool(JsonElement e) => e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : null;
    public static DiskStatus Parse(JsonElement root, int exitCode)
    {
        var model = Str(At(root, "model_name")) ?? Str(At(root, "scsi_model_name")) ?? Str(At(root, "product")) ?? "未知型号";
        var serial = Str(At(root, "serial_number"));
        var wwn = At(root, "wwn");
        if (string.IsNullOrWhiteSpace(serial)) serial = wwn.ValueKind == JsonValueKind.Object ? "WWN:" + wwn.GetRawText() : null;
        if (string.IsNullOrWhiteSpace(serial)) throw new InvalidDataException("磁盘未提供序列号或 WWN；拒绝使用不稳定设备路径作为永久身份");
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(model.Trim() + "|" + serial.Trim())))[..24].ToLowerInvariant();
        var nvme = At(root, "nvme_smart_health_information_log");
        var protocol = Str(At(root, "device", "protocol")) ?? "SATA";
        var kind = nvme.ValueKind == JsonValueKind.Object || protocol == "NVMe" ? "NVMe" : Number(At(root, "rotation_rate")) == 0 ? "SSD" : "HDD";
        List<SmartAttribute> attributes = [];
        var table = At(root, "ata_smart_attributes", "table");
        if (table.ValueKind == JsonValueKind.Array)
            foreach (var a in table.EnumerateArray()) attributes.Add(new((int)(Number(At(a, "id")) ?? 0), Str(At(a, "name")) ?? "Unknown",
                Number(At(a, "raw", "value")), Str(At(a, "raw", "string")) ?? "--", (int?)Number(At(a, "value")), (int?)Number(At(a, "worst")),
                (int?)Number(At(a, "thresh")), Str(At(a, "when_failed")) is { Length: > 0 } failure && failure != "-" ? failure : "正常"));
        long? Raw(int attr) => attributes.FirstOrDefault(x => x.Id == attr)?.Raw;
        var execution = At(root, "ata_smart_data", "self_test", "status");
        var executionValue = Number(At(execution, "value"));
        var running = executionValue is >= 240;
        var remaining = (int?)Number(At(execution, "remaining_percent"));
        var latest = At(root, "ata_smart_self_test_log", "standard", "table");
        if (latest.ValueKind != JsonValueKind.Array) latest = At(root, "ata_smart_self_test_log", "extended", "table");
        var testStatus = Str(At(execution, "string")) ?? "无自检记录";
        var failed = Bool(At(execution, "passed")) == false;
        if (latest.ValueKind == JsonValueKind.Array && latest.GetArrayLength() > 0)
        {
            var first = latest[0];
            failed = Bool(At(first, "status", "passed")) == false;
            var statusCode = Number(At(first, "status", "value"));
            failed |= (statusCode >> 4) is >= 4 and <= 8;
            if (!running) testStatus = Str(At(first, "status", "string")) ?? testStatus;
        }
        var nvmeTest = At(root, "nvme_self_test_log");
        if (nvmeTest.ValueKind == JsonValueKind.Object)
        {
            running = Number(At(nvmeTest, "current_self_test_operation", "value")) > 0;
            remaining = running ? 100 - (int?)Number(At(nvmeTest, "current_self_test_completion_percent")) : null;
            var tests = At(nvmeTest, "table");
            if (tests.ValueKind == JsonValueKind.Array && tests.GetArrayLength() > 0)
            {
                var result = Number(At(tests[0], "self_test_result", "value"));
                failed = result is >= 5 and <= 7;
                testStatus = Str(At(tests[0], "self_test_result", "string")) ?? "未知";
            }
        }
        var fields = new Dictionary<string, string>();
        if (nvme.ValueKind == JsonValueKind.Object) foreach (var p in nvme.EnumerateObject()) fields[p.Name] = p.Value.ToString();
        var temperature = Number(At(root, "temperature", "current")) ?? Number(At(nvme, "temperature"));
        var now = DateTimeOffset.UtcNow;
        return new DiskStatus { Id = id, Serial = serial.Trim(), Model = model, Kind = kind, Protocol = protocol,
            CapacityBytes = Number(At(root, "user_capacity", "bytes")) ?? Number(At(root, "nvme_total_capacity")),
            Timestamp = now, Temperature = temperature, TemperatureTimestamp = temperature.HasValue ? now : null,
            PowerOnHours = Number(At(root, "power_on_time", "hours")) ?? Number(At(nvme, "power_on_hours")),
            PowerCycles = Number(At(root, "power_cycle_count")) ?? Number(At(nvme, "power_cycles")),
            Reallocated = Raw(5), Pending = Raw(197), Uncorrectable = Raw(198), CrcErrors = Raw(199),
            OverallPassed = (exitCode & 8) != 0 ? false : Bool(At(root, "smart_status", "passed")), ReadError = (exitCode & 4) != 0,
            PercentageUsed = Number(At(nvme, "percentage_used")) ?? Number(At(root, "endurance_used", "current")),
            MediaErrors = Number(At(nvme, "media_errors")), CriticalWarning = Number(At(nvme, "critical_warning")),
            Attributes = attributes.ToArray(), Nvme = fields, SelfTest = new(testStatus, running, remaining, failed) };
    }
}

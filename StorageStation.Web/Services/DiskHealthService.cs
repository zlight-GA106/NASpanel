using StorageStation.Web.Models;
namespace StorageStation.Web.Services;

public static class DiskHealthService
{
    public static Health Evaluate(DiskStatus disk, IReadOnlyList<DiskStatus> history, StationSettings settings)
    {
        if (!disk.Online) return new("offline", ["磁盘连续三次扫描未出现"]);
        List<string> warning = [], critical = [];
        if (disk.OverallPassed == false) critical.Add("SMART 总体健康检查失败");
        if (disk.SelfTest.Failed) critical.Add("最近一次磁盘自检失败");
        if (disk.Temperature >= settings.DiskCriticalTemperature) critical.Add("硬盘温度达到严重阈值");
        else if (disk.Temperature >= settings.DiskWarningTemperature) warning.Add("硬盘温度过高");
        if (disk.Kind is "HDD" or "SSD")
        {
            if (disk.Pending > 0) critical.Add($"待处理扇区：{disk.Pending}");
            if (disk.Uncorrectable > 0) critical.Add($"不可校正扇区：{disk.Uncorrectable}");
            if (disk.Reallocated > 0)
            {
                var samples = history.Where(x => !x.ReadError && x.Reallocated.HasValue).TakeLast(2).Select(x => x.Reallocated!.Value).Append(disk.Reallocated.Value).ToArray();
                if (samples.Length == 3 && samples[0] < samples[1] && samples[1] < samples[2]) critical.Add("重新分配扇区持续增长，建议立即备份并更换磁盘");
                else warning.Add($"检测到 {disk.Reallocated} 个重新分配扇区");
            }
            if (disk.CrcErrors > 0) warning.Add(history.LastOrDefault()?.CrcErrors < disk.CrcErrors ? "CRC 错误增加，请检查 SATA 数据线 / 接口" : $"CRC 历史错误：{disk.CrcErrors}，请检查数据线 / 接口");
            if (disk.Kind == "SSD" && disk.PercentageUsed >= 90) (disk.PercentageUsed >= 100 ? critical : warning).Add("SSD 已用寿命达到告警阈值");
        }
        else if (disk.Kind == "NVMe")
        {
            if (disk.CriticalWarning > 0) critical.Add("NVMe Critical Warning 非零");
            if (disk.MediaErrors > 0) critical.Add("NVMe 存在介质 / 数据完整性错误");
            if (disk.PercentageUsed >= 90) (disk.PercentageUsed >= 100 ? critical : warning).Add("NVMe 已用寿命达到告警阈值");
        }
        if (disk.ReadError) warning.Add("SMART 部分命令失败，数据可能不完整");
        if (critical.Count > 0) return new("critical", [.. critical, .. warning]);
        if (warning.Count > 0) return new("warning", warning.ToArray());
        if (disk.OverallPassed is null) return Health.Unknown("SMART 健康状态暂不可用");
        return new("healthy", ["未检测到 SMART 异常"]);
    }
}

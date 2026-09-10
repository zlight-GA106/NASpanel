using System.Diagnostics;
using System.Text.Json;
using System.Collections.Concurrent;
using StorageStation.Web.Models;
namespace StorageStation.Web.Storage;

public sealed class SmartCtlService(IConfiguration config, IHostEnvironment env) : IDiskProvider
{
    private readonly SemaphoreSlim processSlots = new(2, 2);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> diskLocks = new();
    private async Task<(JsonDocument Json, int Exit)> Run(string[] args, CancellationToken ct)
    {
        await processSlots.WaitAsync(ct);
        try
        {
            var configured = config["SmartCtl:Path"] ?? "tools/smartmontools/smartctl.exe";
            var path = Path.IsPathRooted(configured) ? configured : Path.Combine(env.ContentRootPath, configured);
            var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = new Process { StartInfo = start };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.GetValue("SmartCtl:TimeoutSeconds", 20), 5, 60)));
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); await Task.WhenAll(stdout, stderr); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException("smartctl 执行超时");
            }
            if ((process.ExitCode & 3) != 0) throw new IOException($"smartctl 无法打开设备或参数错误（退出码 {process.ExitCode}）");
            if (stdout.Result.Length > 4 * 1024 * 1024) throw new InvalidDataException("SMART JSON 超出大小限制");
            return (JsonDocument.Parse(stdout.Result), process.ExitCode);
        }
        finally { processSlots.Release(); }
    }
    private static string[] Args(DiskDevice d, params string[] operation) => d.Type is { Length: > 0 }
        ? [.. operation, "-j", "-d", d.Type, d.Path] : [.. operation, "-j", d.Path];
    public async Task<DiskDevice[]> ScanAsync(CancellationToken ct)
    {
        var (json, code) = await Run(["--scan-open", "-j"], ct);
        using (json)
        {
            if ((code & 7) != 0) throw new IOException("smartctl 扫描未成功完成，保留原有磁盘状态");
            var devices = SmartParser.At(json.RootElement, "devices");
            if (devices.ValueKind != JsonValueKind.Array) return [];
            return devices.EnumerateArray().Select(x => new DiskDevice(SmartParser.Str(SmartParser.At(x, "name")) ?? "", SmartParser.Str(SmartParser.At(x, "type"))))
                .Where(x => !string.IsNullOrWhiteSpace(x.Path) && !x.Path.StartsWith('-')).DistinctBy(x => (x.Path, x.Type)).ToArray();
        }
    }
    public async Task<DiskStatus> ReadAsync(DiskDevice device, bool full, CancellationToken ct)
    {
        var gate = diskLocks.GetOrAdd(device.Path, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var (json, code) = await Run(full ? Args(device, "-x") : Args(device, "-i", "-A"), ct);
            using (json) return SmartParser.Parse(json.RootElement, code);
        }
        finally { gate.Release(); }
    }
    public async Task<string> SelfTestAsync(DiskDevice device, string expectedId, string kind, CancellationToken ct)
    {
        if (kind is not ("short" or "long")) throw new ArgumentException("不支持的自检类型");
        var gate = diskLocks.GetOrAdd(device.Path, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var (identity, code) = await Run(Args(device, "-x"), ct);
            using (identity)
            {
                var disk = SmartParser.Parse(identity.RootElement, code);
                if (disk.Id != expectedId) throw new InvalidOperationException("设备身份发生变化，请等待重新扫描");
                if (disk.SelfTest.Running) throw new InvalidOperationException("磁盘已有自检正在运行");
            }
            var (json, result) = await Run(Args(device, "-t", kind), ct);
            using (json)
            {
                if ((result & 7) != 0) throw new IOException("磁盘拒绝自检请求");
                return kind == "short" ? "短自检请求已接受" : "扩展自检请求已接受";
            }
        }
        finally { gate.Release(); }
    }
}

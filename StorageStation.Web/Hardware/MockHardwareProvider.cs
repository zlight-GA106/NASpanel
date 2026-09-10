using StorageStation.Web.Models;
namespace StorageStation.Web.Hardware;

public sealed class MockHardwareProvider : IHardwareProvider, IFanController
{
    private double output = 42;
    public Task<(HardwareSnapshot Snapshot, SensorInfo[] Sensors)> ReadAsync(CancellationToken ct)
    {
        var t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
        var cpu = 24 + 7 * Math.Sin(t / 9) + 2 * Math.Cos(t / 3);
        var temp = 43 + 5 * Math.Sin(t / 37);
        var used = 3.8 + 0.2 * Math.Sin(t / 51);
        var pwm = Volatile.Read(ref output);
        var rpm = 450 + pwm * 12 + 12 * Math.Sin(t / 5);
        SensorInfo[] sensors = [new("/mock/cpu/temp", "CPU Package（模拟）", "Temperature", "Intel Core i3-4170", "Cpu", temp, false),
            new("/mock/fan/rpm", "CHA_FAN（模拟）", "Fan", "模拟 Super I/O", "SuperIO", rpm, false),
            new("/mock/fan/control", "CHA_FAN PWM（模拟）", "Control", "模拟 Super I/O", "SuperIO", pwm, true)];
        return Task.FromResult<(HardwareSnapshot, SensorInfo[])>((new(DateTimeOffset.UtcNow, Environment.MachineName, "Windows Server 2022 Datacenter · 模拟环境", Environment.TickCount64 / 1000d, "Intel Core i3-4170（模拟）", cpu, temp, used / 8 * 100, used, 8, rpm, pwm, true, null), sensors));
    }
    public Task<bool> CanControlAsync(CancellationToken ct) => Task.FromResult(true);
    public Task SetPercentAsync(double percent, CancellationToken ct) { Volatile.Write(ref output, percent); return Task.CompletedTask; }
    public Task SetDefaultAsync(CancellationToken ct) { Volatile.Write(ref output, 42); return Task.CompletedTask; }
}

using StorageStation.Web.Models;
namespace StorageStation.Web.Hardware;

public interface IHardwareProvider
{
    Task<(HardwareSnapshot Snapshot, SensorInfo[] Sensors)> ReadAsync(CancellationToken ct);
}
public interface IFanController
{
    Task<bool> CanControlAsync(CancellationToken ct);
    Task SetPercentAsync(double percent, CancellationToken ct);
    Task SetDefaultAsync(CancellationToken ct);
}

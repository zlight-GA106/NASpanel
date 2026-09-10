using StorageStation.Web.Models;
namespace StorageStation.Web.Storage;
public interface IDiskProvider
{
    Task<DiskDevice[]> ScanAsync(CancellationToken ct);
    Task<DiskStatus> ReadAsync(DiskDevice device, bool full, CancellationToken ct);
    Task<string> SelfTestAsync(DiskDevice device, string expectedId, string kind, CancellationToken ct);
}

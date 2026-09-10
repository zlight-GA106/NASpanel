using StorageStation.Web.Data;
using StorageStation.Web.Models;
using System.Text.Json;

namespace StorageStation.Web.Services;

public sealed class SettingsService(StationDatabase db, IConfiguration config)
{
    private StationSettings current = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    public StationSettings Current => Volatile.Read(ref current);
    public async Task Initialize(CancellationToken ct = default)
    {
        var stored = await db.GetSetting("station", ct);
        // ConfigurationBinder appends to initialized arrays; start an explicitly configured curve empty.
        var fromFile = new StationSettings { Fan = new FanSettings { Curve = config.GetSection("StorageStation:Fan:Curve").Exists() ? [] : new FanSettings().Curve } };
        config.GetSection("StorageStation").Bind(fromFile);
        var settings = stored is null ? fromFile : JsonSerializer.Deserialize<StationSettings>(stored, StationDatabase.Json)!;
        if (settings.Validate() is { } error) throw new InvalidOperationException(error);
        Volatile.Write(ref current, settings);
    }
    public async Task Update(Func<StationSettings, StationSettings> update, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var next = update(Current);
            if (next.Validate() is { } error) throw new ArgumentException(error);
            await db.SetSetting("station", JsonSerializer.Serialize(next, StationDatabase.Json), ct);
            Volatile.Write(ref current, next);
        }
        finally { gate.Release(); }
    }
}

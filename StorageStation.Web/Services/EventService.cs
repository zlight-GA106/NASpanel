using StorageStation.Web.Data;
namespace StorageStation.Web.Services;

public sealed class EventService(StationDatabase db, ILogger<EventService> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, string> last = [];
    public async Task Transition(string key, string level, string source, string code, string message, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var state = level + message;
            if (last.GetValueOrDefault(key) == state) return;
            await db.AddEvent(level, source, code, message, ct: ct);
            last[key] = state;
            logger.LogInformation("{Level} {Source} {Code}: {Message}", level, source, code, message);
        }
        finally { gate.Release(); }
    }
}

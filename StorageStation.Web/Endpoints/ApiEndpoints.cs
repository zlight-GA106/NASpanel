using StorageStation.Web.Data;
using StorageStation.Web.Models;
using StorageStation.Web.Services;
namespace StorageStation.Web.Endpoints;

public static class ApiEndpoints
{
    public record ModeRequest(string Mode);
    public record SpeedRequest(double Percent, int Minutes);
    public record CurveRequest(CurvePoint[] Curve, string TemperatureSource);
    public record BayRequest(string? Serial);
    public record DisplayNameRequest(string DisplayName);
    private static int Hours(int? value, int fallback, int max) => Math.Clamp(value ?? fallback, 1, max);
    public static void MapStationApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/system", (StateCache cache, SettingsService settings) => cache.SystemView(settings.Current.DisplayName));
        api.MapGet("/system/history", (int? hours, StationDatabase db, CancellationToken ct) => db.SystemHistory(Hours(hours, 1, 720), ct));
        api.MapGet("/sensors", (StateCache cache) => cache.Sensors);
        api.MapGet("/disks", (DiskService service) => service.View());
        api.MapGet("/disks/{id}", (string id, StateCache cache) => cache.Disks.TryGetValue(id, out var disk) ? Results.Ok(disk) : Results.NotFound(new { error = "磁盘不存在" }));
        api.MapGet("/disks/{id}/history", (string id, int? hours, StationDatabase db, CancellationToken ct) => db.DiskHistory(id, Hours(hours, 24, 87600), ct));
        api.MapPost("/disks/{id}/selftest/{kind}", async (string id, string kind, DiskService service, CancellationToken ct) =>
        {
            if (kind is not ("short" or "long")) return Results.BadRequest(new { error = "自检仅支持 short / long" });
            return Results.Ok(new { message = await service.SelfTest(id, kind, ct) });
        });
        api.MapGet("/fans", (StateCache cache, SettingsService settings) => new { fan = cache.Fan, settings = settings.Current.Fan });
        api.MapPut("/fans/{id}/mode", async (string id, ModeRequest request, FanControlService service, CancellationToken ct) =>
        {
            if (id != "chassis") return Results.NotFound();
            await service.SetMode(request.Mode, ct); return Results.Ok(new { message = "模式已更新" });
        });
        api.MapPut("/fans/{id}/speed", async (string id, SpeedRequest request, FanControlService service, CancellationToken ct) =>
        {
            if (id != "chassis") return Results.NotFound();
            await service.SetManual(request.Percent, request.Minutes, ct); return Results.Ok(new { message = "手动设置已接受，安全规则仍然生效" });
        });
        api.MapPut("/fans/{id}/curve", async (string id, CurveRequest request, SettingsService settings, StationDatabase db, CancellationToken ct) =>
        {
            if (id != "chassis") return Results.NotFound();
            await settings.Update(s => s with { Fan = s.Fan with { Curve = request.Curve, TemperatureSource = request.TemperatureSource } }, ct);
            await db.AddEvent("info", "FAN", "CURVE", "风扇温控曲线已更新", ct: ct);
            return Results.Ok(new { message = "曲线已保存" });
        });
        api.MapGet("/events", (string? level, int? page, StationDatabase db, CancellationToken ct) => db.Events(level is "info" or "warning" or "critical" ? level : null, Math.Clamp(page ?? 1, 1, 10000), ct));
        api.MapPost("/events/{id:long}/acknowledge", async (long id, StationDatabase db, CancellationToken ct) => await db.Write("UPDATE events SET acknowledged=1 WHERE id=$id", [("$id", id)], ct) == 0 ? Results.NotFound() : Results.Ok());
        api.MapGet("/settings", (SettingsService settings) => settings.Current);
        api.MapPut("/settings/display-name", async (DisplayNameRequest request, SettingsService settings, StationDatabase db, CancellationToken ct) =>
        {
            var name = request.DisplayName?.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 80) return Results.BadRequest(new { error = "主机名称需为 1–80 字符" });
            await settings.Update(s => s with { DisplayName = name }, ct);
            await db.AddEvent("info", "SETTINGS", "DISPLAY_NAME", $"主机显示名称已修改为 {name}", ct: ct);
            return Results.Ok(new { displayName = name, message = "主机名称已保存" });
        });
        api.MapPut("/settings", async (StationSettings request, SettingsService settings, StateCache cache, FanControlService fan, StationDatabase db, CancellationToken ct) =>
        {
            if (request.Validate() is { } error) return Results.BadRequest(new { error });
            bool ValidBinding(string? id, string type) => string.IsNullOrWhiteSpace(id) || cache.Sensors.Any(x => x.Id == id && x.Type == type);
            if (!ValidBinding(request.CpuTemperatureSensorId, "Temperature") || !ValidBinding(request.FanRpmSensorId, "Fan") || !ValidBinding(request.FanControlSensorId, "Control")) return Results.BadRequest(new { error = "传感器绑定必须来自已发现的对应类型" });
            if (request.Fan.Enabled && !cache.Hardware.IsMock && (string.IsNullOrWhiteSpace(request.FanControlSensorId) || !cache.Sensors.Any(x => x.Id == request.FanControlSensorId && x.Writable))) return Results.BadRequest(new { error = "启用真实风扇控制前必须绑定可写控制器" });
            await fan.Recover(ct);
            await settings.Update(_ => request, ct);
            await db.AddEvent("info", "SETTINGS", "UPDATED", "系统设置已更新；风扇恢复 BIOS，可在风扇页启用自动模式", ct: ct);
            return Results.Ok(new { message = "设置已保存，风扇已恢复 BIOS" });
        });
        api.MapPut("/settings/bays/{bay:int}", async (int bay, BayRequest request, DiskService service, CancellationToken ct) =>
        {
            await service.MapBay(bay, string.IsNullOrWhiteSpace(request.Serial) ? null : request.Serial, ct); return Results.Ok();
        });
        api.MapPost("/settings/bays", async (DiskService service, CancellationToken ct) => Results.Ok(new { bay = await service.AddBay(ct) }));
        api.MapDelete("/settings/bays/{bay:int}", async (int bay, DiskService service, CancellationToken ct) =>
        {
            await service.DeleteBay(bay, ct); return Results.Ok();
        });
    }
}

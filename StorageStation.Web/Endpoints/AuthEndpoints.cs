using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using StorageStation.Web.Security;
using StorageStation.Web.Data;
namespace StorageStation.Web.Endpoints;

public static class AuthEndpoints
{
    public record LoginRequest(string Username, string Password);
    public record UsernameRequest(string Username);
    public record PasswordRequest(string CurrentPassword, string NewPassword);
    public record AvatarRequest(string Image);
    public static void MapAuth(this WebApplication app)
    {
        app.MapGet("/api/auth/csrf", (HttpContext ctx, IAntiforgery antiforgery) => Results.Ok(new { token = antiforgery.GetAndStoreTokens(ctx).RequestToken })).AllowAnonymous();
        app.MapPost("/api/auth/login", async (LoginRequest request, AdminService admin, StationDatabase db, HttpContext ctx, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(request.Password) || request.Password.Length > 256 || string.IsNullOrEmpty(request.Username) || request.Username.Length > 80) return Results.BadRequest(new { error = "用户名或密码格式无效" });
            var account = admin.Verify(request.Username, request.Password);
            if (account is null) return Results.Json(new { error = "用户名或密码错误；生产环境请先初始化管理员账户" }, statusCode: 401);
            await ctx.SignInAsync(AdminService.Principal(account), new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8) });
            await db.AddEvent("info", "AUTH", "LOGIN", "管理员登录成功", ct: ct);
            return Results.Ok(new { username = account.Username });
        }).AllowAnonymous().RequireRateLimiting("login");
        app.MapGet("/api/auth/me", async (AdminService admin, IHostEnvironment env, CancellationToken ct) => Results.Ok(new { username = admin.Username, avatar = await admin.Avatar(ct), isDevelopment = env.IsDevelopment() })).RequireAuthorization();
        app.MapPut("/api/auth/username", async (UsernameRequest request, AdminService admin, StationDatabase db, HttpContext ctx, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username)) return Results.BadRequest(new { error = "用户名不能为空" });
            var authentication = await ctx.AuthenticateAsync();
            var account = await admin.Rename(request.Username, ctx.User, ct);
            await ctx.SignInAsync(AdminService.Principal(account), authentication.Properties);
            await db.AddEvent("info", "AUTH", "USERNAME", $"管理员用户名已修改为 {account.Username}", ct: ct);
            return Results.Ok(new { username = account.Username, message = "用户名已保存，下次登录请使用新用户名" });
        }).RequireAuthorization();
        app.MapPut("/api/auth/password", async (PasswordRequest request, AdminService admin, StationDatabase db, HttpContext ctx, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(request.CurrentPassword) || request.CurrentPassword.Length > 256 || string.IsNullOrEmpty(request.NewPassword)) return Results.BadRequest(new { error = "请填写当前密码和新密码" });
            await admin.SetPassword(request.NewPassword, ct, request.CurrentPassword, ctx.User);
            await ctx.SignOutAsync();
            await db.AddEvent("info", "AUTH", "PASSWORD", "管理员密码已重置，旧登录会话已失效", ct: ct);
            return Results.Ok(new { message = "密码已重置，请使用新密码重新登录" });
        }).RequireAuthorization().RequireRateLimiting("password-change");
        app.MapPut("/api/auth/avatar", async (AvatarRequest request, AdminService admin, StationDatabase db, HttpContext ctx, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(request.Image)) return Results.BadRequest(new { error = "请选择头像图片" });
            await admin.SaveAvatar(request.Image, ctx.User, ct);
            await db.AddEvent("info", "AUTH", "AVATAR", "管理员头像已更新", ct: ct);
            return Results.Ok(new { avatar = request.Image, message = "头像已保存" });
        }).RequireAuthorization();
        app.MapPost("/api/auth/logout", async (HttpContext ctx) => { await ctx.SignOutAsync(); return Results.Ok(); }).RequireAuthorization();
    }
}

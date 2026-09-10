using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Serilog;
using StorageStation.Web.Data;
using StorageStation.Web.Endpoints;
using StorageStation.Web.Hardware;
using StorageStation.Web.Hubs;
using StorageStation.Web.Security;
using StorageStation.Web.Services;
using StorageStation.Web.Storage;
using StorageStation.Web.Workers;

var working = Directory.GetCurrentDirectory();
var contentRoot = File.Exists(Path.Combine(working, "StorageStation.Web.csproj")) ? working : AppContext.BaseDirectory;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args.Where(arg => arg != "--init-admin").ToArray(), ContentRootPath = contentRoot, WebRootPath = Path.Combine(contentRoot, "wwwroot") });
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true).AddEnvironmentVariables();
if (builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any()) throw new InvalidOperationException("禁止配置额外 Kestrel 端点；服务固定监听 127.0.0.1:5180");
builder.WebHost.ConfigureKestrel(options => { options.Listen(IPAddress.Loopback, 5180); options.Limits.MaxRequestBodySize = 64 * 1024; });
builder.Host.UseWindowsService(options => options.ServiceName = "StorageStationService");
builder.Host.UseSerilog((context, log) => log.MinimumLevel.Information().MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning).WriteTo.Console()
    .WriteTo.File(Path.Combine(contentRoot, "logs", "storage-station-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30, fileSizeLimitBytes: 10_000_000, rollOnFileSizeLimit: true));
var development = builder.Environment.IsDevelopment();
var requireHttps = builder.Configuration.GetValue<bool>("Security:RequireHttps");
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownProxies.Add(IPAddress.Loopback); options.ForwardLimit = 1;
});
var protection = builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(contentRoot, "Data", "keys"))).SetApplicationName("StorageStation");
if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi(protectToLocalMachine: true);
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "StorageStation.Session"; options.Cookie.HttpOnly = true; options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = requireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromHours(8); options.SlidingExpiration = false;
    options.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    options.Events.OnValidatePrincipal = async ctx =>
    {
        if (!ctx.HttpContext.RequestServices.GetRequiredService<AdminService>().IsCurrent(ctx.Principal))
        {
            ctx.RejectPrincipal(); await ctx.HttpContext.SignOutAsync();
        }
    };
});
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options => { options.HeaderName = "X-CSRF-TOKEN"; options.Cookie.SameSite = SameSiteMode.Strict; options.Cookie.SecurePolicy = requireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest; });
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions { PermitLimit = 8, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("password-change", _ => RateLimitPartition.GetFixedWindowLimiter("admin", _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = 4096);
builder.Services.AddSingleton<StationDatabase>(); builder.Services.AddSingleton<SettingsService>(); builder.Services.AddSingleton<StateCache>();
builder.Services.AddSingleton<EventService>(); builder.Services.AddSingleton<AdminService>(); builder.Services.AddSingleton<DiskService>(); builder.Services.AddSingleton<FanControlService>();
if (development)
{
    builder.Services.AddSingleton<MockHardwareProvider>();
    builder.Services.AddSingleton<IHardwareProvider>(sp => sp.GetRequiredService<MockHardwareProvider>());
    builder.Services.AddSingleton<IFanController>(sp => sp.GetRequiredService<MockHardwareProvider>());
    builder.Services.AddSingleton<IDiskProvider, MockDiskProvider>();
}
else
{
    builder.Services.AddSingleton<HardwareMonitor>();
    builder.Services.AddSingleton<IHardwareProvider>(sp => sp.GetRequiredService<HardwareMonitor>());
    builder.Services.AddSingleton<IFanController, LibreHardwareMonitorFanController>();
    builder.Services.AddSingleton<IDiskProvider, SmartCtlService>();
}
builder.Services.AddHostedService<HardwareWorker>(); builder.Services.AddHostedService<SmartWorker>(); builder.Services.AddHostedService<FanWorker>();
builder.Services.AddHostedService<HealthMonitorWorker>(); builder.Services.AddHostedService<MetricsCleanupWorker>(); builder.Services.AddHostedService<WindowsEventWorker>(); builder.Services.AddHostedService<SelfTestScheduleWorker>();
var app = builder.Build();
await app.Services.GetRequiredService<StationDatabase>().Initialize();
await app.Services.GetRequiredService<SettingsService>().Initialize();
await app.Services.GetRequiredService<AdminService>().Initialize(app.Environment);
if (args.Contains("--init-admin"))
{
    var admin = app.Services.GetRequiredService<AdminService>();
    await admin.SetPassword(AdminService.ReadPassword(admin.Username));
    Console.WriteLine($"{admin.Username} 密码哈希已保存。"); await app.DisposeAsync(); return;
}
app.UseForwardedHeaders();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
    ctx.Response.Headers["Referrer-Policy"] = "same-origin";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; connect-src 'self'; worker-src 'self' blob:; object-src 'none'; base-uri 'self'; frame-ancestors 'self'";
    if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.Headers.CacheControl = "no-store";
    else ctx.Response.Headers.CacheControl = "no-cache";
    if (requireHttps && !ctx.Request.IsHttps) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "已启用强制 HTTPS，请通过 IIS HTTPS 访问" }); return; }
    if (requireHttps && ctx.Request.IsHttps) ctx.Response.Headers["Strict-Transport-Security"] = "max-age=31536000";
    try { await next(); }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        if (ctx.Response.HasStarted) throw;
        var userError = ex is ArgumentException or NotSupportedException or InvalidOperationException;
        app.Logger.LogWarning(ex, "请求失败 {Path}", ctx.Request.Path);
        ctx.Response.StatusCode = userError ? 400 : ex is BadHttpRequestException ? 400 : 503;
        await ctx.Response.WriteAsJsonAsync(new { error = userError ? ex.Message : "操作暂不可用，请查看服务日志" });
    }
});
app.UseStaticFiles();
app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter(); app.UseWebSockets();
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/hubs") && ctx.Request.Headers.Origin.Count > 0 && ctx.Request.Headers.Origin.ToString() != $"{ctx.Request.Scheme}://{ctx.Request.Host}") { ctx.Response.StatusCode = 403; return; }
    if (ctx.Request.Method is "POST" or "PUT" or "DELETE" or "PATCH" && !ctx.Request.Path.StartsWithSegments("/hubs"))
    {
        try { await ctx.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(ctx); }
        catch (AntiforgeryValidationException) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "CSRF 校验失败，请刷新页面后重试" }); return; }
    }
    await next();
});
app.MapAuth(); app.MapStationApi(); app.MapRemote(); app.MapHub<RealtimeHub>("/hubs/realtime", options => options.CloseOnAuthenticationExpiration = true);
foreach (var (route, file) in new[] { ("/", "index.html"), ("/login", "login.html"), ("/system", "system.html"), ("/storage", "storage.html"), ("/storage/{bay:int}", "disk.html"), ("/fans", "fans.html"), ("/remote", "remote.html"), ("/events", "events.html"), ("/settings", "settings.html") })
    app.MapGet(route, async context => { context.Response.ContentType = "text/html; charset=utf-8"; await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath, file)); });
await app.RunAsync();

public partial class Program;

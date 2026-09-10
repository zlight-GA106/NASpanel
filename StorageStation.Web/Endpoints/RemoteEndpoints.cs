using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Authentication;
using StorageStation.Web.Security;
namespace StorageStation.Web.Endpoints;

public static class RemoteEndpoints
{
    public static void MapRemote(this WebApplication app)
    {
        app.MapGet("/api/remote/status", async (CancellationToken ct) =>
        {
            async Task<bool> Check(int port)
            {
                using var client = new TcpClient();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(1000);
                try { await client.ConnectAsync("127.0.0.1", port, timeout.Token); return true; }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException) { return false; }
            }
            var vnc = Check(5900); var proxy = Check(6080); await Task.WhenAll(vnc, proxy);
            return Results.Ok(new { available = vnc.Result && proxy.Result, vnc = vnc.Result, websockify = proxy.Result, path = "/novnc/websockify" });
        }).RequireAuthorization();
        app.MapGet("/novnc/websockify", async (HttpContext ctx, ILoggerFactory logs, AdminService admin) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            var origin = ctx.Request.Headers.Origin.ToString();
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed) || parsed.GetLeftPart(UriPartial.Authority) != $"{ctx.Request.Scheme}://{ctx.Request.Host}") { ctx.Response.StatusCode = 403; return; }
            using var upstream = new ClientWebSocket();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, admin.SessionRevoked(ctx.User));
            var authentication = await ctx.AuthenticateAsync();
            if (authentication.Properties?.ExpiresUtc is { } expires)
            {
                var remaining = expires - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) { ctx.Response.StatusCode = 401; return; }
                lifetime.CancelAfter(remaining);
            }
            try
            {
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); connectTimeout.CancelAfter(3000);
                await upstream.ConnectAsync(new Uri("ws://127.0.0.1:6080/"), connectTimeout.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { ctx.Response.StatusCode = 503; return; }
            using var downstream = await ctx.WebSockets.AcceptWebSocketAsync();
            static async Task Copy(WebSocket source, WebSocket destination, CancellationToken ct)
            {
                var buffer = new byte[64 * 1024];
                while (!ct.IsCancellationRequested)
                {
                    var result = await source.ReceiveAsync(buffer.AsMemory(), ct);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    await destination.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, ct);
                }
            }
            var incoming = Copy(downstream, upstream, lifetime.Token); var outgoing = Copy(upstream, downstream, lifetime.Token);
            try { await Task.WhenAny(incoming, outgoing); lifetime.Cancel(); await Task.WhenAll(incoming, outgoing); }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { logs.CreateLogger("Remote").LogDebug(ex, "远程桌面连接已结束"); }
            finally { upstream.Abort(); downstream.Abort(); }
        }).RequireAuthorization();
    }
}

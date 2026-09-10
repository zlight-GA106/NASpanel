using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using StorageStation.Web.Security;
namespace StorageStation.Web.Hubs;

[Authorize]
public sealed class RealtimeHub(AdminService admin) : Hub
{
    public override Task OnConnectedAsync()
    {
        Context.Items["session-revocation"] = admin.SessionRevoked(Context.User).Register(Context.Abort);
        return base.OnConnectedAsync();
    }
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items["session-revocation"] is CancellationTokenRegistration registration) registration.Dispose();
        return base.OnDisconnectedAsync(exception);
    }
}

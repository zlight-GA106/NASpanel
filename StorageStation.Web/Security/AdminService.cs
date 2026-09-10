using Microsoft.AspNetCore.Identity;
using StorageStation.Web.Data;
using System.Security.Claims;
using System.Text.Json;
namespace StorageStation.Web.Security;

public sealed class AdminService(StationDatabase db)
{
    public record Account(string Username, string PasswordHash, string SecurityStamp);
    private record SessionState(Account Account, CancellationToken Revoked);
    private readonly PasswordHasher<string> hasher = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private CancellationTokenSource sessions = new();
    private SessionState? current;
    public string Username => Volatile.Read(ref current)?.Account.Username ?? "Admin";
    public bool IsCurrent(ClaimsPrincipal? user) => !SessionRevoked(user).IsCancellationRequested;
    public CancellationToken SessionRevoked(ClaimsPrincipal? user)
    {
        var state = Volatile.Read(ref current);
        return state is not null && user?.FindFirstValue("security_stamp") == state.Account.SecurityStamp ? state.Revoked : new CancellationToken(true);
    }
    public Account? Verify(string username, string password)
    {
        var account = Volatile.Read(ref current)?.Account;
        return account is not null && MatchesPassword(account, password) && string.Equals(username.Trim(), account.Username, StringComparison.OrdinalIgnoreCase) ? account : null;
    }
    private bool MatchesPassword(Account account, string password) => password.Length is >= 1 and <= 256 && hasher.VerifyHashedPassword("Admin", account.PasswordHash, password) != PasswordVerificationResult.Failed;
    public static ClaimsPrincipal Principal(Account account) => new(new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, "admin"), new Claim(ClaimTypes.Name, account.Username),
        new Claim(ClaimTypes.Role, "Administrator"), new Claim("security_stamp", account.SecurityStamp)
    ], "Cookies"));
    public async Task Initialize(IHostEnvironment env)
    {
        var json = await db.GetSetting("admin-account");
        if (json is not null)
        {
            current = new(JsonSerializer.Deserialize<Account>(json, StationDatabase.Json)!, sessions.Token);
            return;
        }
        var legacyHash = await db.GetSetting("admin-password-hash");
        if (legacyHash is not null) await Save(new("Admin", legacyHash, Guid.NewGuid().ToString("N")), default);
        else if (env.IsDevelopment()) await SetPassword("StorageStation!Dev2026");
    }
    private async Task Save(Account account, CancellationToken ct)
    {
        await db.Write("""
            BEGIN IMMEDIATE;
            INSERT INTO settings(key,value) VALUES('admin-account',$account) ON CONFLICT(key) DO UPDATE SET value=excluded.value;
            DELETE FROM settings WHERE key='admin-password-hash';
            COMMIT;
            """, [("$account", JsonSerializer.Serialize(account, StationDatabase.Json))], ct);
        var oldSessions = sessions; sessions = new();
        Volatile.Write(ref current, new(account, sessions.Token));
        oldSessions.Cancel(); oldSessions.Dispose();
    }
    public async Task<Account> Rename(string username, ClaimsPrincipal user, CancellationToken ct)
    {
        username = username.Trim();
        if (username.Length is < 1 or > 40 || username.Any(c => !char.IsLetterOrDigit(c) && c is not ('_' or '-' or '.'))) throw new ArgumentException("用户名需为 1–40 个字母、汉字、数字或 . _ -");
        await gate.WaitAsync(ct);
        try
        {
            if (!IsCurrent(user)) throw new ArgumentException("登录已过期，请重新登录");
            var account = current!.Account with { Username = username, SecurityStamp = Guid.NewGuid().ToString("N") };
            await Save(account, ct); return account;
        }
        finally { gate.Release(); }
    }
    public async Task SetPassword(string password, CancellationToken ct = default, string? currentPassword = null, ClaimsPrincipal? user = null)
    {
        if (password.Length is < 12 or > 256) throw new ArgumentException("密码长度必须为 12–256 字符");
        await gate.WaitAsync(ct);
        try
        {
            if (user is not null && (!IsCurrent(user) || currentPassword is null || !MatchesPassword(current!.Account, currentPassword))) throw new ArgumentException("当前密码不正确，请重新输入");
            await Save(new(Username, hasher.HashPassword("Admin", password), Guid.NewGuid().ToString("N")), ct);
        }
        finally { gate.Release(); }
    }
    public Task<string?> Avatar(CancellationToken ct = default) => db.GetSetting("admin-avatar", ct);
    public async Task SaveAvatar(string image, ClaimsPrincipal user, CancellationToken ct)
    {
        AvatarImage.Validate(image);
        await gate.WaitAsync(ct);
        try
        {
            if (!IsCurrent(user)) throw new ArgumentException("登录已过期，请重新登录");
            await db.SetSetting("admin-avatar", image, ct);
        }
        finally { gate.Release(); }
    }
    public static string ReadPassword(string username)
    {
        Console.Write($"输入 {username} 密码（至少 12 字符）：");
        var password = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (password.Length > 0) password.Length--; }
            else if (!char.IsControl(key.KeyChar)) password.Append(key.KeyChar);
        }
        Console.WriteLine(); return password.ToString();
    }
}

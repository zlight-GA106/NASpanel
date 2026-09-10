using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using StorageStation.Web.Data;
using StorageStation.Web.Models;
using StorageStation.Web.Services;
using StorageStation.Web.Storage;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using StorageStation.Web.Security;

namespace StorageStation.Tests;

public class CriticalLogicTests
{
    [Theory]
    [InlineData(35, 35)]
    [InlineData(40, 50)]
    [InlineData(37.5, 42.5)]
    [InlineData(20, 30)]
    [InlineData(60, 100)]
    public void CurveInterpolatesAndClamps(double temperature, double percent) =>
        Assert.Equal(percent, FanEngine.Interpolate(new FanSettings().Curve, temperature));

    [Theory]
    [InlineData(80d, 35d, true)]
    [InlineData(40d, 50d, true)]
    [InlineData(null, 35d, true)]
    [InlineData(40d, null, true)]
    [InlineData(40d, 35d, false)]
    public void EmergenciesOverrideManualSpeed(double? cpu, double? disk, bool fresh)
    {
        var result = new FanEngine().Decide(new(), cpu, disk, fresh, "manual", 30, DateTimeOffset.UtcNow);
        Assert.Equal(100, result.Output); Assert.NotNull(result.SafetyReason);
    }

    [Fact]
    public void StableTemperatureNoiseDoesNotOscillateFanAndCoolingIsDelayed()
    {
        var engine = new FanEngine(); var s = new FanSettings(); var now = DateTimeOffset.UtcNow;
        Assert.Equal(100, engine.Decide(s, 40, 40, true, "auto", 30, now).Output);
        Assert.Equal(100, engine.Decide(s, 40, 40, true, "auto", 30, now.AddSeconds(29)).Output);
        double prior = 100;
        for (var t = 30; t <= 40; t++)
        {
            var current = engine.Decide(s, 40, 40, true, "auto", 30, now.AddSeconds(t)).Output;
            Assert.InRange(prior - current, 0, 10); prior = current;
        }
        Assert.Equal(50, prior);
        for (var t = 41; t <= 100; t++) Assert.Equal(50, engine.Decide(s, 40, t % 2 == 0 ? 39.9 : 40.1, true, "auto", 30, now.AddSeconds(t)).Output);
        Assert.Equal(50, engine.Decide(s, 40, 42, true, "auto", 30, now.AddSeconds(101)).Output);
        Assert.True(engine.Decide(s, 40, 42, true, "auto", 30, now.AddSeconds(104)).Output > 50);
    }

    private static DiskStatus Disk(long realloc = 0, long pending = 0, long crc = 0) => new()
    { Id = "disk", Serial = "serial", Model = "model", OverallPassed = true, Reallocated = realloc, Pending = pending, Uncorrectable = 0, CrcErrors = crc, Temperature = 35 };

    [Theory]
    [InlineData(0, 1, 0, "critical")]
    [InlineData(2, 0, 0, "warning")]
    [InlineData(0, 0, 10, "warning")]
    [InlineData(0, 0, 0, "healthy")]
    public void SmartCountersDetermineHealth(long realloc, long pending, long crc, string level) =>
        Assert.Equal(level, DiskHealthService.Evaluate(Disk(realloc, pending, crc), [Disk(realloc, pending, crc)], new()).Level);

    [Fact]
    public void SustainedReallocationGrowthIsCriticalButStableCountsAreWarning()
    {
        Assert.Equal("critical", DiskHealthService.Evaluate(Disk(20), [Disk(2), Disk(8)], new()).Level);
        Assert.Equal("warning", DiskHealthService.Evaluate(Disk(2), [Disk(2), Disk(2)], new()).Level);
    }

    [Fact]
    public void NvmeWearAndFailedTestsAreCritical()
    {
        Assert.Equal("critical", DiskHealthService.Evaluate(Disk() with { Kind = "NVMe", PercentageUsed = 100 }, [], new()).Level);
        Assert.Equal("critical", DiskHealthService.Evaluate(Disk() with { SelfTest = new("read failure", false, null, true) }, [], new()).Level);
        Assert.Equal("unknown", DiskHealthService.Evaluate(Disk() with { OverallPassed = null }, [], new()).Level);
    }

    [Fact]
    public void SmartIdentitySurvivesDeviceRenumberingAndHealthExitCodeIsNotDiscarded()
    {
        using var a = JsonDocument.Parse("""{"model_name":"WDC","serial_number":"SERIAL-123","device":{"name":"/dev/pd0"},"smart_status":{"passed":true},"temperature":{"current":34}}""");
        using var b = JsonDocument.Parse("""{"model_name":"WDC","serial_number":"SERIAL-123","device":{"name":"/dev/pd7"},"smart_status":{"passed":true}}""");
        Assert.Equal(SmartParser.Parse(a.RootElement, 0).Id, SmartParser.Parse(b.RootElement, 0).Id);
        Assert.False(SmartParser.Parse(a.RootElement, 8).OverallPassed);
        Assert.True(SmartParser.Parse(a.RootElement, 4).ReadError);
    }

    [Fact]
    public async Task EightBaysPersistAndCannotBindOneDiskTwice()
    {
        var directory = Path.Combine(Path.GetTempPath(), "StorageStationTest-" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["StorageStation:DataDirectory"] = directory }).Build();
        var db = new StationDatabase(new TestEnvironment(directory), config);
        try
        {
            await db.Initialize(); Assert.Equal(8, (await db.Bays()).Count);
            await db.MapBay(5, "SERIAL-123", default);
            await Assert.ThrowsAsync<SqliteException>(() => db.MapBay(6, "SERIAL-123", default));
            var reopened = new StationDatabase(new TestEnvironment(directory), config);
            Assert.Equal("SERIAL-123", (await reopened.Bays())[4]["serial"]);
            await reopened.MapBay(5, null, default);
            Assert.Null((await reopened.Bays())[4]["serial"]);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, recursive: true); }
    }
    private const string Avatar = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAEUlEQVR4nGPgLLr8H4QZYAwATGAJNeo52JoAAAAASUVORK5CYII=";

    [Fact]
    public async Task LegacyAccountMigratesAndCredentialChangesRevokeSessions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "StorageStationTest-" + Guid.NewGuid().ToString("N"));
        var env = new TestEnvironment(directory);
        var db = new StationDatabase(env, new ConfigurationBuilder().Build());
        try
        {
            await db.Initialize();
            await db.SetSetting("admin-password-hash", new PasswordHasher<string>().HashPassword("Admin", "Old-password-2026!"));
            var admin = new AdminService(db); await admin.Initialize(env);
            Assert.Null(await db.GetSetting("admin-password-hash"));
            var original = AdminService.Principal(admin.Verify("Admin", "Old-password-2026!")!);
            var oldSession = admin.SessionRevoked(original);
            var renamed = await admin.Rename(" 磁盘站管理员 ", original, default);
            Assert.True(oldSession.IsCancellationRequested);
            Assert.Null(admin.Verify("Admin", "Old-password-2026!"));
            Assert.NotNull(admin.Verify("磁盘站管理员", "Old-password-2026!"));
            var user = AdminService.Principal(renamed);
            await admin.SaveAvatar(Avatar, user, default);
            await Assert.ThrowsAsync<ArgumentException>(() => admin.SetPassword("New-password-2026!", default, "wrong", user));
            Assert.True(admin.IsCurrent(user));
            var session = admin.SessionRevoked(user);
            await admin.SetPassword("New-password-2026!", default, "Old-password-2026!", user);
            Assert.True(session.IsCancellationRequested);
            Assert.False(admin.IsCurrent(user));
            Assert.Null(admin.Verify("磁盘站管理员", "Old-password-2026!"));
            var reopened = new AdminService(db); await reopened.Initialize(env);
            Assert.NotNull(reopened.Verify("磁盘站管理员", "New-password-2026!"));
            Assert.Equal(Avatar, await reopened.Avatar());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void AvatarRejectsNonImagesOversizeAndExcessiveDimensions()
    {
        AvatarImage.Validate(Avatar);
        Assert.Throws<ArgumentException>(() => AvatarImage.Validate("data:image/svg+xml;base64,PHN2Zy8+"));
        Assert.Throws<ArgumentException>(() => AvatarImage.Validate("data:image/png;base64," + new string('A', 60_000)));
        Assert.Throws<ArgumentException>(() => AvatarImage.Validate("data:image/png;base64," + Convert.ToBase64String("<script>alert(1)</script>"u8.ToArray())));
        var tooWide = Convert.FromBase64String(Avatar.Split(',')[1]); tooWide[19] = 97;
        Assert.Throws<ArgumentException>(() => AvatarImage.Validate("data:image/png;base64," + Convert.ToBase64String(tooWide)));
        Assert.Throws<ArgumentException>(() => AvatarImage.Validate(Avatar[..^8]));
    }

    private sealed class TestEnvironment(string path) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "StorageStation.Tests";
        public string ContentRootPath { get; set; } = path;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

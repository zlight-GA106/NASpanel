using Microsoft.Data.Sqlite;
using System.Text.Json;
using StorageStation.Web.Models;

namespace StorageStation.Web.Data;

public sealed class StationDatabase
{
    private readonly string connectionString;
    private readonly SemaphoreSlim writer = new(1, 1);
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public StationDatabase(IHostEnvironment env, IConfiguration config)
    {
        var dir = Path.GetFullPath(config["StorageStation:DataDirectory"] ?? Path.Combine(env.ContentRootPath, "Data"));
        Directory.CreateDirectory(dir);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, env.IsDevelopment() ? "monitor.development.db" : "monitor.db"), DefaultTimeout = 5 }.ToString();
    }
    private async Task<SqliteConnection> Open(CancellationToken ct)
    {
        var db = new SqliteConnection(connectionString);
        await db.OpenAsync(ct);
        return db;
    }
    public async Task Initialize(CancellationToken ct = default)
    {
        await Write("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS system_metrics(id INTEGER PRIMARY KEY, timestamp INTEGER NOT NULL, cpu_load REAL, memory_used REAL, memory_total REAL, memory_usage REAL, cpu_temp REAL, fan_rpm REAL, fan_percent REAL, fan_mode TEXT);
            CREATE INDEX IF NOT EXISTS ix_system_time ON system_metrics(timestamp);
            CREATE TABLE IF NOT EXISTS disk_metrics(id INTEGER PRIMARY KEY, timestamp INTEGER NOT NULL, disk_id TEXT NOT NULL, temperature REAL, power_on_hours INTEGER, reallocated INTEGER, pending INTEGER, uncorrectable INTEGER, crc_errors INTEGER, health TEXT, payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_disk_time ON disk_metrics(disk_id,timestamp);
            CREATE TABLE IF NOT EXISTS events(id INTEGER PRIMARY KEY, timestamp INTEGER NOT NULL, level TEXT NOT NULL, source TEXT NOT NULL, code TEXT NOT NULL, message TEXT NOT NULL, details TEXT, acknowledged INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS ix_events_time ON events(timestamp);
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS disk_bays(bay_number INTEGER PRIMARY KEY CHECK(bay_number BETWEEN 1 AND 8), disk_serial TEXT UNIQUE, model TEXT, last_seen INTEGER);
            PRAGMA user_version=1;
            """, [], ct);
        for (var bay = 1; bay <= 8; bay++) await Write("INSERT OR IGNORE INTO disk_bays(bay_number) VALUES($bay)", [("$bay", bay)], ct);
    }
    public async Task<int> Write(string sql, (string, object?)[] args, CancellationToken ct = default)
    {
        await writer.WaitAsync(ct);
        try
        {
            await using var db = await Open(ct);
            await using var command = db.CreateCommand();
            command.CommandText = sql;
            foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
            return await command.ExecuteNonQueryAsync(ct);
        }
        finally { writer.Release(); }
    }
    public async Task<List<Dictionary<string, object?>>> Query(string sql, (string, object?)[] args, CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        List<Dictionary<string, object?>> rows = [];
        while (await reader.ReadAsync(ct))
        {
            Dictionary<string, object?> row = [];
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }
    public async Task<string?> GetSetting(string key, CancellationToken ct = default) =>
        (await Query("SELECT value FROM settings WHERE key=$key", [("$key", key)], ct)).FirstOrDefault()?["value"] as string;
    public Task SetSetting(string key, string value, CancellationToken ct = default) => Write("INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value", [("$key", key), ("$value", value)], ct);
    public Task SaveSystem(HardwareSnapshot h, FanStatus f, CancellationToken ct) => Write("""
        INSERT INTO system_metrics(timestamp,cpu_load,memory_used,memory_total,memory_usage,cpu_temp,fan_rpm,fan_percent,fan_mode)
        VALUES($t,$cpu,$used,$total,$mem,$temp,$rpm,$pwm,$mode)
        """, [("$t", h.Timestamp.ToUnixTimeSeconds()), ("$cpu", h.CpuUsage), ("$used", h.MemoryUsedGb), ("$total", h.MemoryTotalGb), ("$mem", h.MemoryUsage), ("$temp", h.CpuTemperature), ("$rpm", h.FanRpm), ("$pwm", f.Output), ("$mode", f.Mode)], ct);
    public Task SaveDisk(DiskStatus d, CancellationToken ct) => Write("""
        INSERT INTO disk_metrics(timestamp,disk_id,temperature,power_on_hours,reallocated,pending,uncorrectable,crc_errors,health,payload)
        VALUES($t,$id,$temp,$hours,$r,$p,$u,$c,$health,$json)
        """, [("$t", d.Timestamp.ToUnixTimeSeconds()), ("$id", d.Id), ("$temp", d.Temperature), ("$hours", d.PowerOnHours), ("$r", d.Reallocated), ("$p", d.Pending), ("$u", d.Uncorrectable), ("$c", d.CrcErrors), ("$health", d.Health.Level), ("$json", JsonSerializer.Serialize(d, Json))], ct);
    public async Task<DiskStatus[]> RecentDisks(string id, int count, CancellationToken ct) => (await Query("SELECT payload FROM disk_metrics WHERE disk_id=$id ORDER BY timestamp DESC,id DESC LIMIT $n", [("$id", id), ("$n", count)], ct)).Select(x => JsonSerializer.Deserialize<DiskStatus>((string)x["payload"]!, Json)!).Reverse().ToArray();
    public Task<List<Dictionary<string, object?>>> SystemHistory(int hours, CancellationToken ct)
    {
        var bucket = Math.Max(10, hours * 3600 / 720);
        return Query("""
            SELECT (timestamp / $bucket)*$bucket*1000 AS timestamp, AVG(cpu_load) AS cpuUsage,
            AVG(memory_usage) AS memoryUsage, AVG(cpu_temp) AS cpuTemperature, AVG(fan_rpm) AS fanRpm, AVG(fan_percent) AS fanPercent
            FROM system_metrics WHERE timestamp >= $since GROUP BY timestamp / $bucket ORDER BY timestamp
            """, [("$bucket", bucket), ("$since", DateTimeOffset.UtcNow.AddHours(-hours).ToUnixTimeSeconds())], ct);
    }
    public Task<List<Dictionary<string, object?>>> DiskHistory(string id, int hours, CancellationToken ct) => Query("""
        SELECT (timestamp / $bucket)*$bucket*1000 AS timestamp, AVG(temperature) AS temperature, MAX(reallocated) AS reallocated,
        MAX(pending) AS pending, MAX(uncorrectable) AS uncorrectable, MAX(crc_errors) AS crcErrors, MAX(power_on_hours) AS powerOnHours
        FROM disk_metrics WHERE disk_id=$id AND timestamp >= $since GROUP BY timestamp / $bucket ORDER BY timestamp
        """, [("$id", id), ("$bucket", Math.Max(300, hours * 3600 / 720)), ("$since", DateTimeOffset.UtcNow.AddHours(-hours).ToUnixTimeSeconds())], ct);
    public Task<List<Dictionary<string, object?>>> Bays(CancellationToken ct = default) => Query("SELECT bay_number AS bay, disk_serial AS serial FROM disk_bays ORDER BY bay_number", [], ct);
    public Task MapBay(int bay, string? serial, CancellationToken ct) => Write("UPDATE disk_bays SET disk_serial=$serial WHERE bay_number=$bay", [("$serial", serial), ("$bay", bay)], ct);
    public Task TouchBay(DiskStatus d, CancellationToken ct) => Write("UPDATE disk_bays SET model=$model,last_seen=$t WHERE disk_serial=$serial", [("$model", d.Model), ("$t", d.Timestamp.ToUnixTimeSeconds()), ("$serial", d.Serial)], ct);
    public Task AddEvent(string level, string source, string code, string message, string? details = null, CancellationToken ct = default) => Write("INSERT INTO events(timestamp,level,source,code,message,details) VALUES($t,$l,$s,$c,$m,$d)", [("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("$l", level), ("$s", source), ("$c", code), ("$m", message), ("$d", details)], ct);
    public async Task<EventRecord[]> Events(string? level, int page, CancellationToken ct) => (await Query("SELECT * FROM events WHERE ($level IS NULL OR level=$level) ORDER BY timestamp DESC,id DESC LIMIT 100 OFFSET $offset", [("$level", level), ("$offset", (page - 1) * 100)], ct)).Select(r => new EventRecord((long)r["id"]!, DateTimeOffset.FromUnixTimeSeconds((long)r["timestamp"]!), (string)r["level"]!, (string)r["source"]!, (string)r["code"]!, (string)r["message"]!, r["details"] as string, (long)r["acknowledged"]! != 0)).ToArray();
    public async Task Cleanup(StationSettings s, CancellationToken ct)
    {
        foreach (var (table, days) in new[] { ("system_metrics", s.SystemRetentionDays), ("disk_metrics", s.DiskRetentionDays), ("events", s.EventRetentionDays) })
        {
            // Fixed table names only; small transactions prevent cleanup monopolizing SQLite.
            while (await Write($"DELETE FROM {table} WHERE id IN (SELECT id FROM {table} WHERE timestamp<$t LIMIT 5000)", [("$t", DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds())], ct) == 5000)
                await Task.Delay(25, ct);
        }
        await Write("PRAGMA wal_checkpoint(PASSIVE)", [], ct);
    }
}

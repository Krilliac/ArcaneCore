using System.Data.Common;
using System.Globalization;
using System.Net;
using ArcaneCore.Data;
using ArcaneCore.Data.Resilience;
using ArcaneCore.Kernel.Configuration.Validation;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Ops.Validation;

/// <summary>
/// Startup checks for the world daemon's configuration: bad values are reported with their key,
/// the reason and the fix, all at once, instead of a raw FormatException from the first one.
/// Values are parsed from the raw strings (the options binder would throw on the first bad one).
/// Messages never contain connection strings or passwords.
/// </summary>
public sealed class WorldConfigChecks : IConfigCheck
{
    public IEnumerable<ConfigIssue> Check(IConfiguration configuration)
    {
        var issues = new List<ConfigIssue>();

        string bind = configuration["World:BindAddress"] ?? "0.0.0.0";
        if (!IPAddress.TryParse(bind, out _))
        {
            issues.Add(Error("World:BindAddress", $"'{bind}' is not an IP address.", "use an IPv4/IPv6 address such as 0.0.0.0 or 127.0.0.1 (host names are not resolved)"));
        }

        Range(issues, configuration, "World:Port", 8085, 1, 65535, "a TCP port, 1-65535 (the vanilla world port is 8085)");
        if (Int(issues, configuration, "World:TickIntervalMs", 50) is { } tick)
        {
            if (tick < 1)
            {
                issues.Add(Error("World:TickIntervalMs", $"{tick} is not a valid tick length.", "use at least 1; the vmangos world loop sleeps 50 ms (WORLD_SLEEP_CONST)"));
            }
            else if (tick > 100)
            {
                issues.Add(Warn("World:TickIntervalMs", $"{tick} ms ticks make movement and combat coarse.", "retail-like servers tick every 50 ms"));
            }
        }

        Range(issues, configuration, "World:TickLateToleranceMs", 2, 0, 1000, "0-1000 ms a tick may start late before it counts as late (default 2)");
        string? timer = configuration["World:TickTimer"];
        if (!string.IsNullOrWhiteSpace(timer) && !Enum.TryParse<ArcaneCore.Game.Maps.WorldTickTimer>(timer, ignoreCase: true, out _))
        {
            issues.Add(Error("World:TickTimer", $"'{timer}' is not a known tick timer.", "use Precise (default) or Legacy"));
        }

        Range(issues, configuration, "World:CharactersPerRealm", 10, 1, 10, "1-10 (vmangos CharactersPerRealm allows at most 10)");
        Range(issues, configuration, "World:AutosaveIntervalMs", 900000, 0, int.MaxValue, "0 to disable, or a positive number of milliseconds (vmangos PlayerSave.Interval 900000)");
        Range(issues, configuration, "World:SaveQueueWarnDepth", 1000, 0, int.MaxValue, "0 to disable, or the number of characters with a waiting snapshot at which the save queue warns (default 1000)");
        Range(issues, configuration, "World:SaveQueueWarnAgeMs", 5000, 0, int.MaxValue, "0 to disable, or the age in milliseconds of the oldest waiting snapshot at which the save queue warns (default 5000)");
        Range(issues, configuration, "World:UpdateCompressionThreshold", 128, 0, int.MaxValue, "0 to disable, or a byte count (vmangos Compression.Update.Size 128)");
        foreach (string key in new[] { "Quests:SettlementBudgetSeconds", "Economy:SettlementBudgetSeconds", "GameObjects:LootSettlementBudgetSeconds" })
        {
            Range(issues, configuration, key, 15, 1, 300, "1-300 seconds of wall time one settlement may take before it is refused (15 is the shipped value)");
        }
        foreach (string key in new[] { "SlowWorldUpdate", "SlowMapUpdate", "SlowPackets" })
        {
            Range(issues, configuration, $"PerformanceLog:{key}", 0, 0, int.MaxValue, "0 to disable, or a threshold in milliseconds");
        }

        Range(issues, configuration, "PerformanceLog:SummaryIntervalSeconds", 60, 0, 86400, "0 to log every slow update on its own line, or 1-86400 seconds between summary lines");
        const string DetailExpected = "0 for no detail lines, or the threshold in milliseconds above which an update also gets its own line";
        Range(issues, configuration, "PerformanceLog:SlowWorldUpdateDetail", 250, 0, int.MaxValue, DetailExpected);
        Range(issues, configuration, "PerformanceLog:SlowMapUpdateDetail", 200, 0, int.MaxValue, DetailExpected);
        Range(issues, configuration, "PerformanceLog:DetailLinesPerMapPerInterval", 1, 0, 16, "0 for no detail lines, or 1-16 lines per map instance per summary interval");

        string? measure = configuration["PerformanceLog:SlowWorldUpdateMeasure"];
        if (!string.IsNullOrWhiteSpace(measure) && !Enum.TryParse<ArcaneCore.Game.Maps.SlowWorldUpdateMeasure>(measure, ignoreCase: true, out _))
        {
            issues.Add(Error("PerformanceLog:SlowWorldUpdateMeasure", $"'{measure}' is not a known measure.", "use FrameInterval (retail) or TickDuration"));
        }

        foreach (string key in new[] { "World:Maps:DataDirectory", "World:Collision:VMapDirectory", "World:Collision:MMapDirectory" })
        {
            string? path = configuration[key];
            if (!string.IsNullOrWhiteSpace(path) && !Directory.Exists(path))
            {
                issues.Add(Error(key, $"directory '{path}' does not exist.", "point it at the extracted data directory, or leave it empty to run without that data"));
            }
        }

        bool publicBind = IPAddress.TryParse(bind, out IPAddress? address) && !IPAddress.IsLoopback(address);
        foreach (DatabaseComponent component in Enum.GetValues<DatabaseComponent>())
        {
            CheckDatabase(issues, configuration, component, publicBind);
        }

        return issues;
    }

    private static void CheckDatabase(List<ConfigIssue> issues, IConfiguration configuration, DatabaseComponent component, bool publicBind)
    {
        // A component without its own sub-section falls back to the Database section (DatabaseOptions.Resolve).
        string section = $"Database:{component}";
        bool own = configuration.GetSection(section).Exists();
        if (component == DatabaseComponent.Auth)
        {
            Range(issues, configuration, "Database:SqliteBusyTimeoutMs", 5000, 0, 60000, "0 to disable, or 1-60000 milliseconds (default 5000)");
            Range(issues, configuration, "Database:SqliteWriteTimeoutMs", 5000, 0, 60000, "0 to keep the connection string timeout, or 1-60000 milliseconds (default 5000)");
            Synchronous(issues, configuration, "Database:SqliteSynchronous");
            if (SqliteCheckpointInUse(configuration))
            {
                Range(issues, configuration, "Database:Sqlite:Checkpoint:IntervalMs", SqliteCheckpointOptions.DefaultIntervalMs, SqliteCheckpointOptions.MinIntervalMs, SqliteCheckpointOptions.MaxIntervalMs, "100-3600000 milliseconds (default 2000)");
                LongRange(issues, configuration, "Database:Sqlite:Checkpoint:JournalSizeLimitBytes", SqliteCheckpointOptions.DefaultJournalSizeLimitBytes, 0, SqliteCheckpointOptions.MaxBytes, "0 to 1099511627776 bytes (default 33554432)");
            }

            const string singleWriterRange = "0-3600000 milliseconds";
            Range(issues, configuration, "Database:Sqlite:SingleWriter:StatsIntervalMs", SqliteSingleWriterOptions.DefaultStatsIntervalMs, 0, SqliteSingleWriterOptions.MaxMs, singleWriterRange + " (default 300000; 0 never)");
        }

        if (own)
        {
            Range(issues, configuration, $"{section}:SqliteBusyTimeoutMs", 5000, 0, 60000, "0 to disable, or 1-60000 milliseconds (default 5000)");
            Synchronous(issues, configuration, $"{section}:SqliteSynchronous");
        }

        string prefix = own ? section : "Database";
        string connectionKey = $"{prefix}:ConnectionString";

        string providerText = configuration[$"{prefix}:Provider"] ?? nameof(DatabaseProvider.MariaDb);
        if (!Enum.TryParse(providerText, ignoreCase: true, out DatabaseProvider provider) || !Enum.IsDefined(provider))
        {
            issues.Add(Error($"{prefix}:Provider", $"'{providerText}' is not a database provider.", $"use one of {string.Join(", ", Enum.GetNames<DatabaseProvider>())}"));
            return;
        }

        string? connection = configuration[connectionKey];
        if (string.IsNullOrWhiteSpace(connection))
        {
            issues.Add(Error(connectionKey, $"the {component} database has no connection string.", "set a connection string for the chosen provider"));
            return;
        }

        var builder = new DbConnectionStringBuilder();
        try
        {
            builder.ConnectionString = connection;
        }
        catch (ArgumentException)
        {
            issues.Add(Error(connectionKey, $"the {component} connection string cannot be parsed.", "use 'key=value;key=value' pairs; quote values that contain ';'"));
            return;
        }

        bool sqliteShape = builder.ContainsKey("Data Source") || builder.ContainsKey("Filename");
        bool serverShape = builder.ContainsKey("Server") || builder.ContainsKey("Host");
        if (provider == DatabaseProvider.Sqlite && !sqliteShape)
        {
            issues.Add(Error(connectionKey, $"provider is Sqlite but the {component} connection string has no 'Data Source'.", "use 'Data Source=path/to/file.db' or change the provider"));
        }
        else if (provider != DatabaseProvider.Sqlite && !serverShape)
        {
            issues.Add(Error(connectionKey, $"provider is {provider} but the {component} connection string has no 'Server' or 'Host'.", "use 'Server=host;Port=3306;Database=...;User=...;Password=...;' or change the provider"));
        }

        if (provider != DatabaseProvider.Sqlite && serverShape)
        {
            string host = Value(builder, "Server") ?? Value(builder, "Host") ?? string.Empty;
            bool remote = !(host is "localhost" or "127.0.0.1" or "::1" or "[::1]" || host.StartsWith("127.", StringComparison.Ordinal));
            string user = Value(builder, "User") ?? Value(builder, "User Id") ?? Value(builder, "Username") ?? Value(builder, "Uid") ?? string.Empty;
            string password = Value(builder, "Password") ?? Value(builder, "Pwd") ?? string.Empty;
            if ((remote || publicBind) && user == "arcane" && password == "arcane")
            {
                issues.Add(Warn(connectionKey, $"the {component} database uses the shipped default credentials while the database host or the world listener is not loopback.", "create a dedicated database user with its own password"));
            }
        }
    }

    private static string? Value(DbConnectionStringBuilder builder, string key)
        => builder.TryGetValue(key, out object? value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;

    private static int? Int(List<ConfigIssue> issues, IConfiguration configuration, string key, int fallback)
    {
        string? text = configuration[key];
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            return value;
        }

        issues.Add(Error(key, $"'{text}' is not a whole number.", "use a whole number"));
        return null;
    }

    /// <summary>A SQLite <c>synchronous</c> key must be unset, FULL or NORMAL (any case); numeric enum values are not accepted.</summary>
    private static void Synchronous(List<ConfigIssue> issues, IConfiguration configuration, string key)
    {
        string? text = configuration[key];
        if (!string.IsNullOrWhiteSpace(text) && !text.Trim().Equals(nameof(SqliteSynchronousMode.Full), StringComparison.OrdinalIgnoreCase)
            && !text.Trim().Equals(nameof(SqliteSynchronousMode.Normal), StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(Error(key, $"'{text}' is not a SQLite synchronous mode.", "use FULL (default) or NORMAL"));
        }
    }

    private static void Range(List<ConfigIssue> issues, IConfiguration configuration, string key, int fallback, int min, int max, string expected)
    {
        if (Int(issues, configuration, key, fallback) is { } value && (value < min || value > max))
        {
            issues.Add(Error(key, $"{value} is out of range.", $"use {expected}"));
        }
    }

    /// <summary>
    /// Whether the background WAL checkpointer's settings are read at all: it is enabled and at least one database component is SQLite. A MariaDB or PostgreSQL daemon
    /// (or a disabled checkpointer) ignores them, so a bad value there must not be an error. A configuration that cannot be bound is checked (the binder error is reported elsewhere).
    /// </summary>
    private static bool SqliteCheckpointInUse(IConfiguration configuration)
    {
        try
        {
            DatabaseOptions database = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
            return database.Sqlite.Checkpoint.Enabled && Enum.GetValues<DatabaseComponent>().Any(component => database.Resolve(component).Provider == DatabaseProvider.Sqlite);
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static void LongRange(List<ConfigIssue> issues, IConfiguration configuration, string key, long fallback, long min, long max, string expected)
    {
        string? text = configuration[key];
        long value = fallback;
        if (!string.IsNullOrWhiteSpace(text) && !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            issues.Add(Error(key, $"'{text}' is not a whole number.", "use a whole number"));
            return;
        }

        if (value < min || value > max)
        {
            issues.Add(Error(key, $"{value} is out of range.", $"use {expected}"));
        }
    }

    private static ConfigIssue Error(string key, string problem, string fix) => new(ConfigSeverity.Error, key, problem, fix);

    private static ConfigIssue Warn(string key, string problem, string fix) => new(ConfigSeverity.Warning, key, problem, fix);
}

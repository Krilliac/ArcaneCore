using System.Data.Common;
using System.Globalization;
using System.Net;
using ArcaneCore.Data;
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

        Range(issues, configuration, "World:CharactersPerRealm", 10, 1, 10, "1-10 (vmangos CharactersPerRealm allows at most 10)");
        Range(issues, configuration, "World:AutosaveIntervalMs", 900000, 0, int.MaxValue, "0 to disable, or a positive number of milliseconds (vmangos PlayerSave.Interval 900000)");
        Range(issues, configuration, "World:UpdateCompressionThreshold", 128, 0, int.MaxValue, "0 to disable, or a byte count (vmangos Compression.Update.Size 128)");
        foreach (string key in new[] { "SlowWorldUpdate", "SlowMapUpdate", "SlowPackets" })
        {
            Range(issues, configuration, $"PerformanceLog:{key}", 0, 0, int.MaxValue, "0 to disable, or a threshold in milliseconds");
        }

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

    private static void Range(List<ConfigIssue> issues, IConfiguration configuration, string key, int fallback, int min, int max, string expected)
    {
        if (Int(issues, configuration, key, fallback) is { } value && (value < min || value > max))
        {
            issues.Add(Error(key, $"{value} is out of range.", $"use {expected}"));
        }
    }

    private static ConfigIssue Error(string key, string problem, string fix) => new(ConfigSeverity.Error, key, problem, fix);

    private static ConfigIssue Warn(string key, string problem, string fix) => new(ConfigSeverity.Warning, key, problem, fix);
}

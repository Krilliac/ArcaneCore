using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core.Commands;

namespace ArcaneCore.Atlas.Host;

/// <summary>
/// What <c>ctl</c> needs to reach a running <c>serve</c>: <c>{pid, port, token, started, version, terrainData, clientRoot}</c>,
/// stored as <c>&lt;state&gt;/instances/&lt;pid&gt;.json</c> for the lifetime of the process.
/// </summary>
public sealed record DiscoveryRecord(int Pid, int Port, string Token, DateTimeOffset Started, string Version, string? TerrainData, string? ClientRoot)
{
    public JsonObject ToJson() => new()
    {
        ["pid"] = Pid,
        ["port"] = Port,
        ["token"] = Token,
        ["started"] = Started.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        ["version"] = Version,
        ["terrainData"] = TerrainData,
        ["clientRoot"] = ClientRoot,
    };

    /// <summary>Parses a discovery file; null when it is not a complete record.</summary>
    public static DiscoveryRecord? Parse(JsonNode? node)
    {
        if (node is not JsonObject json
            || !JsonScalars.TryGetInt64(json["pid"], out long pid) || pid is <= 0 or > int.MaxValue
            || !JsonScalars.TryGetInt64(json["port"], out long port) || port is <= 0 or > 65535
            || json["token"] is not JsonValue tokenValue || !tokenValue.TryGetValue(out string? token) || string.IsNullOrEmpty(token)
            || json["started"] is not JsonValue startedValue || !startedValue.TryGetValue(out string? startedText)
            || !DateTimeOffset.TryParse(startedText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset started))
        {
            return null;
        }

        return new DiscoveryRecord(
            (int)pid,
            (int)port,
            token,
            started,
            json["version"] is JsonValue v && v.TryGetValue(out string? version) ? version : "unknown",
            json["terrainData"] is JsonValue t && t.TryGetValue(out string? terrain) ? terrain : null,
            json["clientRoot"] is JsonValue c && c.TryGetValue(out string? client) ? client : null);
    }
}

/// <summary>Reads, writes and prunes the discovery files of one state root.</summary>
public static class Discovery
{
    /// <summary>The file for a process id.</summary>
    public static string FileFor(AtlasState state, int pid)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Path.Combine(state.InstancesDirectory, pid.ToString(CultureInfo.InvariantCulture) + ".json");
    }

    /// <summary>Writes the record atomically; on Unix the file is readable by its owner only because it holds the bearer token.</summary>
    public static string Write(AtlasState state, DiscoveryRecord record)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(record);
        string file = FileFor(state, record.Pid);
        Directory.CreateDirectory(state.InstancesDirectory);
        string temp = $"{file}.{Guid.NewGuid():N}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temp, options))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(record.ToJson().ToJsonString());
        }

        File.Move(temp, file, overwrite: true);
        return file;
    }

    /// <summary>Deletes the record's file, but only while it still holds this record's token (another run may have replaced it).</summary>
    public static void Delete(AtlasState state, DiscoveryRecord record)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(record);
        string file = FileFor(state, record.Pid);
        try
        {
            if (File.Exists(file) && Read(file) is { } current && current.Token == record.Token)
            {
                File.Delete(file);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A file another process holds open stays; the next ctl removes it once the pid is gone.
        }
    }

    /// <summary>
    /// The running instances. A file whose process is gone (or whose pid now belongs to a process that started after the
    /// instance did) is deleted; an unreadable file is ignored but left alone.
    /// </summary>
    public static IReadOnlyList<DiscoveryRecord> Live(AtlasState state, Func<DiscoveryRecord, bool>? isAlive = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        isAlive ??= IsAlive;
        var live = new List<DiscoveryRecord>();
        if (!Directory.Exists(state.InstancesDirectory))
        {
            return live;
        }

        foreach (string file in Directory.EnumerateFiles(state.InstancesDirectory, "*.json"))
        {
            if (Read(file) is not { } record)
            {
                continue;
            }

            if (isAlive(record))
            {
                live.Add(record);
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Someone else removed it or holds it open; either way it is not live.
            }
        }

        return [.. live.OrderBy(r => r.Started)];
    }

    /// <summary>Whether the instance's process still runs: it exists, has not exited, and began before the file was written.</summary>
    public static bool IsAlive(DiscoveryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        try
        {
            using Process process = Process.GetProcessById(record.Pid);
            if (process.HasExited)
            {
                return false;
            }

            // A reused pid belongs to a process that started after the instance recorded itself (a few seconds of clock slack).
            return process.StartTime.ToUniversalTime() <= record.Started.UtcDateTime + TimeSpan.FromSeconds(5);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // The process exists but its details are not readable to this user; it cannot be shown to be gone.
            return e is not InvalidOperationException;
        }
    }

    private static DiscoveryRecord? Read(string file)
    {
        try
        {
            return DiscoveryRecord.Parse(JsonNode.Parse(File.ReadAllText(file), nodeOptions: null, HostJson.Strict));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

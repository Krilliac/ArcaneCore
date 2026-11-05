using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Server;
using ArcaneCore.Game.Maps.Grid;

namespace ArcaneCore.Atlas.Diff;

/// <summary>One question for the native probe: a map and two points (the line of sight runs from the first to the second; height and area are asked at the first).</summary>
/// <param name="Map">Map id.</param>
/// <param name="From">First point, world coordinates.</param>
/// <param name="To">Second point, world coordinates.</param>
public sealed record NativeVMapQuery(uint Map, Vector3 From, Vector3 To);

/// <summary>One answer of the native probe, field for field its output line <c>height los area flags rootId groupId</c>.</summary>
/// <param name="Height">vmangos <c>getHeight(map, x, y, z, 50)</c>; at or below -100000 when the data has no floor (vmangos' invalid-height value is -200000).</param>
/// <param name="LineOfSight">vmangos <c>isInLineOfSight</c> with doodads ignored.</param>
/// <param name="InsideWmo">vmangos <c>getAreaInfo</c> found a WMO group around the first point.</param>
/// <param name="Flags">The group's flags (0 when not inside).</param>
/// <param name="RootId">The WMO root id (0 when not inside).</param>
/// <param name="GroupId">The WMO group id (0 when not inside).</param>
public sealed record NativeVMapAnswer(float Height, bool LineOfSight, bool InsideWmo, uint Flags, int RootId, int GroupId)
{
    /// <summary>The value at or below which <see cref="Height"/> means "no floor".</summary>
    public const float NoFloorBelow = -100000f;

    /// <summary>The floor height, or null when the native data has none.</summary>
    public float? Floor => Height > NoFloorBelow ? Height : null;
}

/// <summary>
/// The native vmangos vmap oracle: <c>VMapProbe</c> (<c>tools/terrain/vmap-oracle</c>, a small program linked against vmangos' own <c>VMapManager2</c>,
/// built by <c>tools/terrain/build-terrain-data.ps1</c>) run as a child process over a batch of queries, as <c>VMapNativeOracleTests</c> does in the Game tests.
/// Its protocol (main.cpp): <c>VMapProbe &lt;vmapsDir&gt; --batch &lt;file&gt;</c>; each file line <c>map x y z x2 y2 z2</c>; each output line
/// <c>height los area flags rootId groupId</c> (<c>%.4f %d %d %u %d %d</c>).
/// </summary>
/// <remarks>
/// Process ownership: <see cref="RunAsync"/> starts the probe, owns it, kills only that process (and its children) on timeout or cancellation, and
/// removes its temporary query file and working folder. Thread safety: static, any thread; each call has its own process and files.
/// </remarks>
public static class VMapNativeProbe
{
    /// <summary>Environment variable naming the probe executable for Atlas.</summary>
    public const string ProbeVariable = "ARCANECORE_ATLAS_VMAP_PROBE";

    /// <summary>The variable the Game tests use for the same executable; used when <see cref="ProbeVariable"/> is not set.</summary>
    public const string OracleVariable = "ARCANECORE_TEST_VMAP_ORACLE";

    /// <summary>The downward search the probe hard-codes for its height query.</summary>
    public const float HeightSearch = 50f;

    /// <summary>The most queries one run may carry.</summary>
    public const int MaxQueries = 1024;

    /// <summary>How long one run may take before the probe is killed.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    /// <summary>The probe executable named by the environment, or null when neither variable is set.</summary>
    public static string? ProbePathFromEnvironment()
    {
        string? path = Environment.GetEnvironmentVariable(ProbeVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Environment.GetEnvironmentVariable(OracleVariable);
        }

        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    /// <summary>The query file text: one line per query, invariant culture, round-trip floats.</summary>
    public static string FormatQueries(IEnumerable<NativeVMapQuery> queries)
    {
        ArgumentNullException.ThrowIfNull(queries);
        var text = new StringBuilder();
        foreach (NativeVMapQuery q in queries)
        {
            text.Append(string.Create(CultureInfo.InvariantCulture, $"{q.Map} {q.From.X:R} {q.From.Y:R} {q.From.Z:R} {q.To.X:R} {q.To.Y:R} {q.To.Z:R}")).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>Parses the probe's output: exactly <paramref name="expected"/> lines of six fields.</summary>
    /// <exception cref="InvalidDataException">A line is malformed or the line count differs.</exception>
    public static IReadOnlyList<NativeVMapAnswer> ParseAnswers(string output, int expected)
    {
        ArgumentNullException.ThrowIfNull(output);
        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != expected)
        {
            throw new InvalidDataException($"the probe answered {lines.Length} lines for {expected} queries");
        }

        var answers = new List<NativeVMapAnswer>(lines.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            string[] f = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length != 6
                || !float.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float height) || !float.IsFinite(height)
                || !TryFlag(f[1], out bool los) || !TryFlag(f[2], out bool area)
                || !uint.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out uint flags)
                || !int.TryParse(f[4], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int root)
                || !int.TryParse(f[5], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int group))
            {
                throw new InvalidDataException($"probe line {i + 1} is not 'height los area flags rootId groupId': '{Clip(lines[i])}'");
            }

            answers.Add(new NativeVMapAnswer(height, los, area, flags, root, group));
        }

        return answers;

        static bool TryFlag(string text, out bool value)
        {
            value = text == "1";
            return text is "0" or "1";
        }

        static string Clip(string text) => text.Length <= 80 ? text : text[..80] + "...";
    }

    /// <summary>Runs the probe at <paramref name="probePath"/> over <paramref name="queries"/> against the vmaps in <paramref name="vmapsDirectory"/>.</summary>
    /// <exception cref="CommandException">The probe is missing, cannot start, fails, times out or answers something unreadable; the message says which.</exception>
    public static async Task<IReadOnlyList<NativeVMapAnswer>> RunAsync(
        string probePath, string vmapsDirectory, IReadOnlyList<NativeVMapQuery> queries, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(probePath);
        ArgumentException.ThrowIfNullOrEmpty(vmapsDirectory);
        ArgumentNullException.ThrowIfNull(queries);
        if (queries.Count is 0 or > MaxQueries)
        {
            throw new CommandException(CommandException.BadArguments, $"the native probe takes 1 to {MaxQueries} queries, not {queries.Count}.");
        }

        if (!File.Exists(probePath))
        {
            throw new CommandException(CommandException.Unavailable, $"the native vmap probe '{Path.GetFileName(probePath)}' does not exist; build it with tools/terrain/build-terrain-data.ps1 and point {ProbeVariable} at it.");
        }

        if (!Directory.Exists(vmapsDirectory))
        {
            throw new CommandException(CommandException.Unavailable, "the terrain data root has no 'vmaps' folder to run the native probe on.");
        }

        string work = Path.Combine(Path.GetTempPath(), "arcane-atlas-vmapprobe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string queryFile = Path.Combine(work, "queries.txt");
            await File.WriteAllTextAsync(queryFile, FormatQueries(queries), cancellationToken).ConfigureAwait(false);
            var start = new ProcessStartInfo(probePath)
            {
                WorkingDirectory = work, // the probe writes VMapProbe.log into its working folder
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(vmapsDirectory);
            start.ArgumentList.Add("--batch");
            start.ArgumentList.Add(queryFile);

            using var process = new Process { StartInfo = start };
            try
            {
                process.Start();
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                throw new CommandException(CommandException.Unavailable, $"the native vmap probe could not be started: {e.Message}", e);
            }

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            Task<string> output = process.StandardOutput.ReadToEndAsync(limit.Token);
            Task<string> error = process.StandardError.ReadToEndAsync(limit.Token);
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                await Task.WhenAll(output, error).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                throw new CommandException(CommandException.Failed, $"the native vmap probe did not finish within {timeout.TotalSeconds:0} s and was stopped.");
            }

            if (process.ExitCode != 0)
            {
                throw new CommandException(CommandException.Failed, $"the native vmap probe exited with code {process.ExitCode}: {Truncate(await error.ConfigureAwait(false))}");
            }

            try
            {
                return ParseAnswers(await output.ConfigureAwait(false), queries.Count);
            }
            catch (InvalidDataException e)
            {
                throw new CommandException(CommandException.Failed, $"the native vmap probe's answer is unreadable: {e.Message}", e);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // a probe that still holds its log open; the temp folder is left for the OS
            }
        }

        static void Kill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // it ended between the check and the kill
            }
        }

        static string Truncate(string text) => text.Length <= 400 ? text.Trim() : text[..400].Trim() + "...";
    }

    /// <summary>
    /// Compares the server's answers with the native ones. <paramref name="atFrom"/> is the probe's answer for the query as given (line of sight and area at
    /// the first point); <paramref name="atProbeZ"/> is its answer for the same point raised by <see cref="ServerEngine.FloorProbeOffset"/> (the height the
    /// server's model probe asks at). Heights are equal within <paramref name="heightTolerance"/> yards (the probe prints four decimals), or both absent.
    /// </summary>
    public static JsonObject Compare(
        NativeVMapAnswer atFrom, NativeVMapAnswer atProbeZ, bool serverLineOfSight, float? serverModelZ, IndoorSample serverIndoor, float heightTolerance = 0.01f)
    {
        ArgumentNullException.ThrowIfNull(atFrom);
        ArgumentNullException.ThrowIfNull(atProbeZ);
        ArgumentNullException.ThrowIfNull(serverIndoor);
        bool los = atFrom.LineOfSight == serverLineOfSight;
        float? nativeFloor = atProbeZ.Floor;
        bool height = nativeFloor is null
            ? serverModelZ is null
            : serverModelZ is { } z && MathF.Abs(z - nativeFloor.Value) <= heightTolerance;
        bool area = atFrom.InsideWmo == serverIndoor.InsideWmo
            && (!atFrom.InsideWmo || (atFrom.Flags == serverIndoor.MogpFlags && atFrom.RootId == serverIndoor.RootId && atFrom.GroupId == serverIndoor.GroupId));
        return new JsonObject
        {
            ["lineOfSight"] = new JsonObject { ["native"] = atFrom.LineOfSight, ["server"] = serverLineOfSight, ["match"] = los },
            ["height"] = new JsonObject { ["native"] = nativeFloor, ["server"] = serverModelZ, ["tolerance"] = heightTolerance, ["match"] = height },
            ["area"] = new JsonObject
            {
                ["native"] = new JsonObject { ["inside"] = atFrom.InsideWmo, ["flags"] = atFrom.Flags, ["rootId"] = atFrom.RootId, ["groupId"] = atFrom.GroupId },
                ["server"] = new JsonObject { ["inside"] = serverIndoor.InsideWmo, ["flags"] = serverIndoor.MogpFlags, ["rootId"] = serverIndoor.RootId, ["groupId"] = serverIndoor.GroupId },
                ["match"] = area,
            },
            ["allMatch"] = los && height && area,
        };
    }
}

/// <summary>
/// <c>diff.vmap.native</c>: the server's model queries (line of sight with doodads ignored, the model floor below a point, the WMO group around it)
/// against the same questions put to vmangos' own <c>VMapManager2</c> through <c>VMapProbe</c> (<see cref="VMapNativeProbe"/>). Needs the data root's
/// <c>vmaps</c> folder and the probe executable named by the environment; without them the command refuses with <see cref="CommandException.Unavailable"/>
/// and says what is missing.
/// </summary>
public static class VMapNativeCommands
{
    /// <summary>Name of the command.</summary>
    public const string Name = "diff.vmap.native";

    /// <summary>The command definition.</summary>
    public static IReadOnlyList<CommandDefinition> Definitions { get; } =
    [
        new CommandDefinition(
            Name,
            "Compare the server's vmap line of sight, model floor and WMO group at a point with vmangos' native VMapManager2 (VMapProbe).",
            [
                new CommandArg("map", ArgType.Integer, "Map id.", Required: true),
                new CommandArg("a", ArgType.Point3, "From [x, y, z]: the point whose floor and WMO group are compared, and the start of the ray.", Required: true),
                new CommandArg("b", ArgType.Point3, "To [x, y, z]: the end of the ray.", Required: true),
            ],
            RunAsync),
    ];

    private static async Task<JsonNode?> RunAsync(CommandInvocation inv, CancellationToken cancellationToken)
    {
        uint map = inv.GetMapId();
        Vector3 a = Checked(inv.GetPoint("a"), "a");
        Vector3 b = Checked(inv.GetPoint("b"), "b");
        AtlasPaths paths = inv.Service<AtlasPaths>();
        if (paths.VMapsDirectory is not { } vmaps)
        {
            throw new CommandException(CommandException.Unavailable, $"{Name}: no terrain data root with a 'vmaps' folder is configured.");
        }

        string probe = VMapNativeProbe.ProbePathFromEnvironment()
            ?? throw new CommandException(CommandException.Unavailable, $"{Name}: set {VMapNativeProbe.ProbeVariable} (or {VMapNativeProbe.OracleVariable}) to the VMapProbe executable built from tools/terrain/vmap-oracle.");
        ServerEngine engine = inv.Service<ServerEngine>();
        var raised = new Vector3(a.X, a.Y, a.Z + ServerEngine.FloorProbeOffset);

        IReadOnlyList<NativeVMapAnswer> native = await VMapNativeProbe.RunAsync(
            probe, vmaps, [new NativeVMapQuery(map, a, b), new NativeVMapQuery(map, raised, b)], VMapNativeProbe.DefaultTimeout, cancellationToken).ConfigureAwait(false);

        LosReport los;
        HeightReport height;
        try
        {
            los = await engine.QueryLosAsync(map, a, b, cancellationToken).ConfigureAwait(false);
            height = await engine.QueryHeightAsync(map, a, VMapNativeProbe.HeightSearch, cancellationToken).ConfigureAwait(false);
        }
        catch (ServerEngineException e)
        {
            string code = e.Kind == ServerErrorKind.BadRequest ? CommandException.BadArguments : CommandException.Unavailable;
            throw new CommandException(code, $"{Name}: {e.Message}", e);
        }

        JsonObject json = VMapNativeProbe.Compare(native[0], native[1], los.Spell.Clear, height.Models.Z, height.Indoor);
        json["map"] = map;
        json["a"] = new JsonArray(a.X, a.Y, a.Z);
        json["b"] = new JsonArray(b.X, b.Y, b.Z);
        json["modelsAvailable"] = los.ModelsAvailable && height.Models.Available;
        return json;
    }

    private static Vector3 Checked(WorldPoint p, string name)
    {
        float x = (float)p.X, y = (float)p.Y, z = (float)p.Z;
        if (!GridDefines.IsValidMapCoord(x) || !GridDefines.IsValidMapCoord(y) || !float.IsFinite(z))
        {
            throw new CommandException(CommandException.BadArguments, $"{Name}: point '{name}' is outside the map or not finite.");
        }

        return new Vector3(x, y, z);
    }
}

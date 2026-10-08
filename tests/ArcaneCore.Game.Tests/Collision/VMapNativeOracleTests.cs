using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Terrain;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// ArcaneCore's vmap reader against vmangos' own <c>VMapManager2</c> on the same real extraction:
/// thousands of random height, line-of-sight and area queries around cities, start zones and
/// dungeons, answered by both and compared value for value. Needs the data root
/// (<c>ARCANECORE_TEST_TERRAIN_DIR</c>) and the native probe built from
/// <c>tools/terrain/vmap-oracle</c> (<c>ARCANECORE_TEST_VMAP_ORACLE</c> = path of VMapProbe.exe);
/// skipped, visibly, otherwise. Recipe: docs/integration/maps-vmaps-mmaps.md.
/// </summary>
public sealed class VMapNativeOracleTests(ITestOutputHelper output)
{
    public const string OracleVariable = "ARCANECORE_TEST_VMAP_ORACLE";

    private static readonly (uint Map, float X, float Y)[] OpenWorld =
    [
        (0, -8800, 650), (0, -4900, -950), (0, 1700, 1700), (0, -9460, 40), (0, -8920, -170), (0, -14400, 450), (0, 1650, 240),
        (1, 1600, -4400), (1, -1200, 50), (1, 9900, 2300), (1, -620, -4250), (1, 10300, 830),
    ];

    // Instance entrances (map, x, y, z): Deadmines, Ragefire Chasm, Molten Core, Stockade, Scarlet Monastery, Wailing Caverns.
    private static readonly (uint Map, float X, float Y, float Z)[] Dungeons =
    [
        (36, -16.4f, -383.07f, 61.78f), (389, 3.81f, -14.82f, -17.84f), (409, 1096f, -467f, -104.6f),
        (34, 54.23f, 0.28f, -18.34f), (189, 1688.99f, 1053.48f, 18.68f), (43, -163.49f, 132.9f, -73.66f),
    ];

    [VMapOracleFact]
    public void RandomQueries_MatchVmangosVMapManager2()
    {
        string root = Environment.GetEnvironmentVariable(RealTerrainFactAttribute.Variable)!;
        string probe = Environment.GetEnvironmentVariable(OracleVariable)!;
        string vmapDirectory = Path.Combine(root, "vmaps");
        var terrain = new TerrainManager(root);
        var vmaps = new VMapManager(vmapDirectory);
        var loaded = new HashSet<(uint, int, int)>();
        var random = new Random(5875);
        var queries = new List<(uint Map, Vector3 From, Vector3 To)>();

        foreach ((uint map, float cx, float cy) in OpenWorld)
        {
            for (int n = 0; n < 300; n++)
            {
                float x = cx + (random.NextSingle() * 300) - 150, y = cy + (random.NextSingle() * 300) - 150;
                float x2 = x + (random.NextSingle() * 80) - 40, y2 = y + (random.NextSingle() * 80) - 40;
                float ground = terrain.For(map).GetHeight(x, y, 0), ground2 = terrain.For(map).GetHeight(x2, y2, 0);
                float z = ground + (random.NextSingle() * 40) - 5, z2 = ground2 + (random.NextSingle() * 10) + 1;
                if (ground > TerrainTile.InvalidHeight && ground2 > TerrainTile.InvalidHeight)
                {
                    queries.Add((map, new Vector3(x, y, z), new Vector3(x2, y2, z2)));
                }
            }
        }

        foreach ((uint map, float cx, float cy, float cz) in Dungeons)
        {
            for (int n = 0; n < 250; n++)
            {
                var from = new Vector3(cx + (random.NextSingle() * 80) - 40, cy + (random.NextSingle() * 80) - 40, cz + (random.NextSingle() * 25) - 10);
                var to = new Vector3(from.X + (random.NextSingle() * 60) - 30, from.Y + (random.NextSingle() * 60) - 30, cz + (random.NextSingle() * 20) - 10);
                queries.Add((map, from, to));
            }
        }

        var managed = new List<string>(queries.Count);
        var lines = new StringBuilder();
        foreach ((uint map, Vector3 from, Vector3 to) in queries)
        {
            Load(map, from);
            Load(map, to);
            lines.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{map} {from.X:R} {from.Y:R} {from.Z:R} {to.X:R} {to.Y:R} {to.Z:R}"));
            float? height = vmaps.GetModelHeight(map, from.X, from.Y, from.Z, 50);
            bool clear = vmaps.IsInLineOfSight(map, from, to);
            bool inside = vmaps.TryGetAreaInfo(map, from.X, from.Y, from.Z, out ModelAreaInfo area);
            managed.Add(Answer(height ?? -200000f, clear, inside, area));
        }

        string queryFile = Path.Combine(Path.GetTempPath(), $"arcanecore-vmap-oracle-{Guid.NewGuid():N}.txt");
        File.WriteAllText(queryFile, lines.ToString());
        string[] native;
        try
        {
            native = RunProbe(probe, vmapDirectory, queryFile);
        }
        finally
        {
            File.Delete(queryFile);
        }

        Assert.Equal(queries.Count, native.Length);
        int heights = 0, blocked = 0, areas = 0;
        var mismatches = new List<string>();
        for (int i = 0; i < queries.Count; i++)
        {
            string[] fields = native[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            heights += float.Parse(fields[0], CultureInfo.InvariantCulture) > -100000 ? 1 : 0;
            blocked += fields[1] == "0" ? 1 : 0;
            areas += fields[2] == "1" ? 1 : 0;
            if (!SameAnswer(managed[i], native[i]))
            {
                mismatches.Add($"map {queries[i].Map} {queries[i].From} -> {queries[i].To}: managed [{managed[i]}] native [{native[i]}]");
            }
        }

        output.WriteLine($"{queries.Count} queries: native model heights {heights}, blocked rays {blocked}, inside WMO groups {areas}; {mismatches.Count} mismatches");
        foreach (string mismatch in mismatches.Take(10))
        {
            output.WriteLine("  " + mismatch);
        }

        // The comparison has to be about something: a vmap set that failed to load answers "open" on both sides.
        Assert.True(heights > 200 && blocked > 200 && areas > 100, "too few model hits for a meaningful comparison");
        Assert.Empty(mismatches);

        void Load(uint map, Vector3 at)
        {
            if (TerrainTile.TileOf(at.X, at.Y) is not { } tile)
            {
                return;
            }

            for (int x = tile.X - 1; x <= tile.X + 1; x++)
            {
                for (int y = tile.Y - 1; y <= tile.Y + 1; y++)
                {
                    if (loaded.Add((map, x, y)))
                    {
                        vmaps.LoadTile(map, x, y);
                    }
                }
            }
        }
    }

    private static string Answer(float height, bool clear, bool inside, ModelAreaInfo area)
        => string.Create(CultureInfo.InvariantCulture, $"{height:F4} {(clear ? 1 : 0)} {(inside ? 1 : 0)} {(inside ? area.MogpFlags : 0)} {(inside ? area.RootId : 0)} {(inside ? area.GroupId : 0)}");

    private static bool SameAnswer(string managed, string native)
    {
        string[] a = managed.Split(' ');
        string[] b = native.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return a.Length == b.Length
            && MathF.Abs(float.Parse(a[0], CultureInfo.InvariantCulture) - float.Parse(b[0], CultureInfo.InvariantCulture)) <= 0.01f
            && a.Skip(1).SequenceEqual(b.Skip(1));
    }

    private static string[] RunProbe(string probe, string vmapDirectory, string queryFile)
    {
        var start = new ProcessStartInfo(probe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        };
        start.ArgumentList.Add(vmapDirectory + Path.DirectorySeparatorChar);
        start.ArgumentList.Add("--batch");
        start.ArgumentList.Add(queryFile);
        using Process process = Process.Start(start)!;
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        string stdout = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(5)), "VMapProbe did not finish");
        Assert.True(process.ExitCode == 0, $"VMapProbe exited {process.ExitCode}: {stderr.Result}");
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

/// <summary>Needs both the real terrain data and the native vmangos probe; skipped, visibly, otherwise.</summary>
public sealed class VMapOracleFactAttribute : FactAttribute
{
    public VMapOracleFactAttribute()
    {
        string? probe = Environment.GetEnvironmentVariable(VMapNativeOracleTests.OracleVariable);
        if (RealTerrain.SkipReason() is { } reason)
        {
            Skip = reason;
        }
        else if (string.IsNullOrWhiteSpace(probe) || !File.Exists(probe))
        {
            Skip = $"{VMapNativeOracleTests.OracleVariable} is not set to VMapProbe.exe (built from tools/terrain/vmap-oracle).";
        }
    }
}

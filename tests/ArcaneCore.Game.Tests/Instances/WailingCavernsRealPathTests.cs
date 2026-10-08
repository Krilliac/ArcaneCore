using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text.RegularExpressions;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// The Disciple of Naralex escort on the real classic-db z2815 <c>waypoint_path</c> 3678 (opt-in: set <c>ARCANECORE_CLASSICDB_DUMP</c> to
/// the dump, .sql or .sql.gz; the dump is GPL data and is read at test time, never committed). The 79 points are taken as they are (ids
/// and waits), shrunk to 30 % around the first point and laid flat beside the test map's entrance so the whole walk stays in the loaded
/// grid: what this proves is that the real rows drive the script, i.e. the escort starts, pauses at points 12, 30 and 70 (the rows the
/// data comments call "Spawn 1 Wave", "Spawn 2 Wave" and "End Event") and despawns after point 79.
/// </summary>
public sealed partial class WailingCavernsRealPathTests
{
    private const string Variable = "ARCANECORE_CLASSICDB_DUMP";
    private const float Scale = 0.3f, StartX = -14.4f, StartY = -383.07f, Z = 61.78f;

    [GeneratedRegex(@"\(3678,(\d+),(-?[\d.]+(?:e-?\d+)?),(-?[\d.]+(?:e-?\d+)?),(-?[\d.]+(?:e-?\d+)?),(-?[\d.]+(?:e-?\d+)?),(\d+),(\d+),")]
    private static partial Regex NaralexRow();

    private sealed class ClassicDbDumpFactAttribute : FactAttribute
    {
        public ClassicDbDumpFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            {
                Skip = $"Set {Variable} to the classic-db z2815 dump (.sql or .sql.gz) to run the escort on the real path.";
            }
        }
    }

    private static List<CreatureWaypoint> ReadNaralexPath(string path)
    {
        using FileStream file = File.OpenRead(path);
        using Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionMode.Decompress) : file;
        using var reader = new StreamReader(stream);
        var columns = new List<string>();
        bool inCreate = false;
        var points = new List<CreatureWaypoint>();
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("CREATE TABLE `waypoint_path` (", StringComparison.Ordinal))
            {
                inCreate = true;
                continue;
            }

            if (inCreate)
            {
                if (!line.TrimStart().StartsWith('`'))
                {
                    inCreate = false;
                    continue;
                }

                columns.Add(line.Trim().Split('`')[1]);
                continue;
            }

            if (!line.StartsWith("INSERT INTO `waypoint_path` VALUES ", StringComparison.Ordinal))
            {
                continue;
            }

            // The VALUES rows carry no column list: the CREATE TABLE order is the one the regex assumes.
            Assert.Equal(["PathId", "Point", "PositionX", "PositionY", "PositionZ", "Orientation", "WaitTime", "ScriptId", "Comment"], columns);
            foreach (Match m in NaralexRow().Matches(line))
            {
                float F(int group) => float.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);
                points.Add(new CreatureWaypoint(uint.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), F(2), F(3), F(4), F(5),
                    uint.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture)));
            }
        }

        return points;
    }

    [ClassicDbDumpFact]
    public void DiscipleEscort_OnTheRealWaypointPath3678_StopsAtPoints12_30_70_AndDespawnsAfterPoint79()
    {
        string dump = Environment.GetEnvironmentVariable(Variable)!;
        Assert.True(File.Exists(dump), $"{Variable} is set but '{dump}' does not exist");
        List<CreatureWaypoint> real = ReadNaralexPath(dump);
        Assert.Equal(79, real.Count);
        Assert.Equal(Enumerable.Range(1, 79).Select(i => (uint)i), real.Select(p => p.Point));
        Assert.Equal((13_000u, 1_000u, 1_000u, 1_000u), (real[0].WaitTimeMs, real[11].WaitTimeMs, real[29].WaitTimeMs, real[69].WaitTimeMs));

        CreatureWaypoint first = real[0];
        CreatureWaypoint[] path = [.. real.Select(p => p with
        {
            X = StartX + ((p.X - first.X) * Scale),
            Y = StartY + ((p.Y - first.Y) * Scale),
            Z = Z,
        })];
        using var run = new DungeonScriptTestKit(map => new WailingCavernsInstance(map),
            [3678, 3679, 3636, 5048, 5755, 5762, 5763, 3654], [3678, 3679], [],
            entryWaypoints: path.Select(p => (CreatureContent.WaypointPathEntry, CreatureContent.WaypointPathBit | DiscipleOfNaralexAi.PathId, p)));
        foreach (uint type in new uint[] { 0, 1, 2, 3 })
        {
            run.Script.SetData(type, EncounterState.Done);
        }

        Creature disciple = run.Creature(3678);
        var escort = Assert.IsType<DiscipleOfNaralexAi>(disciple.AI);
        Assert.True(escort.Start(waypointPath: DiscipleOfNaralexAi.PathId));
        Assert.Equal(79, escort.WaypointCount);

        var stops = new List<uint>();
        bool wasPaused = false;
        for (int i = 0; i < 900 && disciple.IsAlive; i++)
        {
            run.Tick(1_000);
            foreach (Creature enemy in run.Creatures.Creatures.Where(c => c.IsAlive
                && (c.Entry is 3636 or 5048 or 5755 or 3654
                    || (c.Entry == 5762 && escort.EventPhase >= 4) || (c.Entry == 5763 && escort.EventPhase >= 6))).ToArray())
            {
                run.Map.Combat.Kill(run.Player, enemy);
            }

            bool paused = escort.HasEscortState(EscortAI.EscortState.Paused);
            if (paused && !wasPaused)
            {
                var at = new Vector2(disciple.X, disciple.Y);
                stops.Add(path.MinBy(p => Vector2.Distance(at, new Vector2(p.X, p.Y)))!.Point);
            }

            wasPaused = paused;
        }

        Assert.Equal([12u, 30u, 70u], stops);
        Assert.False(disciple.IsAlive);
        Assert.Equal(EncounterState.Done, run.Script.GetData(WailingCavernsInstance.TypeDisciple));
        Assert.True(run.Creature(3679).IsAlive);
    }
}

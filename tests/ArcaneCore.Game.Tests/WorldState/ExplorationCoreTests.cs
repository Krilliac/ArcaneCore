using System.IO.Compression;
using System.Text.RegularExpressions;
using ArcaneCore.Game.WorldState.Exploration;
using Xunit;

namespace ArcaneCore.Game.Tests.WorldState;

/// <summary>A test that needs the (GPL, never committed) classic-db dump and is skipped, visibly, without it.</summary>
public sealed class ClassicDbFactAttribute : FactAttribute
{
    public static string DumpPath { get; } = Environment.GetEnvironmentVariable("ARCANE_CLASSICDB_DUMP")
        ?? @"D:\refs\classic-db\Full_DB\ClassicDB_1_12_1_z2815.sql.gz";

    public ClassicDbFactAttribute()
    {
        if (!File.Exists(DumpPath))
        {
            Skip = $"classic-db dump not found at {DumpPath} (set ARCANE_CLASSICDB_DUMP)";
        }
    }
}

internal static class ClassicDb
{
    /// <summary>The (a, b) pairs of the single-line <c>INSERT INTO `table` VALUES (a,b),...</c> statement.</summary>
    public static List<(uint A, uint B)> ReadPairs(string table)
    {
        using var file = File.OpenRead(ClassicDbFactAttribute.DumpPath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        string prefix = $"INSERT INTO `{table}` VALUES ";
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return Regex.Matches(line[prefix.Length..], @"\((\d+),(\d+)\)")
                    .Select(m => (uint.Parse(m.Groups[1].Value), uint.Parse(m.Groups[2].Value))).ToList();
            }
        }

        throw new InvalidOperationException($"no INSERT for {table}");
    }
}

/// <summary>
/// Explored-zones bit math and the exploration XP formula (vmangos Player.cpp:6089-6204,
/// ObjectMgr::GetBaseXP :8516, Server/Packets/Misc.cpp:833-837).
/// </summary>
public sealed class ExplorationCoreTests
{
    private static ExplorationBaseXpTable Linear()
        => new(Enumerable.Range(0, 70).Select(l => new KeyValuePair<uint, uint>((uint)l, (uint)l * 10)));

    [Theory]
    [InlineData(0u, 0, 0x1u)]
    [InlineData(31u, 0, 0x80000000u)]
    [InlineData(32u, 1, 0x1u)]
    [InlineData(2047u, 63, 0x80000000u)]
    public void Mark_UsesWordAndBit(uint flag, int word, uint mask)
    {
        uint[] words = new uint[ExploredZones.WordCount];
        Assert.Equal(ExploreOutcome.Discovered, ExploredZones.Mark(words, flag));
        Assert.Equal(mask, words[word]);
        Assert.Equal(1, words.Count(w => w != 0));
        Assert.True(ExploredZones.IsExplored(words, flag));
        Assert.Equal(ExploreOutcome.AlreadyExplored, ExploredZones.Mark(words, flag));
    }

    [Fact]
    public void Mark_RejectsOutOfRangeAndNoAreaWithoutThrowing()
    {
        uint[] words = new uint[ExploredZones.WordCount];
        Assert.Equal(ExploreOutcome.OutOfRange, ExploredZones.Mark(words, 2048));
        Assert.Equal(ExploreOutcome.OutOfRange, ExploredZones.Mark(words, 5000));
        Assert.Equal(ExploreOutcome.NoArea, ExploredZones.Mark(words, 0xFFFF));
        Assert.All(words, w => Assert.Equal(0u, w));
        Assert.False(ExploredZones.IsExplored(words, 2048));
    }

    [Fact]
    public void Xp_FollowsTheThreeBranches_WithIntegerDivisionBeforeTheRate()
    {
        ExplorationBaseXpTable table = Linear(); // base(level) = 10 * level
        // diff within [-5, 5]: base(area level)
        Assert.Equal(100u, ExplorationXp.Compute(10, 10, table, 1.0f));
        Assert.Equal(100u, ExplorationXp.Compute(5, 10, table, 1.0f));  // diff -5
        Assert.Equal(100u, ExplorationXp.Compute(15, 10, table, 1.0f)); // diff 5
        // diff < -5: base(player level + 5)
        Assert.Equal(100u, ExplorationXp.Compute(5, 12, table, 1.0f)); // base(10)
        // diff > 5: base(area) * percent / 100 with percent = 100 - (diff - 5) * 5
        Assert.Equal(52u, ExplorationXp.Compute(20, 8, table, 1.0f));  // diff 12 -> 65%: 80 * 65 / 100 = 52
        Assert.Equal(0u, ExplorationXp.Compute(40, 1, table, 1.0f));   // diff 39 -> clamped to 0
        // the rate multiplies the integer result as a float, truncating
        Assert.Equal(78u, ExplorationXp.Compute(20, 8, table, 1.5f));  // uint(52 * 1.5)
        Assert.Equal(200u, ExplorationXp.Compute(10, 10, table, 2.0f));
    }

    [Fact]
    public void Xp_IsZeroForNoLevelAreasMaxLevelPlayersAndMissingRows()
    {
        ExplorationBaseXpTable table = Linear();
        Assert.Equal(0u, ExplorationXp.Compute(10, 0, table, 1.0f));
        Assert.Equal(0u, ExplorationXp.Compute(60, 50, table, 1.0f));
        Assert.Equal(0u, ExplorationXp.Compute(10, 10, ExplorationBaseXpTable.Empty, 1.0f));
        Assert.Equal(0u, ExplorationXp.Compute(10, 10, table, 1.0f, maxPlayerLevel: 10));
        // diff < -5 asks for base(level + 5): a missing row is 0 (vmangos GetBaseXP)
        Assert.Equal(0u, ExplorationXp.Compute(56, 63, new ExplorationBaseXpTable([new(63, 400)]), 1.0f));
    }

    [Fact]
    public void Packet_IsAreaThenXp()
        => Assert.Equal([0x0C, 0, 0, 0, 0x2D, 0, 0, 0], ExplorationPackets.Build(12, 45));

    [ClassicDbFact]
    public void RealBaseXpTable_GivesTheDocumentedExplorationXp()
    {
        List<(uint Level, uint Xp)> rows = ClassicDb.ReadPairs("exploration_basexp");
        Assert.Equal(61, rows.Count);
        var table = new ExplorationBaseXpTable(rows.Select(r => new KeyValuePair<uint, uint>(r.Level, r.Xp)));
        Assert.Equal(660u, table.Get(60));

        Assert.Equal(5u, ExplorationXp.Compute(1, 1, table, 1.0f));
        Assert.Equal(85u, ExplorationXp.Compute(10, 10, table, 1.0f));
        Assert.Equal(85u, ExplorationXp.Compute(5, 12, table, 1.0f));    // diff -7: base(10)
        Assert.Equal(45u, ExplorationXp.Compute(20, 8, table, 1.0f));    // 70 * 65 / 100 = 45 (integer division)
        Assert.Equal(67u, ExplorationXp.Compute(20, 8, table, 1.5f));    // uint(45 * 1.5)
        Assert.Equal(170u, ExplorationXp.Compute(10, 10, table, 2.0f));
        Assert.Equal(0u, ExplorationXp.Compute(40, 1, table, 1.0f));
        Assert.Equal(0u, ExplorationXp.Compute(57, 63, table, 1.0f));    // diff -6: base(62) has no row
    }
}

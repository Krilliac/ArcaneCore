using ArcaneCore.Game.Loot;
using ArcaneCore.Kernel.WorldData.Loot;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ArcaneCore.World.Tests.GameObjects;

internal sealed class RealLootFactAttribute : FactAttribute
{
    public RealLootFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(RealContent.WorldFixtureSchema.Variable) is not { Length: > 0 } path || !File.Exists(path))
        {
            Skip = $"{RealContent.WorldFixtureSchema.Variable} (an imported SQLite world.db) is required; the real Scorpid Worker loot check did NOT run.";
        }

        Skip ??= RealContent.WorldFixtureSchema.SkipReasonForEnvironment();
    }
}

/// <summary>
/// Creature 3124 "Scorpid Worker" (quest 789 "Sting of the Scorpid") rolled from the real creature_loot_template / reference_loot_template rows of
/// the world.db fixture: the quest tail (item 4862, -80) drops on about 80 percent of kills, and a corpse with nothing at all is about 5 percent
/// while the tail is wanted and about 26 percent once it is not (the fixture's creature_loot_info for 3124 has no gold). The eligibility itself
/// (a player whose quest needs the tail) is the quest journal's, covered end to end by <see cref="QuestItemLootCountingWorldTests"/>.
/// </summary>
public sealed class ScorpidWorkerRealLootTests
{
    private const uint Worker = 3124;
    private const uint Tail = 4862;

    private static LootContent LoadWorkerLoot()
    {
        string path = Environment.GetEnvironmentVariable(RealContent.WorldFixtureSchema.Variable)!;
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(path), Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        try
        {
            var rows = new List<(LootTableKind, LootStoreRow)>();
            Read(connection, "creature_loot_template", LootTableKind.Creature, "Entry = 3124", rows);
            Read(connection, "reference_loot_template", LootTableKind.Reference, "Entry IN (60000, 60441)", rows);
            return new LootContent(rows, [new CreatureLootInfo(Worker, Worker, 0, 0, 0)]);
        }
        finally
        {
            connection.Dispose();
            SqliteConnection.ClearAllPools();
        }
    }

    private static void Read(SqliteConnection connection, string table, LootTableKind kind, string where, List<(LootTableKind, LootStoreRow)> rows)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT Entry, Item, ChanceOrQuestChance, GroupId, MinCountOrRef, MaxCount FROM {table} WHERE {where}";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((kind, new LootStoreRow((uint)reader.GetInt64(0), (uint)reader.GetInt64(1), (float)reader.GetDouble(2), (byte)reader.GetInt64(3),
                (int)reader.GetInt64(4), (uint)reader.GetInt64(5))));
        }
    }

    [RealLootFact]
    public void TheTailDropsAboutEightyPercent_AndAnEmptyCorpseIsRareWhileTheTailIsWanted()
    {
        const int Kills = 40000;
        var generator = new LootGenerator(LoadWorkerLoot(), new Random(789));
        int tails = 0;
        int emptyWithTail = 0;
        int emptyWithoutTail = 0;
        for (int i = 0; i < Kills; i++)
        {
            List<RolledLoot> rolled = generator.Roll(LootTableKind.Creature, Worker);
            bool tail = rolled.Any(r => r.ItemId == Tail && r.IsQuestItem);
            bool others = rolled.Any(r => r.ItemId != Tail);
            tails += tail ? 1 : 0;
            emptyWithTail += !tail && !others ? 1 : 0;       // wanted: the tail roll missed and nothing else dropped
            emptyWithoutTail += !others ? 1 : 0;              // not wanted: the tail is never generated
        }

        Assert.InRange(tails / (double)Kills, 0.785, 0.815);
        Assert.InRange(emptyWithTail / (double)Kills, 0.045, 0.062);
        Assert.InRange(emptyWithoutTail / (double)Kills, 0.250, 0.279);
    }
}

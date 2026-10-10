using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ArcaneCore.World.Tests.Instances;

/// <summary>Read-only contract against an imported ClassicDB world, supplied by ARCANECORE_TEST_WORLD_DB.</summary>
public sealed class Aq40ImportedContentTests
{
    [RealWorldContentFact]
    public void BossSpawnsAndWebTargetsExist_AndShadowedGuardActionHasAScriptPath()
    {
        string path = Environment.GetEnvironmentVariable(RealWorldContentFactAttribute.Variable)!;
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        db.Open();

        static long Count(SqliteConnection db, string query, uint id)
        {
            using SqliteCommand command = db.CreateCommand();
            command.CommandText = query;
            command.Parameters.AddWithValue("$id", id);
            return (long)command.ExecuteScalar()!;
        }

        static long CountInTemple(SqliteConnection db, uint entry)
        {
            using SqliteCommand command = db.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM creature_spawn WHERE Entry = $entry AND MapId = 531";
            command.Parameters.AddWithValue("$entry", entry);
            return (long)command.ExecuteScalar()!;
        }

        foreach (uint boss in new uint[] { 15509, 15510, 15516 })
        {
            Assert.Equal(1, Count(db, "SELECT COUNT(*) FROM creature_template WHERE Entry = $id", boss));
            Assert.True(CountInTemple(db, boss) > 0);
        }
        Assert.Equal(3, CountInTemple(db, 15984));
        foreach (uint add in new uint[] { 15984, 15630, 15962 })
            Assert.Equal(1, Count(db, "SELECT COUNT(*) FROM creature_template WHERE Entry = $id", add));
        foreach (uint spell in new uint[] { 720, 731, 1121, 25646, 26662, 26083, 26038, 19813, 21727 })
            Assert.Equal(1, Count(db, "SELECT COUNT(*) FROM spell_template WHERE Id = $id", spell));

        (uint Id, float X, float Y)[] webSites = [(720, -8043.6f, 1254.1f), (731, -8003f, 1222.9f), (1121, -8022.3f, 1149f)];
        foreach (var site in webSites)
        {
            using SqliteCommand command = db.CreateCommand();
            command.CommandText = "SELECT target_map, target_position_x, target_position_y FROM spell_target_position WHERE id = $id";
            command.Parameters.AddWithValue("$id", site.Id);
            using SqliteDataReader reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(531, reader.GetInt32(0));
            Assert.InRange(Math.Abs(reader.GetFloat(1) - site.X), 0f, 0.1f);
            Assert.InRange(Math.Abs(reader.GetFloat(2) - site.Y), 0f, 0.1f);
        }

        using (SqliteCommand guard = db.CreateCommand())
        {
            guard.CommandText = "SELECT AIName FROM creature_template WHERE Entry = 15984";
            Assert.Equal("EventAI", guard.ExecuteScalar());
            guard.CommandText = "SELECT EventType, Action1Type, Action1Param1, Action1Param2 FROM creature_ai_scripts WHERE Id = 1598403";
            using SqliteDataReader action = guard.ExecuteReader();
            Assert.True(action.Read());
            Assert.Equal(36, action.GetInt32(0)); // target not reachable
            Assert.Equal(11, action.GetInt32(1)); // cast
            Assert.Equal(21727, action.GetInt32(2));
            Assert.Equal(1, action.GetInt32(3)); // victim
        }
    }
}

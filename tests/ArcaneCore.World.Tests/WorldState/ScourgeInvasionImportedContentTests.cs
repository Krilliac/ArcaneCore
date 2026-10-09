using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

public sealed class ScourgeInvasionImportedContentTests
{
    [RealWorldContentFact]
    public void SixConditionFieldsAndNecropolisHealthSpawnsMatchClassicDb()
    {
        string path = Environment.GetEnvironmentVariable(RealWorldContentFactAttribute.Variable)!;
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        db.Open();
        Assert.Equal(6, ScourgeInvasionCatalog.Zones.Count);
        foreach (ScourgeInvasionZone zone in ScourgeInvasionCatalog.Zones)
        {
            using SqliteCommand query = db.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM conditions WHERE type=40 AND value1=$field AND value2=0";
            query.Parameters.AddWithValue("$field", zone.WorldStateField);
            Assert.Equal(1L, (long)query.ExecuteScalar()!);
            query.CommandText = "SELECT COUNT(*) FROM creature_spawn s JOIN game_event_creature e ON e.guid=s.Guid "
                + "WHERE s.Entry=16421 AND s.MapId=$map AND e.event=$event";
            query.Parameters.AddWithValue("$map", zone.MapId);
            query.Parameters.AddWithValue("$event", zone.EventId);
            Assert.Equal(zone.Necropolises, (long)query.ExecuteScalar()!);
            query.CommandText = "SELECT COUNT(*) FROM game_event WHERE entry=$event AND schedule_type=0";
            Assert.Equal(1L, (long)query.ExecuteScalar()!);
        }
        using SqliteCommand main = db.CreateCommand();
        main.CommandText = "SELECT COUNT(*) FROM game_event WHERE entry=17 AND schedule_type=0";
        Assert.Equal(1L, (long)main.ExecuteScalar()!);
    }
}

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

        main.CommandText = "SELECT c.Guid, COUNT(he.guid) FROM gameobject_spawn c "
            + "JOIN game_event_gameobject ce ON ce.guid=c.Guid "
            + "LEFT JOIN creature_spawn h ON h.Entry=16421 AND h.MapId=c.MapId "
            + "AND (h.X-c.X)*(h.X-c.X)+(h.Y-c.Y)*(h.Y-c.Y)+(h.Z-c.Z)*(h.Z-c.Z)<=122500 "
            + "LEFT JOIN game_event_creature he ON he.guid=h.Guid AND he.event=ce.event "
            + "WHERE c.Entry=181136 AND ce.event BETWEEN 90 AND 95 GROUP BY c.Guid";
        using SqliteDataReader circles = main.ExecuteReader();
        int circleCount = 0;
        while (circles.Read())
        {
            Assert.Equal(1L, circles.GetInt64(1));
            circleCount++;
        }
        Assert.Equal(42, circleCount);
    }
}

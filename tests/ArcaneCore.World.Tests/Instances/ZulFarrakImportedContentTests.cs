using Microsoft.Data.Sqlite;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Xunit;

namespace ArcaneCore.World.Tests.Instances;

/// <summary>Read-only content contract for the Weegli door run in ClassicDB z2815.</summary>
public sealed class ZulFarrakImportedContentTests
{
    [RealWorldContentFact]
    public void ImportedWeegliHasHisGossipSpawn_DoorChargeAndExplosionSpell()
    {
        string path = Environment.GetEnvironmentVariable(RealWorldContentFactAttribute.Variable)!;
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        db.Open();

        Assert.Equal(1L, Count(db, "SELECT COUNT(*) FROM creature_template WHERE Entry=7607 AND (NpcFlags & 1) != 0"));
        Assert.Equal(1L, Count(db, "SELECT COUNT(*) FROM creature_spawn WHERE Entry=7607 AND MapId=209"));
        Assert.Equal(1L, Count(db, "SELECT COUNT(*) FROM gameobject_template WHERE Entry=146084 AND Type=0"));
        Assert.Equal(1L, Count(db, "SELECT COUNT(*) FROM gameobject_spawn WHERE Entry=146084 AND MapId=209"));
        Assert.Equal(1L, Count(db, "SELECT COUNT(*) FROM gameobject_template WHERE Entry=144065 AND Type=10"));
        Assert.Equal(1L, Count(db, "SELECT COUNT(*) FROM spell_template WHERE Id=13259"));

        (float wx, float wy, float wz) = Position(db, "SELECT X,Y,Z FROM creature_spawn WHERE Entry=7607 AND MapId=209");
        (float dx, float dy, float dz) = Position(db, "SELECT X,Y,Z FROM gameobject_spawn WHERE Entry=146084 AND MapId=209");
        Assert.InRange(MathF.Sqrt((wx - 1858.57f) * (wx - 1858.57f) + (wy - 1146.35f) * (wy - 1146.35f)
            + (wz - 14.745f) * (wz - 14.745f)), 0f, 180f);
        Assert.InRange(MathF.Sqrt((dx - 1856.3142f) * (dx - 1856.3142f) + (dy - 1144.9905f) * (dy - 1144.9905f)
            + (dz - 15.4863f) * (dz - 15.4863f)), 0f, 5f);
    }

    private static long Count(SqliteConnection db, string sql)
    {
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static (float X, float Y, float Z) Position(SqliteConnection db, string sql)
    {
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader row = command.ExecuteReader();
        Assert.True(row.Read());
        return (Convert.ToSingle(row.GetValue(0)), Convert.ToSingle(row.GetValue(1)), Convert.ToSingle(row.GetValue(2)));
    }
}

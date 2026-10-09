using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

public sealed class WarEffortImportedContentTests
{
    [RealWorldContentFact]
    public void ImportedClassicDbConditionsAndTurnInsMatchTheWarEffortCatalog()
    {
        string path = Environment.GetEnvironmentVariable(RealWorldContentFactAttribute.Variable)!;
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        db.Open();

        Assert.Equal(WarEffortCatalog.ResourceCount, WarEffortCatalog.Resources.Count);
        foreach (WarEffortResource resource in WarEffortCatalog.Resources)
        {
            using SqliteCommand condition = db.CreateCommand();
            condition.CommandText = "SELECT COUNT(*) FROM conditions WHERE type=40 AND value1=$field AND value2=0";
            condition.Parameters.AddWithValue("$field", resource.WorldStateField);
            Assert.Equal(1L, (long)condition.ExecuteScalar()!);

            foreach (uint questId in new[] { resource.FirstQuest, resource.RepeatQuest })
            {
                using SqliteCommand quest = db.CreateCommand();
                quest.CommandText = "SELECT ReqItemId1,ReqItemCount1 FROM quest_template WHERE entry=$quest";
                quest.Parameters.AddWithValue("$quest", questId);
                using SqliteDataReader row = quest.ExecuteReader();
                Assert.True(row.Read(), $"missing war-effort quest {questId}");
                Assert.True(row.GetInt64(0) > 0 && row.GetInt64(1) > 0, $"quest {questId} must turn in an item");
            }
        }

        using SqliteCommand days = db.CreateCommand();
        days.CommandText = "SELECT COUNT(*) FROM conditions WHERE type=40 AND value1=$field AND value2=0";
        days.Parameters.AddWithValue("$field", WarEffortCatalog.DaysLeftCondition);
        Assert.Equal(1L, (long)days.ExecuteScalar()!);
    }
}

using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Rules;
using ArcaneCore.Kernel.Rules;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The rule engine tables (<see cref="RuleDataModule"/>, <see cref="PlayerbotGoalDataModule"/>; design section 4): the schema steps create
/// them, and <see cref="EfRuleContentStore"/> reads every table back in key order with every column.
/// </summary>
public sealed class RuleDataTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    [Fact]
    public void WorldSteps_AreTheRuleTables_ThenTheGoalTable()
    {
        SchemaStep rules = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == RuleDataModule.Version);
        Assert.Equal(
            ["rule", "rule_action", "rule_list", "rule_list_action", "rule_subject_filter", "rule_text"],
            rules.Changes.OfType<CreateTableChange>().Select(c => c.Table));
        SchemaStep goals = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == PlayerbotGoalDataModule.Version);
        Assert.Equal(["playerbot_goal"], goals.Changes.OfType<CreateTableChange>().Select(c => c.Table));
        Assert.Equal(RuleDataModule.Version + 1, PlayerbotGoalDataModule.Version);
        Assert.Contains(DataModules.For(DatabaseComponent.World), m => m is RuleDataModule);
        Assert.Contains(DataModules.For(DatabaseComponent.World), m => m is PlayerbotGoalDataModule);
    }

    [Fact]
    public async Task Store_ReadsEveryTable_InKeyOrder()
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.AddRange(
                new RuleRow { Id = 2, Host = 2, OwnerKind = 1, OwnerId = 448, EventType = 3, EventParam1 = 30, EventParam4 = -4, PhaseMask = 6, FilterId = 1, ChancePct = 40, ConditionId = 12, CooldownMinMs = 1000, CooldownMaxMs = 2000, Priority = -1, Flags = 0x11, Enabled = 0, Comment = "hogger" },
                new RuleRow { Id = 1, Host = 1, EventType = 1 },
                new RuleActionRow { RuleId = 2, Seq = 1, ActionType = 4, Param1 = 133, Param6 = 6, TargetType = 2, TargetParam3 = 9, DelayMs = 500 },
                new RuleActionRow { RuleId = 2, Seq = 0, ActionType = 3, Param1 = 1, TargetType = 1 },
                new RuleActionRow { RuleId = 1, Seq = 0, ActionType = 1, TargetType = 1 },
                new RuleListRow { Id = 7, Comment = "list" },
                new RuleListActionRow { ListId = 7, Seq = 1, ActionType = 1, Param1 = 2, TargetType = 1, DelayMs = 100 },
                new RuleListActionRow { ListId = 7, Seq = 0, ActionType = 1, Param1 = 1, TargetType = 1 },
                new RuleSubjectFilterRow { Id = 1, ClassMask = 3, RaceMask = 5, LevelMin = 10, LevelMax = 20, Team = 1, MapId = 1, ZoneId = 14, AreaId = 15, SubjectFlags = 2, SharePct = 30, ShareSeed = 9, Comment = "f" },
                new RuleTextRow { Id = 1, ContentDefault = "Grr", Sound = 5, Type = 1, Language = 7, Emote = 3, BroadcastTextId = 11, Comment = "t" },
                new PlayerbotGoalRow { GoalId = 2, Name = "grind", BasePriority = 10, Driver = 1, DriverParam1 = 1, DriverParam4 = 4, Comment = "g" },
                new PlayerbotGoalRow { GoalId = 1, Name = "quest", Enabled = 0 });
            await db.SaveChangesAsync();
        }

        await using WorldDbContext read = TestContexts.Create<WorldDbContext>(cs);
        RuleContent content = await new EfRuleContentStore(read).LoadAsync();

        Assert.Equal([1, 2], content.Rules.Select(r => r.Id));
        RuleRecord hogger = content.Rules[1];
        Assert.Equal(((byte)2, (byte)1, 448, (short)3, 30, -4, 6, 1), (hogger.Host, hogger.OwnerKind, hogger.OwnerId, hogger.EventType, hogger.EventParam1, hogger.EventParam4, hogger.PhaseMask, hogger.FilterId));
        Assert.Equal(((byte)40, 12, 1000, 2000, (short)-1, 0x11, false, "hogger"), (hogger.ChancePct, hogger.ConditionId, hogger.CooldownMinMs, hogger.CooldownMaxMs, hogger.Priority, hogger.Flags, hogger.Enabled, hogger.Comment));
        Assert.True(content.Rules[0].Enabled);
        Assert.Equal([(1, 0), (2, 0), (2, 1)], content.Actions.Select(a => (a.OwnerId, (int)a.Seq)));
        RuleActionRecord cast = content.Actions[2];
        Assert.Equal(((short)4, 133, 6, (short)2, 9, 500), (cast.ActionType, cast.Param1, cast.Param6, cast.TargetType, cast.TargetParam3, cast.DelayMs));
        Assert.Equal("list", Assert.Single(content.Lists).Comment);
        Assert.Equal([(7, 0, 1), (7, 1, 2)], content.ListActions.Select(a => (a.OwnerId, (int)a.Seq, a.Param1)));
        Assert.Equal(100, content.ListActions[1].DelayMs);
        RuleSubjectFilterRecord filter = Assert.Single(content.Filters);
        Assert.Equal((3, 5, (byte)10, (byte)20, (byte)1, 1, 14, 15, 2, (byte)30, 9, "f"), (filter.ClassMask, filter.RaceMask, filter.LevelMin, filter.LevelMax, filter.Team, filter.MapId, filter.ZoneId, filter.AreaId, filter.SubjectFlags, filter.SharePct, filter.ShareSeed, filter.Comment));
        RuleTextRecord text = Assert.Single(content.Texts);
        Assert.Equal(("Grr", 5, (byte)1, (byte)7, 3, 11, "t"), (text.ContentDefault, text.Sound, text.Type, text.Language, text.Emote, text.BroadcastTextId, text.Comment));
        Assert.Equal([(short)1, (short)2], content.Goals.Select(g => g.GoalId));
        Assert.False(content.Goals[0].Enabled);
        Assert.Equal(("grind", (short)10, true, (short)1, 1, 4, "g"), (content.Goals[1].Name, content.Goals[1].BasePriority, content.Goals[1].Enabled, content.Goals[1].Driver, content.Goals[1].DriverParam1, content.Goals[1].DriverParam4, content.Goals[1].Comment));
    }
}

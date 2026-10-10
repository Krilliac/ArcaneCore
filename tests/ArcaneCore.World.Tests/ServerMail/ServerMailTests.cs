using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.ServerMail;
using ArcaneCore.World.Economy;
using ArcaneCore.World.ServerMail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.ServerMail;

public sealed class ServerMailTests
{
    private sealed class Check : IServerMailValidation
    {
        public uint MaxLevel => 60;
        public bool CreatureExists(uint entry) => entry == 100;
        public (uint Stackable, uint MaxCount)? Item(uint entry) => entry switch { 6948 => (1u, 1u), 2589 => (20u, 0u), _ => null };
        public bool QuestExists(uint questId) => questId == 783;
        public bool FactionExists(uint factionId) => factionId == 72;
    }

    private sealed class Facts : IServerMailFacts
    {
        public uint Level { get; set; } = 10;
        public uint PlayedSeconds { get; set; } = 3600;
        public Team Team { get; set; } = Team.Alliance;
        public Race Race { get; set; } = Race.Human;
        public Class Class { get; set; } = Class.Warrior;
        public (QuestStatus Status, bool Rewarded)? QuestState { get; set; } = (QuestStatus.None, false);
        public ReputationRank? RankState { get; set; } = ReputationRank.Friendly;
        public (QuestStatus Status, bool Rewarded)? Quest(uint questId) => QuestState;
        public ReputationRank? Rank(uint factionId) => RankState;
    }

    private static IReadOnlyList<ServerMailTemplate> Load(ServerMailContent content, List<string>? errors = null)
        => ServerMailRules.Load(content, new Check(), e => errors?.Add(e));

    [Fact]
    public void Loading_skips_inactive_and_bad_rows_and_splits_items_by_faction()
    {
        var errors = new List<string>();
        var content = new ServerMailContent(
            [
                new(1, 100, 500, 700, "Welcome", "Hello", true),
                new(2, 999, 0, 0, "Bad sender", "", true),
                new(3, 0, 0, 0, "Off", "", false),
                new(4, 0, uint.MaxValue, 0, "Too rich", "", true),
            ],
            [
                new(1, "Alliance", 6948, 1), new(1, "Horde", 2589, 5), new(1, "Horde", 2589, 21), new(1, "Pirates", 2589, 1),
                new(1, "Alliance", 1, 1), new(3, "Alliance", 6948, 1),
            ],
            [
                new(1, "Level", 10, 0), new(1, "Achievement", 1, 0), new(1, "Level", 61, 0), new(1, "Quest", 783, 2),
                new(1, "Reputation", 72, 8), new(1, "Race", 0x400, 0),
            ]);
        IReadOnlyList<ServerMailTemplate> templates = Load(content, errors);
        Assert.Equal([1u, 2u], templates.Select(t => t.Id).Order());
        ServerMailTemplate welcome = templates.Single(t => t.Id == 1);
        Assert.Equal([new ServerMailItem(6948, 1)], welcome.ItemsAlliance);
        Assert.Equal([new ServerMailItem(2589, 5)], welcome.ItemsHorde);
        Assert.Equal([new ServerMailCondition(ServerMailConditionType.Level, 10, 0)], welcome.Conditions);
        Assert.Equal(0u, templates.Single(t => t.Id == 2).SenderEntry); // falls back to the default sender
        Assert.Equal(11, errors.Count);
    }

    [Fact]
    public void Conditions_follow_azerothcore_check_condition()
    {
        var facts = new Facts();
        Assert.True(ServerMailRules.Check(new(ServerMailConditionType.Level, 10, 0), facts));
        Assert.False(ServerMailRules.Check(new(ServerMailConditionType.Level, 11, 0), facts));
        Assert.True(ServerMailRules.Check(new(ServerMailConditionType.PlayTime, 3600, 0), facts));
        Assert.True(ServerMailRules.Check(new(ServerMailConditionType.Faction, 0, 0), facts));
        Assert.False(ServerMailRules.Check(new(ServerMailConditionType.Faction, 1, 0), facts));
        Assert.True(ServerMailRules.Check(new(ServerMailConditionType.Race, 1, 0), facts));
        Assert.False(ServerMailRules.Check(new(ServerMailConditionType.Class, 1u << 7, 0), facts));
        Assert.True(ServerMailRules.Check(new(ServerMailConditionType.Reputation, 72, (uint)ReputationRank.Neutral), facts));
        Assert.False(ServerMailRules.Check(new(ServerMailConditionType.Reputation, 72, (uint)ReputationRank.Honored), facts));

        Assert.True(ServerMailRules.Check(new(ServerMailConditionType.Quest, 783, 0), facts));
        facts.QuestState = (QuestStatus.Complete, true);
        Assert.True(ServerMailRules.Check(new(ServerMailConditionType.Quest, 783, ServerMailRules.QuestStateRewarded), facts));
        Assert.False(ServerMailRules.Check(new(ServerMailConditionType.Quest, 783, 1), facts));
        facts.QuestState = null; // quest log not loaded: owed for later
        Assert.False(ServerMailRules.Check(new(ServerMailConditionType.Quest, 783, 0), facts));
    }

    [Fact]
    public void Owed_letters_skip_sent_and_unmet_templates_and_use_the_team_reward()
    {
        IReadOnlyList<ServerMailTemplate> templates = Load(new ServerMailContent(
            [new(1, 0, 500, 700, "A", "", true), new(2, 0, 0, 0, "B", "", true), new(3, 0, 0, 0, "C", "", true)],
            [new(1, "Horde", 2589, 5)],
            [new(3, "Level", 20, 0)]));
        var facts = new Facts { Team = Team.Horde, Race = Race.Orc };
        Assert.Equal([1u], ServerMailRules.Owed(templates, [2u], facts).Select(t => t.Id));
        ServerMailRequest letter = ServerMailRules.Letter(templates.Single(t => t.Id == 1), 42, Team.Horde);
        Assert.Equal((42, 700u), (letter.ReceiverId, letter.Money));
        Assert.Equal([new ServerMailItem(2589, 5)], letter.Items);
        Assert.Empty(ServerMailRules.Letter(templates.Single(t => t.Id == 1), 42, Team.Alliance).Items);
    }

    private static AutoBroadcastFeature Broadcast(params (string Key, string? Value)[] settings)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))).Build();
        return new AutoBroadcastFeature(new ServiceCollection().AddSingleton(configuration).BuildServiceProvider());
    }

    [Fact]
    public void Auto_broadcast_is_off_by_default_and_binds_its_section()
    {
        AutoBroadcastFeature off = Broadcast();
        Assert.False(off.Options.Enabled);
        Assert.Equal(1_800_000u, off.Options.IntervalMs);
        Assert.Null(off.Update(10_000_000));

        AutoBroadcastFeature on = Broadcast(("AutoBroadcast:Enabled", "true"), ("AutoBroadcast:IntervalMs", "1000"),
            ("AutoBroadcast:Messages:0", "Tip one"), ("AutoBroadcast:Messages:1", " "));
        Assert.True(on.Options.Enabled);
        Assert.Equal(["Tip one"], on.Options.Messages);
    }

    [Fact]
    public void Auto_broadcast_sends_one_message_per_interval()
    {
        AutoBroadcastFeature feature = Broadcast(("AutoBroadcast:Enabled", "true"), ("AutoBroadcast:IntervalMs", "1000"),
            ("AutoBroadcast:Messages:0", "Tip one"));
        Assert.Null(feature.Update(600));
        Assert.Equal("Tip one", feature.Update(400));
        Assert.Null(feature.Update(999));
        Assert.Equal("Tip one", feature.Update(1));
    }

    [Fact]
    public void Server_mail_is_on_by_default_and_can_be_switched_off()
    {
        Assert.True(ServerMailFeature.Bind(null).Enabled);
        IConfiguration off = new ConfigurationBuilder().AddInMemoryCollection([new("ServerMail:Enabled", "false")]).Build();
        Assert.False(ServerMailFeature.Bind(off).Enabled);
    }
}

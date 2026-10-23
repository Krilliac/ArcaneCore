using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Groups;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;
using ArcaneCore.World.Tests.Playerbots.Groups;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// Bot-led groups record who grouped with whom, end to end on the group scenario world (real handlers, the manual clock): the coordinator forms
/// the group through client packets, the group kills the elite and disbands, and each bot's social memory shows it.
/// </summary>
public sealed class PlayerbotSocialGroupWorldTests
{
    private const byte Human = 1;
    private const byte Warrior = 1, Priest = 5, Mage = 8;
    private const uint LesserHeal = 2050, Fireball = 133, Smite = 585;

    private static PlayerbotRelation? Relation(GroupTestWorld world, Guid bot, Guid other)
    {
        ObjectGuid guid = world.OnWorldAsync(() => world.Player(other).Guid).GetAwaiter().GetResult();
        return world.Bots.Social.Memory.TryGet(bot, guid.Value, out PlayerbotRelation relation) ? relation : null;
    }

    [Fact]
    public async Task Grouped_IsRecordedOnlyBetweenMembersWhoJoined_NotForInvitedOnes()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest]);
        var tank = await world.AddBotAsync("Joinedtank", Human, Warrior, 10, [Taunt]);
        var healer = await world.AddBotAsync("Joinedheal", Human, Priest, 10, [LesserHeal, Smite, Resurrection]);
        var mage = await world.AddBotAsync("Joinedmage", Human, Mage, 10, [Fireball]);
        Guid[] ids = [tank.Id, healer.Id, mage.Id];
        Assert.True(await world.RunUntilAsync(60_000, () => ids.All(id => world.HasQuest(id, EliteQuest))), await world.TraceAsync());

        // At every tick until the group is complete: a Grouped flag stands only between two members that have both joined the server group.
        bool sawForming = false, sawPartial = false;
        Assert.True(await world.RunUntilAsync(60_000, () =>
        {
            foreach (PlayerbotGroupCoordinator.BotGroup group in world.Coordinator.Groups)
            {
                sawForming |= group.State == PlayerbotGroupState.Forming;
                sawPartial |= group.Members.Any(m => !m.Joined);
                foreach (PlayerbotGroupCoordinator.Member a in group.Members.Where(m => !m.Real))
                    foreach (PlayerbotGroupCoordinator.Member b in group.Members.Where(m => !ReferenceEquals(m, a)))
                        if (world.Bots.Social.Memory.TryGet(a.BotId, b.Guid.Value, out PlayerbotRelation r) && r.Flags.HasFlag(PlayerbotRelationFlags.Grouped))
                            Assert.True(a.Joined && b.Joined, $"{a.Name} remembers {b.Name} as grouped before both joined");
            }

            return world.Coordinator.Groups.Any(g => g.State != PlayerbotGroupState.Forming);
        }), await world.TraceAsync());
        Assert.True(sawForming && sawPartial, "the group was never seen part-joined: nothing was tested");
        foreach (Guid from in ids) Assert.Equal(2, world.Bots.Social.Memory.Snapshot(from).Count); // once joined, every pair is there
    }

    [Fact]
    public async Task AKickIsHeldOnlyForTheCoordinatorsOwnGroups_AndGoesWithTheGroup()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest]);
        var tank = await world.AddBotAsync("Kickedtank", Human, Warrior, 10, [Taunt]);
        var healer = await world.AddBotAsync("Kickedheal", Human, Priest, 10, [LesserHeal, Smite, Resurrection]);
        var mage = await world.AddBotAsync("Kickedmage", Human, Mage, 10, [Fireball]);
        Guid[] ids = [tank.Id, healer.Id, mage.Id];
        Assert.True(await world.RunUntilAsync(60_000, () => ids.All(id => world.HasQuest(id, EliteQuest))), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.State != PlayerbotGroupState.Forming)), await world.TraceAsync());

        ObjectGuid member = await world.OnWorldAsync(() => world.Coordinator.Groups.First().Members.First(m => !m.Real).Guid);
        await world.OnWorldAsync(() =>
        {
            for (uint i = 0; i < 200; i++) world.Coordinator.OnMemberKicked(ObjectGuid.Player(50_000 + i), ObjectGuid.Player(60_000 + i)); // kicks elsewhere in the world
            Assert.Equal(0, world.Coordinator.KickedPending);
            world.Coordinator.OnMemberKicked(ObjectGuid.Player(50_000), member); // one of ours: held (the next scan would see the member gone)
            Assert.Equal(1, world.Coordinator.KickedPending);
            return true;
        });
        Assert.True(await world.RunUntilAsync(240_000, () => world.Coordinator.Groups.Count == 0), await world.TraceAsync());
        Assert.Equal(0, await world.OnWorldAsync(() => world.Coordinator.KickedPending));
    }

    [Fact]
    public async Task ABotLedGroup_RecordsGroupedForEveryPair_AndASuccessfulFinishRaisesAffinityAndTrustForEveryPair_AndTheRolesPlayed()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest]);
        var tank = await world.AddBotAsync("Socialtank", Human, Warrior, 10, [Taunt]);
        var healer = await world.AddBotAsync("Socialheal", Human, Priest, 10, [LesserHeal, Smite, Resurrection]);
        var mage = await world.AddBotAsync("Socialmage", Human, Mage, 10, [Fireball]);
        Guid[] ids = [tank.Id, healer.Id, mage.Id];
        var options = new PlayerbotSocialMemoryOptions();
        Assert.True(await world.RunUntilAsync(60_000, () => ids.All(id => world.HasQuest(id, EliteQuest))), await world.TraceAsync());
        foreach (Guid id in ids) Assert.Empty(world.Bots.Social.Memory.Snapshot(id)); // nobody has met anybody yet

        // The group forms: every ordered pair of its members is remembered as grouped, and nothing more has happened yet.
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.State != PlayerbotGroupState.Forming)), await world.TraceAsync());
        foreach (Guid from in ids)
        {
            Assert.Equal(2, world.Bots.Social.Memory.Snapshot(from).Count);
            foreach (Guid to in ids.Where(id => id != from))
            {
                PlayerbotRelation formed = Relation(world, from, to)!;
                Assert.Equal((short)options.AffinityPerGroup, formed.Affinity);
                Assert.True(formed.Flags.HasFlag(PlayerbotRelationFlags.Grouped));
                Assert.True(formed.OtherIsBot);
                Assert.Equal(1, formed.Familiarity);
                Assert.Equal((short)500, formed.TrustPermille); // trust moves only on a finish
            }
        }

        // The group kills the ogre and disbands successfully.
        Assert.True(await world.RunUntilAsync(240_000, () => world.Coordinator.Groups.Count == 0), await world.TraceAsync());
        Assert.Equal(1, world.Coordinator.Totals.Completed);
        Assert.Equal(0, world.Coordinator.Totals.Failed);
        foreach (Guid from in ids)
        {
            foreach (Guid to in ids.Where(id => id != from))
            {
                PlayerbotRelation done = Relation(world, from, to)!;
                // Groupmates also help each other on the mob they fight (HelpedBy and HelpedThem), so the affinity is at least the group and the
                // success; only a finish moves trust, which makes it the exact witness that the success was recorded for this pair.
                Assert.True(done.Affinity >= options.AffinityPerGroup + options.AffinityPerGroupSuccess, $"affinity {done.Affinity}");
                Assert.Equal((short)(500 + options.TrustPerGroupSuccess), done.TrustPermille);
                Assert.True(done.Flags.HasFlag(PlayerbotRelationFlags.Grouped));
                Assert.True(done.Familiarity >= 2); // met in the group, and again on the success
            }
        }

        // Each bot remembers the role the others played: the tank led as the tank, the priest healed, the mage damaged.
        foreach (Guid from in ids)
        {
            PlayerbotRelationFlags roles = PlayerbotRelationFlags.PrefersTank | PlayerbotRelationFlags.PrefersHealer | PlayerbotRelationFlags.PrefersDamage;
            if (from != tank.Id) Assert.Equal(PlayerbotRelationFlags.PrefersTank, Relation(world, from, tank.Id)!.Flags & roles);
            if (from != healer.Id) Assert.Equal(PlayerbotRelationFlags.PrefersHealer, Relation(world, from, healer.Id)!.Flags & roles);
            if (from != mage.Id) Assert.Equal(PlayerbotRelationFlags.PrefersDamage, Relation(world, from, mage.Id)!.Flags & roles);
        }
    }
}

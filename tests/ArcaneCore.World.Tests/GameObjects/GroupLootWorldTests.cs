using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Loot;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Tests.Items;
using Xunit;

namespace ArcaneCore.World.Tests.GameObjects;

/// <summary>
/// The group loot opcodes through the real host: CMSG_LOOT_ROLL and CMSG_LOOT_MASTER_GIVE reach the loot service of the
/// player's map, and the roll timers run off the map update (GameObjectLootFeature attaches <see cref="LootRollManager"/>).
/// </summary>
public sealed class GroupLootWorldTests
{
    private const uint CreatureEntry = 990201;
    private const uint Sword = 990202;

    private static WorldTestHost Start()
    {
        var content = new LootContent([(LootTableKind.Creature, new LootStoreRow(CreatureEntry, Sword, 100, 0, 1, 1))],
            [new CreatureLootInfo(CreatureEntry, CreatureEntry, 0, 0, 0)]);
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = Sword, Class = 15, Name = "Group Loot Test Blue", DisplayId = 4001, Quality = 3 });
        GameObjectTestStore.Current.Value = new GameObjectTestContext(GameObjectContent.Empty, content);
        try
        {
            using (items.Use())
            {
                return WorldTestHost.Start();
            }
        }
        finally
        {
            GameObjectTestStore.Current.Value = null;
        }
    }

    private static async Task<(WorldTestClient Leader, WorldTestClient Member)> FormGroupAsync(WorldTestHost host)
    {
        WorldTestClient leader = await host.EnterWorldAsync("GLLEAD", "Glleader");
        WorldTestClient member = await host.EnterWorldAsync("GLMEMB", "Glmember");
        await leader.SendAsync(WorldOpcode.CmsgGroupInvite, [.. "glmember"u8, 0]);
        await member.ReadUntilAsync(WorldOpcode.SmsgGroupInvite);
        await member.SendAsync(WorldOpcode.CmsgGroupAccept, []);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Glmember") is { } p
            && ((ArcaneCore.World.Net.WorldSession)p.Session).Services.GetService(typeof(ArcaneCore.World.Social.SocialFeature)) is ArcaneCore.World.Social.SocialFeature social
            && social.Context.Groups.GetGroup(p.Guid) is not null, "the party to form");
        return (leader, member);
    }

    private static async Task<ulong> KillAtAsync(WorldTestHost host, string killerName)
    {
        return await host.OnWorldAsync(() =>
        {
            Player killer = host.World.FindOnlinePlayer(killerName)!;
            var template = new CreatureTemplate { Entry = CreatureEntry, Name = "Group loot corpse", MinLevelHealth = 10, MaxLevelHealth = 10 };
            var system = new CreatureMapSystem(killer.Map!, new CreatureContent([template], [], [], [], []), random: new Random(1));
            killer.Map!.AddUpdater(system);
            Creature creature = system.SpawnTemporary(template, killer.X + 1, killer.Y, killer.Z, 0);
            uint health = creature.Health;
            killer.Map.Combat.DealDamage(killer, creature, health, direct: false);
            Assert.Equal(CreatureDeathState.Corpse, creature.DeathState);
            return creature.Guid.Value;
        });
    }

    private static byte[] Le(ulong value) => BitConverter.GetBytes(value);

    [Fact]
    public async Task LootRoll_NeedAndPass_ResolveOnTheLastVote_AndTheWinnerGetsTheItem()
    {
        await using WorldTestHost host = Start();
        (WorldTestClient leader, WorldTestClient member) = await FormGroupAsync(host);
        await using (leader)
        await using (member)
        {
            ulong corpse = await KillAtAsync(host, "Glleader");
            await leader.SendAsync(WorldOpcode.CmsgLoot, Le(corpse));
            byte[] start = await member.ReadUntilAsync(WorldOpcode.SmsgLootStartRoll);
            Assert.Equal(GroupLootPackets.StartRoll(new ObjectGuid(corpse), 0, Sword, 60000), start);

            // CMSG_LOOT_ROLL: u64 corpse, u32 slot, u8 vote. A vote of 3 is not a vote (dropped), then need and pass.
            await member.SendAsync(WorldOpcode.CmsgLootRoll, [.. Le(corpse), 0, 0, 0, 0, 3]);
            await member.SendAsync(WorldOpcode.CmsgLootRoll, [.. Le(corpse), 0, 0, 0, 0, (byte)RollVote.Need]);
            await leader.SendAsync(WorldOpcode.CmsgLootRoll, [.. Le(corpse), 0, 0, 0, 0, (byte)RollVote.Pass]);
            byte[] won = await leader.ReadUntilAsync(WorldOpcode.SmsgLootRollWon);
            Assert.Equal(28 + 6, won.Length);
            Assert.Equal(1u, await host.PlayerStateAsync("Glmember", p => p.Inventory.GetItemCount(Sword)));
            Assert.Equal(0u, await host.PlayerStateAsync("Glleader", p => p.Inventory.GetItemCount(Sword)));
        }
    }

    [Fact]
    public async Task LootRoll_NobodyVotes_TheMapUpdateExpiresTheTimerIntoAllPassed()
    {
        await using WorldTestHost host = Start();
        (WorldTestClient leader, WorldTestClient member) = await FormGroupAsync(host);
        await using (leader)
        await using (member)
        {
            ulong corpse = await KillAtAsync(host, "Glleader");
            await host.OnWorldAsync(() =>
            {
                Player p = host.World.FindOnlinePlayer("Glleader")!;
                p.Map!.FindUpdater<ArcaneCore.Game.GameObjects.GameObjectMapSystem>()!.Loot!.Options.RollTimeoutMs = 100;
            });
            await leader.SendAsync(WorldOpcode.CmsgLoot, Le(corpse));
            await leader.ReadUntilAsync(WorldOpcode.SmsgLootStartRoll);
            byte[] passed = await leader.ReadUntilAsync(WorldOpcode.SmsgLootAllPassed);
            Assert.Equal(GroupLootPackets.AllPassed(new ObjectGuid(corpse), 0, Sword), passed);
            await member.ReadUntilAsync(WorldOpcode.SmsgLootAllPassed);
        }
    }

    [Fact]
    public async Task MasterLoot_ListThenGive_LandsTheItemInTheRecipientsBags()
    {
        await using WorldTestHost host = Start();
        (WorldTestClient leader, WorldTestClient member) = await FormGroupAsync(host);
        await using (leader)
        await using (member)
        {
            ulong memberGuid = await host.PlayerStateAsync("Glmember", p => p.Guid.Value);
            ulong leaderGuid = await host.PlayerStateAsync("Glleader", p => p.Guid.Value);
            // CMSG_LOOT_METHOD: u32 method (2 = master), u64 master looter, u32 threshold (2 = uncommon).
            await leader.SendAsync(WorldOpcode.CmsgLootMethod, [.. BitConverter.GetBytes((uint)LootMethod.MasterLoot), .. Le(leaderGuid), .. BitConverter.GetBytes(2u)]);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Glleader") is { } p
                && ((ArcaneCore.World.Net.WorldSession)p.Session).Services.GetService(typeof(ArcaneCore.World.Social.SocialFeature)) is ArcaneCore.World.Social.SocialFeature social
                && social.Context.Groups.GetGroup(p.Guid)?.LootMethod == LootMethod.MasterLoot, "master loot to be set");

            ulong corpse = await KillAtAsync(host, "Glmember");
            await leader.SendAsync(WorldOpcode.CmsgLoot, Le(corpse));
            byte[] list = await leader.ReadUntilAsync(WorldOpcode.SmsgLootMasterList);
            Assert.Equal(GroupLootPackets.MasterList([new ObjectGuid(leaderGuid), new ObjectGuid(memberGuid)]), list);

            // CMSG_LOOT_MASTER_GIVE: u64 loot guid, u8 slot, u64 target.
            await leader.SendAsync(WorldOpcode.CmsgLootMasterGive, [.. Le(corpse), 0, .. Le(memberGuid)]);
            await leader.ReadUntilAsync(WorldOpcode.SmsgLootRemoved);
            Assert.Equal(1u, await host.PlayerStateAsync("Glmember", p => p.Inventory.GetItemCount(Sword)));
            Assert.Equal(0u, await host.PlayerStateAsync("Glleader", p => p.Inventory.GetItemCount(Sword)));

            // A short payload is ignored, not answered.
            await leader.SendAsync(WorldOpcode.CmsgLootMasterGive, [.. Le(corpse), 0]);
            await leader.SendAsync(WorldOpcode.CmsgLootRoll, [.. Le(corpse), 0]);
        }
    }
}

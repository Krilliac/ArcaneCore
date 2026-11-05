using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Loot;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Tests.GameObjects;
using ArcaneCore.World.Tests.Items;
using Xunit;

namespace ArcaneCore.World.Tests.Handlers;

/// <summary>
/// The consecutive-play-time limit (<c>World:LimitPlayTime</c>, default off; <see cref="PlayTimeLimit"/>, vmangos WorldSession::CheckPlayedTimeLimit) through a real
/// session over a loopback socket on the manual game clock (10-minute ticks so five hours cost 30 ticks): which SMSG_PLAY_TIME_WARNING each step sends and which
/// flags it sets, that nothing happens with the option off, and the two handler effects of the NO_PLAY_TIME flag that send or answer (loot open, loot roll).
/// Written without a build or test run (lane rule); unproven until the integration gate.
/// </summary>
[Collection("World tick load")]
public sealed class PlayTimeLimitTests
{
    private const uint StepMs = 600_000;

    private static WorldTestHost StartHost(bool limit) => WorldTestHost.Start(clock: TestClock.Manual(stepMs: StepMs), configure: o => o.LimitPlayTime = limit);

    private static async Task<uint> AdvanceToStateAsync(WorldTestHost host, string character, uint state)
    {
        for (int i = 0; i < 40; i++)
        {
            await host.AdvanceAsync(StepMs);
            (uint reached, uint played) = await host.OnWorldAsync(() =>
            {
                var session = (WorldSession)host.World.FindOnlinePlayer(character)!.Session;
                return (session.ExhaustionState, session.PlayedSeconds(host.World.NowMs));
            });
            if (reached >= state)
            {
                return played;
            }
        }

        throw new Xunit.Sdk.XunitException("the play-time step " + state + " was never reached");
    }

    private static (uint Flag, int SecondsLeft) Warning(byte[] body)
    {
        Assert.Equal(8, body.Length);
        return (BinaryPrimitives.ReadUInt32LittleEndian(body), BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4)));
    }

    [Fact]
    public async Task WithTheOptionOff_FiveHoursOfPlay_SendNoWarningAndSetNoFlag()
    {
        await using WorldTestHost host = StartHost(limit: false);
        await using WorldTestClient client = await host.EnterWorldAsync("PTOFF", "Ptoffone");
        await host.SettleAsync();
        await client.CollectAsync();

        for (int i = 0; i < 31; i++)
        {
            await host.AdvanceAsync(StepMs);
        }

        await client.AssertNoPacketsAfterAsync(TimeSpan.FromMilliseconds(300), WorldOpcode.SmsgPlayTimeWarning);
        Assert.Equal(0u, (uint)(await host.PlayerStateAsync("Ptoffone", p => p.Flags) & (PlayerFlags.PartialPlayTime | PlayerFlags.NoPlayTime)));
    }

    [Fact]
    public async Task WithTheOptionOn_EachStepSendsItsWarning_AndFlagsFollowVmangos()
    {
        await using WorldTestHost host = StartHost(limit: true);
        await using WorldTestClient client = await host.EnterWorldAsync("PTON", "Ptonone");
        await host.SettleAsync();
        await client.CollectAsync();
        const PlayerFlags Both = PlayerFlags.PartialPlayTime | PlayerFlags.NoPlayTime;

        // 2 h 30 min: approaching the partial limit, 30 minutes to go; no flag yet.
        uint played = await AdvanceToStateAsync(host, "Ptonone", PlayTimeLimit.ApproachingPartialSeconds);
        Assert.InRange(played, PlayTimeLimit.ApproachingPartialSeconds, PlayTimeLimit.ApproachingPartialSeconds + StepMs / 1000 - 1);
        (uint flag, int left) = Warning(await client.ReadUntilAsync(WorldOpcode.SmsgPlayTimeWarning));
        Assert.Equal(PlayTimeLimit.ApproachingPartialFlag, flag);
        Assert.Equal((int)(PlayTimeLimit.PartialSeconds - played), left);
        Assert.Equal(PlayerFlags.None, await host.PlayerStateAsync("Ptonone", p => p.Flags & Both));

        // 3 h: partial play time (half experience), 2 hours to the full limit.
        played = await AdvanceToStateAsync(host, "Ptonone", PlayTimeLimit.PartialSeconds);
        (flag, left) = Warning(await client.ReadUntilAsync(WorldOpcode.SmsgPlayTimeWarning));
        Assert.Equal(PlayTimeLimit.ApproachingNoPlayTimeFlag, flag);
        Assert.Equal((int)(PlayTimeLimit.FullSeconds - played), left);
        Assert.Equal(PlayerFlags.PartialPlayTime, await host.PlayerStateAsync("Ptonone", p => p.Flags & Both));

        // 4 h 30 min: the same warning with 30 minutes left.
        played = await AdvanceToStateAsync(host, "Ptonone", PlayTimeLimit.ApproachingFullSeconds);
        (flag, left) = Warning(await client.ReadUntilAsync(WorldOpcode.SmsgPlayTimeWarning));
        Assert.Equal(PlayTimeLimit.ApproachingNoPlayTimeFlag, flag);
        Assert.Equal((int)(PlayTimeLimit.FullSeconds - played), left);
        Assert.Equal(PlayerFlags.PartialPlayTime, await host.PlayerStateAsync("Ptonone", p => p.Flags & Both));

        // 5 h: the unhealthy-time warning, NO_PLAY_TIME set and PARTIAL_PLAY_TIME cleared.
        await AdvanceToStateAsync(host, "Ptonone", PlayTimeLimit.FullSeconds);
        Assert.Equal((PlayTimeLimit.UnhealthyFlag, 0), Warning(await client.ReadUntilAsync(WorldOpcode.SmsgPlayTimeWarning)));
        Assert.Equal(PlayerFlags.NoPlayTime, await host.PlayerStateAsync("Ptonone", p => p.Flags & Both));

        // Each step sends once: nothing more however long the session goes on.
        await host.AdvanceAsync(StepMs * 6);
        await client.AssertNoPacketsAfterAsync(TimeSpan.FromMilliseconds(300), WorldOpcode.SmsgPlayTimeWarning);
    }

    [Fact]
    public async Task TheLoadTimeRule_FlagsAtThreeAndFiveHours_AndNeverClearsAFlag()
    {
        // Player.cpp:14690-14697: a character entering with a long-played session gets PARTIAL from 3 h and NO from 5 h; nothing is removed there.
        await using WorldTestHost host = StartHost(limit: true);
        await using WorldTestClient client = await host.EnterWorldAsync("PTLOAD", "Ptloadone");
        Player probe = await host.PlayerAsync("Ptloadone");
        const PlayerFlags Both = PlayerFlags.PartialPlayTime | PlayerFlags.NoPlayTime;
        await host.OnWorldAsync(() =>
        {
            probe.Flags &= ~Both;
            PlayTimeLimit.ApplyLoadFlags(probe, PlayTimeLimit.PartialSeconds - 1);
            Assert.Equal(PlayerFlags.None, probe.Flags & Both);
            PlayTimeLimit.ApplyLoadFlags(probe, PlayTimeLimit.PartialSeconds);
            Assert.Equal(PlayerFlags.PartialPlayTime, probe.Flags & Both);
            PlayTimeLimit.ApplyLoadFlags(probe, PlayTimeLimit.FullSeconds);
            Assert.Equal(Both, probe.Flags & Both); // NO is added, PARTIAL stays: only CheckPlayedTimeLimit clears it
        });
    }

    private const uint CreatureEntry = 990301;
    private const uint Sword = 990302;

    private static WorldTestHost StartLootHost()
    {
        var content = new LootContent([(LootTableKind.Creature, new LootStoreRow(CreatureEntry, Sword, 100, 0, 1, 1))],
            [new CreatureLootInfo(CreatureEntry, CreatureEntry, 0, 0, 0)]);
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = Sword, Class = 15, Name = "Play Time Test Blue", DisplayId = 4001, Quality = 3 });
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

    private static async Task<ulong> KillAtAsync(WorldTestHost host, string killerName)
    {
        return await host.OnWorldAsync(() =>
        {
            Player killer = host.World.FindOnlinePlayer(killerName)!;
            var template = new CreatureTemplate { Entry = CreatureEntry, Name = "Play time corpse", MinLevelHealth = 10, MaxLevelHealth = 10 };
            var system = new CreatureMapSystem(killer.Map!, new CreatureContent([template], [], [], [], []), random: new Random(1));
            killer.Map!.AddUpdater(system);
            Creature creature = system.SpawnTemporary(template, killer.X + 1, killer.Y, killer.Z, 0);
            killer.Map.Combat.DealDamage(killer, creature, creature.Health, direct: false);
            return creature.Guid.Value;
        });
    }

    private static byte[] Le(ulong value) => BitConverter.GetBytes(value);

    [Fact]
    public async Task ANoPlayTimePlayer_IsRefusedTheLootWindow_WithErrorElevenAndTheNormalPlayerIsNot()
    {
        await using WorldTestHost host = StartLootHost();
        await using WorldTestClient flagged = await host.EnterWorldAsync("PTLOOT", "Ptlootone");
        ulong corpse = await KillAtAsync(host, "Ptlootone");
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Ptlootone")!.Flags |= PlayerFlags.NoPlayTime);
        await flagged.CollectAsync();

        await flagged.SendAsync(WorldOpcode.CmsgLoot, Le(corpse));
        byte[] refused = await flagged.ReadUntilAsync(WorldOpcode.SmsgLootResponse);
        Assert.Equal([.. Le(corpse), 0, (byte)LootError.PlayTimeExceeded], refused); // guid, loot type 0, error 11 (LootHandler.cpp:358-362)

        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Ptlootone")!.Flags &= ~PlayerFlags.NoPlayTime);
        await flagged.SendAsync(WorldOpcode.CmsgLoot, Le(corpse));
        byte[] opened = await flagged.ReadUntilAsync(WorldOpcode.SmsgLootResponse);
        Assert.NotEqual((byte)LootError.PlayTimeExceeded, opened[9]);
        Assert.True(opened.Length > 10, "a real loot window, not the error form");
    }

    [Fact]
    public async Task ANoPlayTimePlayersRoll_BecomesAPass_AndItIsToldWhy()
    {
        await using WorldTestHost host = StartLootHost();
        await using WorldTestClient leader = await host.EnterWorldAsync("PTLEAD", "Ptleader");
        await using WorldTestClient member = await host.EnterWorldAsync("PTMEMB", "Ptmember");
        await leader.SendAsync(WorldOpcode.CmsgGroupInvite, [.. "ptmember"u8, 0]);
        await member.ReadUntilAsync(WorldOpcode.SmsgGroupInvite);
        await member.SendAsync(WorldOpcode.CmsgGroupAccept, []);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Ptmember") is { } p
            && ((WorldSession)p.Session).Services.GetService(typeof(ArcaneCore.World.Social.SocialFeature)) is ArcaneCore.World.Social.SocialFeature social
            && social.Context.Groups.GetGroup(p.Guid) is not null, "the party to form");
        ulong corpse = await KillAtAsync(host, "Ptleader");
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Ptmember")!.Flags |= PlayerFlags.NoPlayTime);

        await leader.SendAsync(WorldOpcode.CmsgLoot, Le(corpse));
        await member.ReadUntilAsync(WorldOpcode.SmsgLootStartRoll);
        await member.SendAsync(WorldOpcode.CmsgLootRoll, [.. Le(corpse), 0, 0, 0, 0, (byte)RollVote.Need]); // would win, but is flagged
        Assert.Equal((PlayTimeLimit.UnhealthyFlag, 0), Warning(await member.ReadUntilAsync(WorldOpcode.SmsgPlayTimeWarning)));
        await leader.SendAsync(WorldOpcode.CmsgLootRoll, [.. Le(corpse), 0, 0, 0, 0, (byte)RollVote.Pass]);

        await leader.ReadUntilAsync(WorldOpcode.SmsgLootAllPassed); // both passed: the flagged Need was turned into a Pass
        Assert.Equal(0u, await host.PlayerStateAsync("Ptmember", p => p.Inventory.GetItemCount(Sword)));
    }
}

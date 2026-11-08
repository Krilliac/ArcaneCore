using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Loot;
using ArcaneCore.Kernel.Accounts;
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
                return (session.ExhaustionState, session.PlayedSeconds(host.World.Clock.ElapsedMs));
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

    private static WorldTestHost StartLootHost(uint gold = 0)
    {
        var content = new LootContent([(LootTableKind.Creature, new LootStoreRow(CreatureEntry, Sword, 100, 0, 1, 1))],
            [new CreatureLootInfo(CreatureEntry, CreatureEntry, 0, gold, gold)]);
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

    private static async Task<(WorldTestClient Leader, WorldTestClient Member)> FormGroupAsync(WorldTestHost host)
    {
        WorldTestClient leader = await host.EnterWorldAsync("PTGL", "Ptgleader");
        WorldTestClient member = await host.EnterWorldAsync("PTGM", "Ptgmember");
        await leader.SendAsync(WorldOpcode.CmsgGroupInvite, [.. "ptgmember"u8, 0]);
        await member.ReadUntilAsync(WorldOpcode.SmsgGroupInvite);
        await member.SendAsync(WorldOpcode.CmsgGroupAccept, []);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Ptgmember") is { } p
            && ((WorldSession)p.Session).Services.GetService(typeof(ArcaneCore.World.Social.SocialFeature)) is ArcaneCore.World.Social.SocialFeature social
            && social.Context.Groups.GetGroup(p.Guid) is not null, "the party to form");
        return (leader, member);
    }

    [Fact]
    public async Task MasterLoot_GivingToANoPlayTimeRecipient_AnswersErrorFourteen_AndMovesNoItem()
    {
        // LootHandler.cpp:642-646: the master looter is refused with LOOT_ERROR_MASTER_OTHER (14); the item stays in the bag and can be given once the flag is gone.
        await using WorldTestHost host = StartLootHost();
        (WorldTestClient leader, WorldTestClient member) = await FormGroupAsync(host);
        await using (leader)
        await using (member)
        {
            ulong memberGuid = await host.PlayerStateAsync("Ptgmember", p => p.Guid.Value);
            ulong leaderGuid = await host.PlayerStateAsync("Ptgleader", p => p.Guid.Value);
            await leader.SendAsync(WorldOpcode.CmsgLootMethod, [.. BitConverter.GetBytes((uint)LootMethod.MasterLoot), .. Le(leaderGuid), .. BitConverter.GetBytes(2u)]);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Ptgleader") is { } p
                && ((WorldSession)p.Session).Services.GetService(typeof(ArcaneCore.World.Social.SocialFeature)) is ArcaneCore.World.Social.SocialFeature social
                && social.Context.Groups.GetGroup(p.Guid)?.LootMethod == LootMethod.MasterLoot, "master loot to be set");

            ulong corpse = await KillAtAsync(host, "Ptgmember");
            await leader.SendAsync(WorldOpcode.CmsgLoot, Le(corpse));
            await leader.ReadUntilAsync(WorldOpcode.SmsgLootMasterList);
            await leader.CollectAsync(); // the loot window and anything else the open sent

            await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Ptgmember")!.Flags |= PlayerFlags.NoPlayTime);
            await leader.SendAsync(WorldOpcode.CmsgLootMasterGive, [.. Le(corpse), 0, .. Le(memberGuid)]);
            byte[] refused = await leader.ReadUntilAsync(WorldOpcode.SmsgLootResponse);
            Assert.Equal(GroupLootPackets.LootErrorResponse(new ObjectGuid(corpse), LootError.MasterOther), refused);
            Assert.Equal(14, refused[^1]);
            await leader.AssertNoPacketsAsync(WorldOpcode.SmsgLootRemoved);
            Assert.Equal(0u, await host.PlayerStateAsync("Ptgmember", p => p.Inventory.GetItemCount(Sword)));
            Assert.Equal(0u, await host.PlayerStateAsync("Ptgleader", p => p.Inventory.GetItemCount(Sword)));

            // Positive control: the same give works once the flag is gone, so the refusal above was the flag and nothing else.
            await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Ptgmember")!.Flags &= ~PlayerFlags.NoPlayTime);
            await leader.SendAsync(WorldOpcode.CmsgLootMasterGive, [.. Le(corpse), 0, .. Le(memberGuid)]);
            await leader.ReadUntilAsync(WorldOpcode.SmsgLootRemoved);
            Assert.Equal(1u, await host.PlayerStateAsync("Ptgmember", p => p.Inventory.GetItemCount(Sword)));
        }
    }

    [Theory]
    [InlineData(PlayerFlags.None, 500u)]
    [InlineData(PlayerFlags.PartialPlayTime, 250u)]
    [InlineData(PlayerFlags.NoPlayTime, 0u)]
    public async Task LootedMoney_IsSplitInFull_ButAFlaggedSharerGetsHalfOrNothing(PlayerFlags flag, uint memberGets)
    {
        // Player::LootMoney (Player.cpp:21793-21797) per sharer; the share notify still names the full share (LootHandler.cpp:322-323).
        await using WorldTestHost host = StartLootHost(gold: 1000);
        (WorldTestClient leader, WorldTestClient member) = await FormGroupAsync(host);
        await using (leader)
        await using (member)
        {
            ulong corpse = await KillAtAsync(host, "Ptgleader");
            uint leaderBefore = await host.PlayerStateAsync("Ptgleader", p => p.Money);
            uint memberBefore = await host.PlayerStateAsync("Ptgmember", p => p.Money);
            await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Ptgmember")!.Flags |= flag);
            await leader.SendAsync(WorldOpcode.CmsgLoot, Le(corpse));
            await leader.ReadUntilAsync(WorldOpcode.SmsgLootResponse);
            await leader.SendAsync(WorldOpcode.CmsgLootMoney, []);
            Assert.Equal(500u, BitConverter.ToUInt32(await member.ReadUntilAsync(WorldOpcode.SmsgLootMoneyNotify)));
            await leader.ReadUntilAsync(WorldOpcode.SmsgLootClearMoney);

            Assert.Equal(500u, await host.PlayerStateAsync("Ptgleader", p => p.Money) - leaderBefore);
            Assert.Equal(memberGets, await host.PlayerStateAsync("Ptgmember", p => p.Money) - memberBefore);
        }
    }

    private static async Task<(WorldTestClient Client, int AccountId, ulong Guid, byte[] Key)> LoginNewAccountAsync(WorldTestHost host, string account, string character)
    {
        byte[] key = await host.AddAccountAsync(account);
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(account, key);
        await client.CreateCharacterAsync(character);
        Account stored = (await host.Accounts.FindByUsernameAsync(account))!;
        ulong guid = (ulong)(await host.Characters.GetByAccountAsync(stored.Id)).Single().Id;
        await client.LoginAsync(guid);
        return (client, stored.Id, guid, key);
    }

    private static async Task<WorldTestClient> LoginAgainAsync(WorldTestHost host, string account, byte[] key, ulong guid)
    {
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(account, key);
        await client.LoginAsync(guid);
        return client;
    }

    [Fact]
    public async Task ARelogWithinFiveHours_ContinuesTheAccountsPlayTime_AndFlagsTheCharacterAtLoad()
    {
        // vmangos World.cpp:293-300: a login less than five hours after the account's last logout carries the old consecutive play time, so
        // relogging every 2 h 29 min never resets the limit. Without the carry the second session would start at 0 and set no flag.
        await using WorldTestHost host = StartHost(limit: true);
        (WorldTestClient first, int accountId, ulong guid, byte[] key) = await LoginNewAccountAsync(host, "PTRELOG", "Ptrelogone");
        uint playedBefore = await AdvanceToStateAsync(host, "Ptrelogone", PlayTimeLimit.PartialSeconds);
        await first.DisposeAsync();
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Ptrelogone") is null, "the first session to leave the world");
        Assert.Null(host.Registry.Find(accountId));

        await using WorldTestClient second = await LoginAgainAsync(host, "PTRELOG", key, guid);
        await host.AdvanceAsync(StepMs);
        (uint played, PlayerFlags flags) = await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Ptrelogone")!;
            return (((WorldSession)player.Session).PlayedSeconds(host.World.Clock.ElapsedMs), player.Flags);
        });
        Assert.True(played >= playedBefore + StepMs / 1000, $"the second session continues the account's {playedBefore} s, not {played} s");
        Assert.Equal(PlayerFlags.PartialPlayTime, flags & (PlayerFlags.PartialPlayTime | PlayerFlags.NoPlayTime));
    }

    [Fact]
    public async Task ARelogAfterFiveHoursAway_StartsTheAccountsPlayTimeAgain()
    {
        await using WorldTestHost host = StartHost(limit: true);
        (WorldTestClient first, int accountId, ulong guid, byte[] key) = await LoginNewAccountAsync(host, "PTRESET", "Ptresetone");
        await AdvanceToStateAsync(host, "Ptresetone", PlayTimeLimit.ApproachingPartialSeconds);
        await first.DisposeAsync();
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Ptresetone") is null, "the first session to leave the world");
        Assert.Null(host.Registry.Find(accountId));
        await host.AdvanceAsync(StepMs * 31); // 5 h 10 min away: the history no longer counts (World.cpp:297-300)

        await using WorldTestClient second = await LoginAgainAsync(host, "PTRESET", key, guid);
        await host.AdvanceAsync(StepMs);
        (uint played, uint state, PlayerFlags flags) = await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Ptresetone")!;
            var session = (WorldSession)player.Session;
            return (session.PlayedSeconds(host.World.Clock.ElapsedMs), session.ExhaustionState, player.Flags);
        });
        Assert.InRange(played, 0u, 2 * StepMs / 1000);
        Assert.Equal(0u, state);
        Assert.Equal(PlayerFlags.None, flags & (PlayerFlags.PartialPlayTime | PlayerFlags.NoPlayTime));
    }

    [Fact]
    public async Task ADoubleLogin_PassesTheConsecutivePlayTimeToTheNewSession()
    {
        // vmangos World.cpp:285-286: the session that replaces a live one of the same account starts with the old one's consecutive play time.
        await using WorldTestHost host = StartHost(limit: true);
        (WorldTestClient first, int accountId, _, byte[] key) = await LoginNewAccountAsync(host, "PTDOUBLE", "Ptdoubleone");
        await using (first)
        {
            uint playedBefore = await AdvanceToStateAsync(host, "Ptdoubleone", PlayTimeLimit.PartialSeconds);
            WorldSession old = host.Registry.Find(accountId)!;

            await using WorldTestClient second = await host.ConnectAsync();
            await second.AuthenticateAsync("PTDOUBLE", key);
            await WorldTestHost.WaitForAsync(() => !ReferenceEquals(host.Registry.Find(accountId), old), "the new session to replace the old one");
            uint played = await host.OnWorldAsync(() => host.Registry.Find(accountId)!.PlayedSeconds(host.World.Clock.ElapsedMs));
            Assert.True(played >= playedBefore, $"the replacing session starts with the old session's {playedBefore} s, not {played} s");
        }
    }
}

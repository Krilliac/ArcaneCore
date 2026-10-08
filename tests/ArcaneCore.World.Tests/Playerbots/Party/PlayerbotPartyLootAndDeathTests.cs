using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Party;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Party;

/// <summary>
/// Round-robin loot the bot holds (vmangos PartyBotAI.cpp:565-585 unassigns it on every party kill) and the dead bot's wait, on the
/// real world: the feature's own bots for the release whoever made the kill, and a hand-driven <see cref="PlayerbotPartyAI"/> on a
/// managed session nothing else ticks for the action budget and the corpse run, one think at a time.
/// </summary>
public sealed class PlayerbotPartyLootAndDeathTests
{
    /// <summary>
    /// Round robin hands the corpses out in turn: the master's kill, the bot's, the master's, the bot's. The bot is passive (it targets
    /// nothing), two of the kills are the master's, and still both corpses the bot holds are opened to the group; the master's own
    /// stay his.
    /// </summary>
    [Fact]
    public async Task RoundRobinLootTheBotHolds_IsReleased_WhoeverMadeTheKill_WhileThePassiveBotTargetsNothing()
    {
        await using WorldTestHost host = PartyTestHost.Start(options => options.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone);
        Guid bot = await PartyTestHost.StartBotAsync(host, "Partyrobin");
        await using WorldTestClient master = await host.EnterWorldAsync("PARTYROBINM", "Partyrobinm");
        ulong botGuid = (await host.PlayerStateAsync("Partyrobin", p => p.Guid)).Value;
        await master.SendAsync(WorldOpcode.CmsgGroupInvite, PartyTestHost.CString("Partyrobin"));
        await host.WaitForWorldAsync(() => PartyTestHost.Feature(host).IsPartyDriven(bot), "the party AI drives the bot");
        await master.SendChatAsync(ChatType.Whisper, Language.Common, "passive", "Partyrobin");
        Assert.Equal("Passive: I will not attack.", (await PartyTestHost.ReadWhisperFromAsync(master, botGuid)).Text);

        LootBag[] bags = await host.OnWorldAsync(() =>
        {
            Player owner = host.World.FindOnlinePlayer("Partyrobinm")!;
            Player follower = host.World.FindOnlinePlayer("Partyrobin")!;
            Groups(host).SetLootMethod(owner, (uint)LootMethod.RoundRobin, ObjectGuid.Empty, 2);
            Assert.Equal(LootMethod.RoundRobin, Groups(host).GetGroup(owner.Guid)!.LootMethod);
            CreatureMapSystem creatures = PartyTestHost.GoldCreatures(follower);
            var result = new LootBag[4];
            for (int i = 0; i < 4; i++)
            {
                Creature corpse = PartyTestHost.KillGoldCreature(creatures, follower, 1f + i, i == 1 ? follower : owner);
                result[i] = Loot(host, follower).FindLoot(corpse.Guid) ?? throw new InvalidOperationException("no loot for kill " + i);
            }

            return result;
        });
        ObjectGuid masterGuid = await host.PlayerStateAsync("Partyrobinm", p => p.Guid);
        Assert.Equal([masterGuid.Value, botGuid, masterGuid.Value, botGuid], await host.OnWorldAsync(() => bags.Select(b => b.Owner.Value).ToArray()));

        await host.WaitForWorldAsync(() => bags[1].Owner.IsEmpty && bags[3].Owner.IsEmpty, "the bot gives up both corpses it holds");
        Assert.Equal([masterGuid.Value, 0UL, masterGuid.Value, 0UL], await host.OnWorldAsync(() => bags.Select(b => b.Owner.Value).ToArray()));
        Assert.Equal(PlayerbotPartyMode.Passive, await host.OnWorldAsync(() => PartyTestHost.Feature(host).FindParty(bot)!.Mode));
    }

    /// <summary>
    /// A staying bot still gives up the loot it holds: it walks to the corpse (the loot window opens only within 5 yards), releases it,
    /// and walks back to the place it was told to hold.
    /// </summary>
    [Fact]
    public async Task AStayingBot_WalksOverToReleaseItsLoot_ThenBackToItsPlace()
    {
        await using WorldTestHost host = PartyTestHost.Start(options => options.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone);
        Guid bot = await PartyTestHost.StartBotAsync(host, "Partystayer");
        await PartyTestHost.InstallFlatGroundAsync(host, await host.PlayerStateAsync("Partystayer", p => p.Z));
        await using WorldTestClient master = await host.EnterWorldAsync("PARTYSTAYERM", "Partystayerm");
        ulong botGuid = (await host.PlayerStateAsync("Partystayer", p => p.Guid)).Value;
        await master.SendAsync(WorldOpcode.CmsgGroupInvite, PartyTestHost.CString("Partystayer"));
        await host.WaitForWorldAsync(() => PartyTestHost.Feature(host).IsPartyDriven(bot), "the party AI drives the bot");
        await master.SendChatAsync(ChatType.Whisper, Language.Common, "stay", "Partystayer");
        Assert.Equal("Staying here.", (await PartyTestHost.ReadWhisperFromAsync(master, botGuid)).Text);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Partystayer") is { } p && (p.Movement.Flags & MovementFlags.MaskMoving) == 0,
            "the bot stands still");
        (float X, float Y) place = await host.PlayerStateAsync("Partystayer", p => (p.X, p.Y));

        LootBag held = await host.OnWorldAsync(() =>
        {
            Player owner = host.World.FindOnlinePlayer("Partystayerm")!;
            Player follower = host.World.FindOnlinePlayer("Partystayer")!;
            Groups(host).SetLootMethod(owner, (uint)LootMethod.RoundRobin, ObjectGuid.Empty, 2);
            CreatureMapSystem creatures = PartyTestHost.GoldCreatures(follower);
            // 12 yards SOUTH: the test map's area triggers lie on the start's east-west line (MapTestData: the Deadmines entrance box
            // 10 yards west, the "Test shortcut" teleport sphere 10 yards east), and a bot walking into one reports it like a client
            // (PlayerbotAreaTriggers) and is teleported away mid-walk.
            PartyTestHost.KillGoldCreature(creatures, follower, 0f, owner, yOffset: -12f); // the master's turn
            Creature corpse = PartyTestHost.KillGoldCreature(creatures, follower, 0f, owner, yOffset: -12f); // the bot's, 12 yards off
            LootBag bag = Loot(host, follower).FindLoot(corpse.Guid)!;
            Assert.Equal(follower.Guid, bag.Owner);
            return bag;
        });

        await host.WaitForWorldAsync(() => held.Owner.IsEmpty, "the staying bot releases the corpse");
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Partystayer") is { } p
            && MathF.Sqrt(MathF.Pow(p.X - place.X, 2) + MathF.Pow(p.Y - place.Y, 2)) <= 1.5f && (p.Movement.Flags & MovementFlags.MaskMoving) == 0,
            "the bot is back at its place");
        Assert.Equal(PlayerbotPartyMode.Stay, await host.OnWorldAsync(() => PartyTestHost.Feature(host).FindParty(bot)!.Mode));
    }

    /// <summary>
    /// The bot's CMSG_LOOT draws on the shared per-tick action budget. A release the budget held back is not lost: the corpse is still
    /// held, so the next think releases it.
    /// </summary>
    [Fact]
    public async Task ARoundRobinReleaseTheBudgetHeldBack_IsDoneOnTheNextThink()
    {
        await using WorldTestHost host = PartyTestHost.Start(options => options.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone);
        await using WorldTestClient master = await host.EnterWorldAsync("PARTYBUDGETM", "Partybudgetm");
        WorldSession session = await PartyTestHost.EnterManagedAsync(host, "PARTYBUDGETB", "Partybudget");
        try
        {
            var ai = new PlayerbotPartyAI(session, PartyTestHost.PlayerbotOptionsOf(host));
            Creature corpse = await host.OnWorldAsync(() =>
            {
                (Player owner, Player follower) = Group(host, "Partybudgetm", session);
                Assert.True(ai.Drives(follower));
                Groups(host).SetLootMethod(owner, (uint)LootMethod.RoundRobin, ObjectGuid.Empty, 2);
                CreatureMapSystem creatures = PartyTestHost.GoldCreatures(follower);
                PartyTestHost.KillGoldCreature(creatures, follower, 1f, owner); // the master's turn
                Creature second = PartyTestHost.KillGoldCreature(creatures, follower, 2f, owner); // the bot's turn, whoever kills
                Assert.Equal(follower.Guid, Loot(host, follower).FindLoot(second.Guid)!.Owner);
                return second;
            });
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(corpse.Guid), "the bot sees the corpse");

            await host.OnWorldAsync(() =>
            {
                Player follower = session.Player!;
                session.ManagedBudget = new ManagedActionBudget(0);
                ai.Update(follower, 1_000);
                Assert.Equal(follower.Guid, Loot(host, follower).FindLoot(corpse.Guid)!.Owner); // held back: still the bot's
                session.ManagedBudget = new ManagedActionBudget(8);
                ai.Update(follower, 1_000);
                LootBag bag = Loot(host, follower).FindLoot(corpse.Guid)!;
                Assert.True(bag.Owner.IsEmpty, $"the next think releases it (goal {ai.Goal}, combat {follower.Combat.IsInCombat}, "
                    + $"closed {bag.IsClosed}, gold {bag.Gold}, corpse {corpse.DeathState}, open {Loot(host, follower).OpenLootOf(follower)?.Source}, "
                    + $"distance {MathF.Sqrt(MathF.Pow(corpse.X - follower.X, 2) + MathF.Pow(corpse.Y - follower.Y, 2))}, moving {follower.Movement.Flags})");
            });
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    /// <summary>
    /// With AutoRevive on, a dead bot that nobody can help waits; when the wait runs out it releases and runs back to its body
    /// (PlayerbotRecovery) and keeps to that: the released ghost is not revived where it stands at the next think, as a ghost that
    /// was released before it was grouped would be (vmangos ShouldAutoRevive). Here the corpse reclaim delay keeps it a ghost.
    /// </summary>
    [Fact]
    public async Task ADeadBotWhoseWaitRanOut_RunsBackToItsBody_InsteadOfRevivingAsAGhost()
    {
        await using WorldTestHost host = PartyTestHost.Start(options => options.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone);
        await using WorldTestClient master = await host.EnterWorldAsync("PARTYDEADWM", "Partydeadwm");
        WorldSession session = await PartyTestHost.EnterManagedAsync(host, "PARTYDEADWB", "Partydeadw");
        try
        {
            var ai = new PlayerbotPartyAI(session, PartyTestHost.PlayerbotOptionsOf(host)) { DeadWaitMs = 0 };
            Assert.True(PartyTestHost.PlayerbotOptionsOf(host).Party.AutoRevive);
            await host.OnWorldAsync(() =>
            {
                (Player owner, Player follower) = Group(host, "Partydeadwm", session);
                Assert.True(ai.Drives(follower));
                // Nobody within 15 yards and no healer: vmangos ShouldAutoRevive says no, so the bot would wait.
                owner.Relocate(follower.X + 40f, follower.Y, follower.Z, owner.Orientation, host.World.NowMs);
                follower.Health = 0;
                follower.Map!.Combat.KillPlayer(follower);
                session.ManagedBudget = null;
                ai.Update(follower, 1_000); // the wait (0 ms) has run out: release
                Assert.True((follower.Flags & PlayerFlags.Ghost) != 0, "the bot released its spirit");
            });

            for (int think = 0; think < 3; think++)
            {
                await host.OnWorldAsync(() =>
                {
                    Player follower = session.Player!;
                    session.ManagedBudget = null;
                    ai.Update(follower, 1_000);
                    Assert.False(follower.IsAlive, "the released ghost was revived at think " + think);
                });
            }
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    /// <summary>World thread: the socket player <paramref name="masterName"/> invites the hand-driven bot and it accepts.</summary>
    private static (Player Master, Player Bot) Group(WorldTestHost host, string masterName, WorldSession session)
    {
        Player owner = host.World.FindOnlinePlayer(masterName)!;
        Player follower = session.Player!;
        Groups(host).Invite(owner, follower.Name);
        Groups(host).Accept(follower);
        Assert.True(Groups(host).AreInSameGroup(owner.Guid, follower.Guid));
        return (owner, follower);
    }

    private static LootService Loot(WorldTestHost host, Player near)
        => host.WorldServices.GetRequiredService<GameObjectLootFeature>().FindSystem(near.Map!)!.Loot!;

    private static GroupManager Groups(WorldTestHost host) => host.WorldServices.GetRequiredService<SocialFeature>().Context.Groups;
}

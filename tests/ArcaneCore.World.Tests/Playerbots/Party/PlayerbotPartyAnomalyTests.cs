using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Party;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Party;

/// <summary>
/// The party AI's outcome checks (<see cref="PlayerbotPartyAnomalies"/>, subsystem <c>bot-party</c>): each one is proved both ways. The
/// timing rules run on plain players and explicit clocks; the wiring runs a hand-driven <see cref="PlayerbotPartyAI"/> on the real world
/// handlers, its clock moved by <see cref="PlayerbotPartyAI.ClockOffsetMs"/> between thinks so minutes pass in milliseconds.
/// </summary>
public sealed class PlayerbotPartyAnomalyTests
{
    private static string Key(string code) => PlayerbotPartyAnomalies.Key(code);

    // --- the episode clock ---------------------------------------------------------------------------------------------

    [Fact]
    public void AnEpisode_FiresOnce_WhenItHasLastedTheBudget_AndNotBefore()
    {
        var episode = new PlayerbotEpisode();
        Assert.False(episode.Observe(true, 0, 100, 1000));
        Assert.False(episode.Observe(true, 99, 100, 1000));
        Assert.True(episode.Observe(true, 100, 100, 1000));
        Assert.False(episode.Observe(true, 500, 100, 1000)); // once per episode
        Assert.Equal(500, episode.ElapsedMs(500));
    }

    [Fact]
    public void AnEpisode_EndsOnAGoodObservation_AndStartsOverAfterAGap_OrAClockThatRanBackwards()
    {
        var episode = new PlayerbotEpisode();
        Assert.False(episode.Observe(true, 0, 100, 50));
        Assert.False(episode.Observe(false, 40, 100, 50)); // good: over
        Assert.False(episode.Observe(true, 60, 100, 50)); // a new episode from 60
        Assert.False(episode.Observe(true, 200, 100, 50)); // 140 ms of silence: not believed, a new episode from 200
        Assert.False(episode.Observe(true, 250, 100, 50));
        Assert.True(episode.Observe(true, 300, 100, 50), "100 ms of observed badness fires");
        Assert.False(episode.Observe(true, 10, 100, 50)); // the clock ran backwards: new episode
        Assert.Equal(0, episode.ElapsedMs(10));
    }

    // --- follower far behind / on another map (pure) ---------------------------------------------------------------------

    private static Player NewPlayer(string name, int id, uint mapId = 0)
    {
        var character = new CharacterRecord
        {
            Id = id, AccountId = 1, Name = name, Race = 1, Class = 1, Gender = 0, Level = 10, MapId = mapId, ZoneId = 12, X = 0, Y = 0, Z = 80f,
        };
        var appearance = new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400);
        return new Player(character, appearance, new NullSession());
    }

    private sealed class NullSession : IPlayerSession
    {
        public int AccountId => 1;

        public AccountSecurity Security => AccountSecurity.Player;

        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
        }

        public void ProcessWorldPackets(Player player)
        {
        }

        public void Kick()
        {
        }

        public void OnLoggedOut()
        {
        }
    }

    /// <summary>A follower in Follow mode, the leader in the world on the same map, <paramref name="distance"/> yards away, free to teleport.</summary>
    private static PlayerbotFollowFacts Near(float distance = 300f)
        => new(PlayerbotPartyMode.Follow, MasterInWorld: true, SameMap: true, distance, TeleportToLeader: true, MasterMapAllowed: true);

    private static PlayerbotFollowFacts Elsewhere()
        => new(PlayerbotPartyMode.Follow, MasterInWorld: true, SameMap: false, float.NaN, TeleportToLeader: false, MasterMapAllowed: true);

    private static int Observe(PlayerbotFollowWatch watch, Player bot, Player leader, PlayerbotFollowFacts facts, long fromMs, long toMs, long stepMs = 10_000)
    {
        using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(bot);
        for (long t = fromMs; t <= toMs; t += stepMs) watch.Observe(bot, leader, facts, PlayerbotFollowAction.Move, t);
        return cap.Count(Key(PlayerbotPartyAnomalies.FollowerFarBehind)) + (cap.Count(Key(PlayerbotPartyAnomalies.FollowerOtherMap)) * 100);
    }

    [Fact]
    public void AFollowerFarBehindItsLeader_ForTwoMinutes_IsReportedOnce()
    {
        Player bot = NewPlayer("Farbehinda", 9101), leader = NewPlayer("Farleadera", 9102);
        var watch = new PlayerbotFollowWatch();
        Assert.Equal(0, Observe(watch, bot, leader, Near(), 0, 110_000)); // not yet
        using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(bot);
        watch.Observe(bot, leader, Near(), PlayerbotFollowAction.Move, 120_000);
        watch.Observe(bot, leader, Near(), PlayerbotFollowAction.Move, 130_000);
        Assert.Equal(1, cap.Count(Key(PlayerbotPartyAnomalies.FollowerFarBehind)));
        Assert.False(cap.Any(Key(PlayerbotPartyAnomalies.FollowerOtherMap)));
    }

    [Theory]
    [InlineData("near", 0)]
    [InlineData("combat", 1)]
    [InlineData("stay", 2)]
    [InlineData("flying", 3)]
    [InlineData("leader-gone", 4)]
    [InlineData("teleport-distance-raised", 5)]
    [InlineData("just-under", 6)]
    public void AFollowerThatIsNotFarBehind_OrHasAReason_IsNotReported(string scenario, int index)
    {
        Player bot = NewPlayer("Quietfar" + (char)('a' + index), 9110 + index), leader = NewPlayer("Quietleader" + (char)('a' + index), 9120 + index);
        PlayerbotFollowFacts facts = scenario switch
        {
            "near" => Near(60f),
            "combat" => Near() with { InCombat = true },
            "stay" => Near() with { Mode = PlayerbotPartyMode.Stay },
            "flying" => Near() with { MasterFlying = true },
            "leader-gone" => Near() with { MasterInWorld = false },
            // Teleport distance 1000: the follower walks to a leader 300 yards off; it is not left behind until 1020.
            "teleport-distance-raised" => Near() with { TeleportDistance = 1000f },
            _ => Near(PlayerbotFollowWatch.FarThreshold(100f) - 1f), // just under the threshold
        };
        Assert.Equal(0, Observe(new PlayerbotFollowWatch(), bot, leader, facts, 0, 600_000));
    }

    [Fact]
    public void AFarFollowerWhoseObservationsAreFarApart_IsNotBelievedForTheGap()
    {
        Player bot = NewPlayer("Gapfara", 9131), leader = NewPlayer("Gapleadera", 9132);
        var watch = new PlayerbotFollowWatch();
        // Two observations 200 s apart say nothing about the 200 s between them (another driver, a lag spike).
        Assert.Equal(0, Observe(watch, bot, leader, Near(), 0, 400_000, stepMs: 200_000));
    }

    [Fact]
    public void AFollowerOnAnotherMapThanItsLeader_ForTwoMinutes_IsReported_ButNotOnAMapThePolicyForbids()
    {
        Player bot = NewPlayer("Othermapa", 9141), leader = NewPlayer("Othermapleadera", 9142, mapId: 1);
        Assert.Equal(100, Observe(new PlayerbotFollowWatch(), bot, leader, Elsewhere(), 0, 130_000));

        Player policyBot = NewPlayer("Policymapa", 9143);
        Assert.Equal(0, Observe(new PlayerbotFollowWatch(), policyBot, leader, Elsewhere() with { MasterMapAllowed = false }, 0, 600_000));
        Player teleBot = NewPlayer("Teleportmapa", 9144);
        Assert.Equal(0, Observe(new PlayerbotFollowWatch(), teleBot, leader, Elsewhere() with { MasterInWorld = false }, 0, 600_000)); // the leader is between maps
        Player armed = NewPlayer("Samemapa", 9145);
        Assert.Equal(0, Observe(new PlayerbotFollowWatch(), armed, leader, Near(10f), 0, 600_000));
    }

    [Fact]
    public void AResetFollower_ForgetsTheEpisode_SoADeathOrATeleportNeverCountsTowardsTheBudget()
    {
        Player bot = NewPlayer("Resetfara", 9151), leader = NewPlayer("Resetleadera", 9152);
        var watch = new PlayerbotFollowWatch();
        using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(bot);
        for (long t = 0; t < 110_000; t += 10_000) watch.Observe(bot, leader, Near(), PlayerbotFollowAction.Move, t);
        watch.Reset(); // the follower died, or teleported to its leader
        for (long t = 110_000; t < 200_000; t += 10_000) watch.Observe(bot, leader, Near(), PlayerbotFollowAction.Move, t);
        Assert.False(cap.Any(Key(PlayerbotPartyAnomalies.FollowerFarBehind)), cap.Describe());
    }

    // --- follow dropped (pure) -----------------------------------------------------------------------------------------

    private static bool Drop(PlayerbotFollowDropWatch watch, PlayerbotPartyMode mode, bool sameMap, bool combat, float yards, long now, out long droppedMs)
        => watch.Observe(mode, sameMap, combat, Vector3.Zero, new Vector3(yards, 0, 0), now, out droppedMs);

    [Fact]
    public void AFollowThatNeverRan_WhileTheLeaderIsFarAway_IsReportedAfterTheBudget()
    {
        var watch = new PlayerbotFollowDropWatch();
        watch.NoteFollow(0);
        bool fired = false;
        long firedAt = -1;
        for (long t = 10_000; t <= 300_000; t += 10_000)
        {
            if (Drop(watch, PlayerbotPartyMode.Follow, true, false, 100f, t, out _) && !fired)
            {
                fired = true;
                firedAt = t;
            }
        }

        // Silent for 45 s, then bad from the 50 s observation; fires 120 s later.
        Assert.True(fired);
        Assert.Equal(170_000, firedAt);
    }

    [Theory]
    [InlineData("follows", true)]
    [InlineData("near", true)]
    [InlineData("combat", true)]
    [InlineData("stay", true)]
    [InlineData("other-map", true)]
    [InlineData("dropped", false)]
    public void AFollowThatRunsOrNeedsNotRun_IsNotReported(string scenario, bool quiet)
    {
        var watch = new PlayerbotFollowDropWatch();
        watch.NoteFollow(0);
        bool fired = false;
        for (long t = 10_000; t <= 600_000; t += 10_000)
        {
            if (scenario == "follows") watch.NoteFollow(t); // the party AI ran the follower this think
            fired |= scenario switch
            {
                "near" => Drop(watch, PlayerbotPartyMode.Follow, true, false, 20f, t, out _),
                "combat" => Drop(watch, PlayerbotPartyMode.Follow, true, true, 100f, t, out _),
                "stay" => Drop(watch, PlayerbotPartyMode.Stay, true, false, 100f, t, out _),
                "other-map" => Drop(watch, PlayerbotPartyMode.Follow, false, false, 100f, t, out _),
                _ => Drop(watch, PlayerbotPartyMode.Follow, true, false, 100f, t, out _),
            };
        }

        Assert.Equal(!quiet, fired);
    }

    // --- the party AI on the real world ----------------------------------------------------------------------------------------

    private static WorldTestHost Host(Action<PlayerbotOptions>? configure = null)
        => PartyTestHost.Start(options =>
        {
            options.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone;
            configure?.Invoke(options);
        });

    /// <summary>World thread: <paramref name="masterName"/> invites the hand-driven bot and it accepts.</summary>
    private static (Player Master, Player Bot) Group(WorldTestHost host, string masterName, WorldSession session)
    {
        Player owner = host.World.FindOnlinePlayer(masterName)!;
        Player follower = session.Player!;
        Groups(host).Invite(owner, follower.Name);
        Groups(host).Accept(follower);
        Assert.True(Groups(host).AreInSameGroup(owner.Guid, follower.Guid));
        return (owner, follower);
    }

    private static GroupManager Groups(WorldTestHost host) => host.WorldServices.GetRequiredService<SocialFeature>().Context.Groups;

    /// <summary>Make every managed action of <paramref name="session"/> fail (the kick token is cancelled) while its services stay alive.</summary>
    private static void Deafen(WorldSession session)
        => ((System.Threading.CancellationTokenSource)typeof(WorldSession).GetField("_kick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(session)!).Cancel();

    private static async Task Finish(WorldSession session)
    {
        session.Kick();
        await session.ManagedClosed;
    }

    [Fact]
    public async Task APartyBot_ThatCannotReachItsLeader_IsReportedFarBehind_AndNotFollowDropped_WhileOneBesideItIsNeither()
    {
        await using WorldTestHost host = Host(options => options.Party.TeleportToLeader = false);
        await using WorldTestClient master = await host.EnterWorldAsync("PARTYFARM", "Partyfarm");
        WorldSession session = await PartyTestHost.EnterManagedAsync(host, "PARTYFARB", "Partyfarb");
        try
        {
            var ai = new PlayerbotPartyAI(session, PartyTestHost.PlayerbotOptionsOf(host));
            Player bot = session.Player!;
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(bot);

            // Beside the leader (30 yards: it walks, it is not left behind): five thinks over four virtual minutes, silence.
            await host.OnWorldAsync(() =>
            {
                (Player owner, Player follower) = Group(host, "Partyfarm", session);
                Assert.True(ai.Drives(follower));
                owner.Relocate(follower.X + 30f, follower.Y, follower.Z, owner.Orientation, host.World.NowMs);
                for (int think = 0; think < 6; think++)
                {
                    ai.ClockOffsetMs = think * 40_000;
                    session.ManagedBudget = null;
                    ai.Update(follower, 1_000);
                }

                return true;
            });
            Assert.False(cap.Any(Key(PlayerbotPartyAnomalies.FollowerFarBehind)), cap.Describe());
            Assert.False(cap.Any(Key(PlayerbotPartyAnomalies.FollowDropped)), cap.Describe());

            // 300 yards away and no teleport: the follower never gets there. The report comes with the think after the second minute.
            await host.OnWorldAsync(() =>
            {
                Player owner = host.World.FindOnlinePlayer("Partyfarm")!;
                Player follower = session.Player!;
                owner.Relocate(follower.X + 300f, follower.Y, follower.Z, owner.Orientation, host.World.NowMs);
                for (int think = 0; think < 6; think++)
                {
                    ai.ClockOffsetMs = 300_000 + (think * 40_000);
                    session.ManagedBudget = null;
                    ai.Update(follower, 1_000);
                }

                return true;
            });
            Assert.Equal(1, cap.Count(Key(PlayerbotPartyAnomalies.FollowerFarBehind)));
            Assert.False(cap.Any(Key(PlayerbotPartyAnomalies.FollowDropped)), "the follower ran at every think: " + cap.Describe());
        }
        finally
        {
            await Finish(session);
        }
    }

    [Fact]
    public async Task AnInvitationTheBotAnswersButTheServerKeeps_IsReported_AnAnsweredOneIsNot()
    {
        await using WorldTestHost host = Host();
        await using WorldTestClient master = await host.EnterWorldAsync("PARTYINVM", "Partyinvm");
        WorldSession answered = await PartyTestHost.EnterManagedAsync(host, "PARTYINVA", "Partyinva");
        WorldSession deaf = await PartyTestHost.EnterManagedAsync(host, "PARTYINVB", "Partyinvb");
        try
        {
            var answerer = new PlayerbotPartyAI(answered, PartyTestHost.PlayerbotOptionsOf(host));
            var other = new PlayerbotPartyAI(deaf, PartyTestHost.PlayerbotOptionsOf(host));
            using AnomalyCapture okCap = PlayerbotAnomalies.CaptureFor(answered.Player!);
            using AnomalyCapture deafCap = PlayerbotAnomalies.CaptureFor(deaf.Player!);
            await host.OnWorldAsync(() =>
            {
                Player owner = host.World.FindOnlinePlayer("Partyinvm")!;
                // Leashed bots are engaged, so their intake is not throttled to one pass per think interval.
                answerer.Leash(answered.Player!, owner, stay: false);
                other.Leash(deaf.Player!, owner, stay: false);
                Groups(host).Invite(owner, answered.Player!.Name);
                for (int pass = 0; pass < 4; pass++)
                {
                    answerer.ClockOffsetMs = pass * 6_000;
                    answerer.Intake(answered.Player!);
                }

                Assert.True(Groups(host).AreInSameGroup(owner.Guid, answered.Player!.Guid), "the answered invitation formed the group");
                Groups(host).Accept(answered.Player!); // no-op, the group exists

                // The second bot's session is down: its answer is never dispatched, so the invitation stays on the server.
                Groups(host).Invite(owner, deaf.Player!.Name);
                Deafen(deaf);
                for (int pass = 0; pass < 4; pass++)
                {
                    other.ClockOffsetMs = pass * 6_000;
                    other.Intake(deaf.Player!);
                }

                Assert.NotNull(Groups(host).GetInvite(deaf.Player!.Guid));
                return true;
            });
            Assert.False(okCap.Any(Key(PlayerbotPartyAnomalies.InviteUnanswered)), okCap.Describe());
            Assert.Equal(1, deafCap.Count(Key(PlayerbotPartyAnomalies.InviteUnanswered)));
        }
        finally
        {
            await Finish(answered);
            await Finish(deaf);
        }
    }

    private static byte[] StartRoll(ulong source = 0xF110_0000_0000_0001UL, uint slot = 0, uint item = 25)
    {
        var writer = new PacketWriter(16);
        writer.WriteUInt64(source);
        writer.WriteUInt32(slot);
        writer.WriteUInt32(item);
        return writer.ToArray();
    }

    [Fact]
    public async Task ALootRollTheBotCannotVoteOn_IsReported_AVoteThatWasCastIsNot()
    {
        await using WorldTestHost host = Host();
        WorldSession unreadable = await PartyTestHost.EnterManagedAsync(host, "PARTYROLLA", "Partyrolla");
        WorldSession cast = await PartyTestHost.EnterManagedAsync(host, "PARTYROLLB", "Partyrollb");
        WorldSession down = await PartyTestHost.EnterManagedAsync(host, "PARTYROLLC", "Partyrollc");
        try
        {
            using AnomalyCapture unreadableCap = PlayerbotAnomalies.CaptureFor(unreadable.Player!);
            using AnomalyCapture castCap = PlayerbotAnomalies.CaptureFor(cast.Player!);
            using AnomalyCapture downCap = PlayerbotAnomalies.CaptureFor(down.Player!);
            await host.OnWorldAsync(() =>
            {
                unreadable.Send(WorldOpcode.SmsgLootStartRoll, new byte[] { 1, 2, 3 });
                new PlayerbotPartyAI(unreadable, PartyTestHost.PlayerbotOptionsOf(host)).Intake(unreadable.Player!);

                cast.Send(WorldOpcode.SmsgLootStartRoll, StartRoll());
                new PlayerbotPartyAI(cast, PartyTestHost.PlayerbotOptionsOf(host)).Intake(cast.Player!);

                down.Send(WorldOpcode.SmsgLootStartRoll, StartRoll());
                Deafen(down);
                new PlayerbotPartyAI(down, PartyTestHost.PlayerbotOptionsOf(host)).Intake(down.Player!);
                return true;
            });
            Assert.Equal(1, unreadableCap.Count(Key(PlayerbotPartyAnomalies.LootRollUncast)));
            Assert.False(castCap.Any(Key(PlayerbotPartyAnomalies.LootRollUncast)), castCap.Describe());
            Assert.Equal(1, downCap.Count(Key(PlayerbotPartyAnomalies.LootRollUncast)));
        }
        finally
        {
            await Finish(unreadable);
            await Finish(cast);
            await Finish(down);
        }
    }

    private static byte[] ChatLine(ChatType type, ObjectGuid sender, string text)
    {
        var writer = new PacketWriter(32 + text.Length);
        writer.WriteByte((byte)type);
        writer.WriteUInt32((uint)Language.Common);
        writer.WriteUInt64(sender.Value);
        if (type is ChatType.Say or ChatType.Party or ChatType.Yell) writer.WriteUInt64(sender.Value);
        writer.WriteUInt32((uint)text.Length + 1);
        writer.WriteCString(text);
        writer.WriteByte(0);
        return writer.ToArray();
    }

    [Fact]
    public async Task AMastersCommandOnAChannelThatIsNoOrder_IsReported_OnPartyChatItIsObeyed()
    {
        await using WorldTestHost host = Host();
        await using WorldTestClient master = await host.EnterWorldAsync("PARTYCHATM", "Partychatm");
        WorldSession session = await PartyTestHost.EnterManagedAsync(host, "PARTYCHATB", "Partychatb");
        try
        {
            var ai = new PlayerbotPartyAI(session, PartyTestHost.PlayerbotOptionsOf(host));
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(session.Player!);
            await host.OnWorldAsync(() =>
            {
                (Player owner, Player follower) = Group(host, "Partychatm", session);
                Assert.True(ai.Drives(follower));

                session.Send(WorldOpcode.SmsgMessagechat, ChatLine(ChatType.Party, owner.Guid, "stay"));
                ai.Intake(follower);
                Assert.Equal(PlayerbotPartyMode.Stay, ai.Mode); // obeyed
                return true;
            });
            Assert.False(cap.Any(Key(PlayerbotPartyAnomalies.CommandIgnored)), cap.Describe());

            await host.OnWorldAsync(() =>
            {
                Player owner = host.World.FindOnlinePlayer("Partychatm")!;
                session.Send(WorldOpcode.SmsgMessagechat, ChatLine(ChatType.Say, owner.Guid, "follow"));
                ai.Intake(session.Player!);
                Assert.Equal(PlayerbotPartyMode.Stay, ai.Mode); // a /say is no order: still staying
                return true;
            });
            Assert.Equal(1, cap.Count(Key(PlayerbotPartyAnomalies.CommandIgnored)));
        }
        finally
        {
            await Finish(session);
        }
    }

    [Fact]
    public async Task AnAcknowledgementTheBotCannotSend_IsReported()
    {
        await using WorldTestHost host = Host();
        await using WorldTestClient master = await host.EnterWorldAsync("PARTYREPLYM", "Partyreplym");
        WorldSession session = await PartyTestHost.EnterManagedAsync(host, "PARTYREPLYB", "Partyreplyb");
        try
        {
            var ai = new PlayerbotPartyAI(session, PartyTestHost.PlayerbotOptionsOf(host));
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(session.Player!);
            await host.OnWorldAsync(() =>
            {
                (Player owner, Player follower) = Group(host, "Partyreplym", session);
                Assert.True(ai.Drives(follower));
                session.Send(WorldOpcode.SmsgMessagechat, ChatLine(ChatType.Party, owner.Guid, "stay"));
                ai.Intake(follower);
                return true;
            });
            Assert.False(cap.Any(Key(PlayerbotPartyAnomalies.ReplyLost)), "the acknowledgement was sent: " + cap.Describe());

            await host.OnWorldAsync(() =>
            {
                Player owner = host.World.FindOnlinePlayer("Partyreplym")!;
                session.Send(WorldOpcode.SmsgMessagechat, ChatLine(ChatType.Party, owner.Guid, "follow"));
                Deafen(session);
                ai.Intake(session.Player!);
                Assert.Equal(PlayerbotPartyMode.Follow, ai.Mode); // the order was carried out
                return true;
            });
            Assert.Equal(1, cap.Count(Key(PlayerbotPartyAnomalies.ReplyLost)));
        }
        finally
        {
            await Finish(session);
        }
    }

    [Fact]
    public async Task ADeadPartyBot_NotAliveAfterTheRecoveryBudget_IsReported_OneThatWaitsLessIsNot()
    {
        await using WorldTestHost host = Host();
        await using WorldTestClient master = await host.EnterWorldAsync("PARTYDEADM", "Partydeadm");
        WorldSession session = await PartyTestHost.EnterManagedAsync(host, "PARTYDEADB", "Partydeadb");
        try
        {
            var ai = new PlayerbotPartyAI(session, PartyTestHost.PlayerbotOptionsOf(host)) { DeadWaitMs = 0 };
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(session.Player!);
            await host.OnWorldAsync(() =>
            {
                (Player owner, Player follower) = Group(host, "Partydeadm", session);
                Assert.True(ai.Drives(follower));
                owner.Relocate(follower.X + 40f, follower.Y, follower.Z, owner.Orientation, host.World.NowMs);
                follower.Health = 0;
                follower.Map!.Combat.KillPlayer(follower);
                // Ten virtual minutes dead: inside the budget.
                for (int think = 0; think <= 12; think++)
                {
                    ai.ClockOffsetMs = think * 50_000;
                    session.ManagedBudget = null;
                    ai.Update(follower, 1_000);
                }

                Assert.False(follower.IsAlive, "the corpse reclaim delay keeps the ghost dead");
                return true;
            });
            Assert.False(cap.Any(Key(PlayerbotPartyAnomalies.DeathUnrecovered)), cap.Describe());

            await host.OnWorldAsync(() =>
            {
                Player follower = session.Player!;
                for (int think = 13; think <= 20; think++)
                {
                    ai.ClockOffsetMs = think * 50_000;
                    session.ManagedBudget = null;
                    ai.Update(follower, 1_000);
                }

                return true;
            });
            Assert.Equal(1, cap.Count(Key(PlayerbotPartyAnomalies.DeathUnrecovered)));
        }
        finally
        {
            await Finish(session);
        }
    }

    [Fact]
    public async Task ABotThatWaitedOutItsMastersAbsence_IsReported_ToStillBeGrouped_OnlyWhenTheLeaveDidNotTake()
    {
        await using WorldTestHost host = Host();
        WorldSession leaves = await PartyTestHost.EnterManagedAsync(host, "PARTYGONEA", "Partygonea");
        WorldSession stuck = await PartyTestHost.EnterManagedAsync(host, "PARTYGONEB", "Partygoneb");
        WorldTestClient? masterA = null, masterB = null;
        try
        {
            masterA = await host.EnterWorldAsync("PARTYGONEMA", "Partygonema");
            masterB = await host.EnterWorldAsync("PARTYGONEMB", "Partygonemb");
            var leavingAi = new PlayerbotPartyAI(leaves, PartyTestHost.PlayerbotOptionsOf(host));
            var stuckAi = new PlayerbotPartyAI(stuck, PartyTestHost.PlayerbotOptionsOf(host));
            using AnomalyCapture leavesCap = PlayerbotAnomalies.CaptureFor(leaves.Player!);
            using AnomalyCapture stuckCap = PlayerbotAnomalies.CaptureFor(stuck.Player!);
            await host.OnWorldAsync(() =>
            {
                Assert.True(leavingAi.Drives(Group(host, "Partygonema", leaves).Bot));
                Assert.True(stuckAi.Drives(Group(host, "Partygonemb", stuck).Bot));
                return true;
            });
            await masterA.DisposeAsync();
            masterA = null;
            await masterB.DisposeAsync();
            masterB = null;
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Partygonema") is null && host.World.FindOnlinePlayer("Partygonemb") is null,
                "both masters log out");

            await host.OnWorldAsync(() =>
            {
                Assert.NotNull(Groups(host).GetGroup(leaves.Player!.Guid)); // the offline leader's group stands
                int timeoutMs = PartyTestHost.PlayerbotOptionsOf(host).Party.MasterTimeoutSeconds * 1000;
                Deafen(stuck); // its leave is never dispatched
                foreach ((PlayerbotPartyAI ai, Player bot) in new[] { (leavingAi, leaves.Player!), (stuckAi, stuck.Player!) })
                {
                    ai.ClockOffsetMs = 0;
                    Assert.True(ai.Drives(bot), "still waiting for the master");
                    ai.ClockOffsetMs = timeoutMs + 1_000;
                    Assert.False(ai.Drives(bot), "the wait is over");
                }

                Assert.Null(Groups(host).GetGroup(leaves.Player!.Guid));
                Assert.NotNull(Groups(host).GetGroup(stuck.Player!.Guid));
                return true;
            });
            Assert.False(leavesCap.Any(Key(PlayerbotPartyAnomalies.LeaderGoneStillGrouped)), leavesCap.Describe());
            Assert.Equal(1, stuckCap.Count(Key(PlayerbotPartyAnomalies.LeaderGoneStillGrouped)));
        }
        finally
        {
            if (masterA is not null) await masterA.DisposeAsync();
            if (masterB is not null) await masterB.DisposeAsync();
            await Finish(leaves);
            await Finish(stuck);
        }
    }

    [Fact]
    public async Task ABotInAGroupOfBotsOnly_NobodyDrives_IsReportedOrphaned_AfterTheBudget_ButNotWhileAPlayerLeadsIt()
    {
        await using WorldTestHost host = Host();
        await using WorldTestClient master = await host.EnterWorldAsync("PARTYORPHM", "Partyorphm");
        WorldSession orphan = await PartyTestHost.EnterManagedAsync(host, "PARTYORPHA", "Partyorpha");
        WorldSession friend = await PartyTestHost.EnterManagedAsync(host, "PARTYORPHB", "Partyorphb");
        WorldSession led = await PartyTestHost.EnterManagedAsync(host, "PARTYORPHC", "Partyorphc");
        try
        {
            var orphanAi = new PlayerbotPartyAI(orphan, PartyTestHost.PlayerbotOptionsOf(host));
            var ledAi = new PlayerbotPartyAI(led, PartyTestHost.PlayerbotOptionsOf(host));
            using AnomalyCapture orphanCap = PlayerbotAnomalies.CaptureFor(orphan.Player!);
            using AnomalyCapture ledCap = PlayerbotAnomalies.CaptureFor(led.Player!);
            await host.OnWorldAsync(() =>
            {
                Groups(host).Invite(friend.Player!, orphan.Player!.Name);
                Groups(host).Accept(orphan.Player!);
                Assert.True(Groups(host).AreInSameGroup(friend.Player!.Guid, orphan.Player!.Guid));
                Group(host, "Partyorphm", led);
                for (int pass = 0; pass <= 8; pass++)
                {
                    orphanAi.ClockOffsetMs = ledAi.ClockOffsetMs = pass * 40_000;
                    Assert.False(orphanAi.Drives(orphan.Player!), "no real player: the party AI does not drive it");
                    Assert.True(ledAi.Drives(led.Player!));
                }

                return true;
            });
            Assert.Equal(1, orphanCap.Count(Key(PlayerbotPartyAnomalies.LeaderGoneStillGrouped)));
            Assert.False(ledCap.Any(Key(PlayerbotPartyAnomalies.LeaderGoneStillGrouped)), ledCap.Describe());
        }
        finally
        {
            await Finish(orphan);
            await Finish(friend);
            await Finish(led);
        }
    }

    [Fact]
    public async Task ABotInAnUnownedGroupWithAnOfflineMember_LeavesBeforeTheOrphanBudget()
    {
        await using WorldTestHost host = Host();
        WorldSession orphan = await PartyTestHost.EnterManagedAsync(host, "PARTYORPHD", "Partyorphd");
        WorldSession friend = await PartyTestHost.EnterManagedAsync(host, "PARTYORPHE", "Partyorphe");
        try
        {
            var orphanAi = new PlayerbotPartyAI(orphan, PartyTestHost.PlayerbotOptionsOf(host));
            using AnomalyCapture orphanCap = PlayerbotAnomalies.CaptureFor(orphan.Player!);
            ObjectGuid friendGuid = friend.Player!.Guid;
            await host.OnWorldAsync(() =>
            {
                Groups(host).Invite(friend.Player!, orphan.Player!.Name);
                Groups(host).Accept(orphan.Player!);
                Assert.True(Groups(host).AreInSameGroup(friendGuid, orphan.Player!.Guid));
                return true;
            });

            await Finish(friend);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(friendGuid) is null, "friend logs out");

            await host.OnWorldAsync(() =>
            {
                orphanAi.ClockOffsetMs = 0;
                Assert.False(orphanAi.Drives(orphan.Player!));
                orphanAi.ClockOffsetMs = 30_000;
                Assert.False(orphanAi.Drives(orphan.Player!));
                orphanAi.ClockOffsetMs = 65_000;
                Assert.False(orphanAi.Drives(orphan.Player!));
                Assert.Null(Groups(host).GetGroup(orphan.Player!.Guid));
                return true;
            });
            Assert.False(orphanCap.Any(Key(PlayerbotPartyAnomalies.LeaderGoneStillGrouped)), orphanCap.Describe());
        }
        finally
        {
            await Finish(orphan);
        }
    }

    [Fact]
    public async Task AMalformedChatPacket_FiresChatCommandMalformed_WhileMonsterChatAndValidChatDoNot()
    {
        await using WorldTestHost host = Host();
        await using WorldTestClient master = await host.EnterWorldAsync("PARTYMCHM", "Partymchm");
        WorldSession session = await PartyTestHost.EnterManagedAsync(host, "PARTYMCHB", "Partymchb");
        try
        {
            var ai = new PlayerbotPartyAI(session, PartyTestHost.PlayerbotOptionsOf(host));
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(session.Player!);
            await host.OnWorldAsync(() =>
            {
                (Player owner, Player follower) = Group(host, "Partymchm", session);
                Assert.True(ai.Drives(follower));

                // Monster chat has a different layout and must be ignored without an anomaly.
                session.Send(WorldOpcode.SmsgMessagechat, new byte[] { (byte)ChatType.MonsterSay, 1, 2, 3 });
                ai.Intake(follower);

                // Valid party chat must not report malformed.
                session.Send(WorldOpcode.SmsgMessagechat, ChatLine(ChatType.Party, owner.Guid, "follow"));
                ai.Intake(follower);
                return true;
            });
            Assert.False(cap.Any(Key(PlayerbotPartyAnomalies.ChatCommandMalformed)), cap.Describe());

            await host.OnWorldAsync(() =>
            {
                Player follower = session.Player!;
                // Truncated whisper packet (< 18 bytes).
                session.Send(WorldOpcode.SmsgMessagechat, new byte[] { (byte)ChatType.Whisper, 1, 2, 3 });
                ai.Intake(follower);

                // Channel packet with an overlong CString channel name (throws ArgumentOutOfRangeException in PacketReader).
                var badChannel = new byte[PacketReader.MaxCStringBytes + 16];
                badChannel[0] = (byte)ChatType.Channel;
                for (int i = 5; i < badChannel.Length; i++) badChannel[i] = (byte)'A';
                session.Send(WorldOpcode.SmsgMessagechat, badChannel);
                ai.Intake(follower);
                return true;
            });
            Assert.Equal(2, cap.Count(Key(PlayerbotPartyAnomalies.ChatCommandMalformed)));
            Assert.Contains(FaultLedger.Shared.Snapshot(), e => e.Site == "playerbot.party");
        }
        finally
        {
            await Finish(session);
        }
    }
}

using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Playerbots.Opportunities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Opportunities.OpportunityWorld;

namespace ArcaneCore.World.Tests.Playerbots.Progression;

/// <summary>
/// The wait for a gather's loot window (<see cref="PlayerbotCrafting"/>: Herb Gathering, Mining, Skinning, Disenchant). Live
/// (2026-10-16 world.debug.log) ~1000 bots think with a p95 gap of 13-16 s outside combat, so the first think after a 5 s Herb
/// Gathering cast lands is routinely seconds past a fixed 6 s deadline. The SMSG_LOOT_RESPONSE was in the bot's ring all along;
/// the bot must take it, not report <c>bot-progression/craft-failed</c> "gather or disenchant loot timed out". A reply that never
/// comes is still reported. Manual world clock: no wall-clock waits.
/// </summary>
[Collection(PlayerbotCraftingCollection.Name)]
public sealed class PlayerbotGatherLootWaitTests
{
    private static readonly string CraftFailed = PlayerbotAnomalies.Key("progression", "craft-failed");

    /// <summary>A herb node 3 yd east of the bot (inside the 4.5 yd interact range: no walk).</summary>
    private static readonly ChestSpawn Node = new(NodeChest, 7, 3f, 0f);

    private sealed class Rig : IAsyncDisposable
    {
        public required WorldTestHost Host { get; init; }

        public required Bot Bot { get; init; }

        public required PlayerbotCrafting Crafting { get; init; }

        public Player Player => Bot.Player;

        public static async Task<Rig> StartAsync(PlayerbotOptions? options = null)
        {
            WorldTestHost host = Start([Node], out _, manualClock: true);
            Bot bot = await EnterAsync(host, "GATHERBOT", "Gatherone", 1, options ?? new PlayerbotOptions { Enabled = true }, new PlayerbotClaims(8), () => 0);
            var node = new ObjectGuid(ChestGuid(Node));
            Assert.True(await host.World.AdvanceClockUntilAsync(10_000, () => bot.Player.VisibleObjects.Contains(node)), "the herb node never became visible");
            await host.OnWorldAsync(() =>
            {
                Assert.True(bot.Session.Services.GetRequiredService<SpellFeature>().Spellbook.LearnSpell(bot.Player, SlowHerbSpell));
                // 100 against the node's 1: never orange, so the landing roll cannot fail (irand(75, 137) >= 1).
                bot.Player.Skills!.Set(SkillIds.Herbalism, 100, 100);
                return true;
            });
            return new Rig { Host = host, Bot = bot, Crafting = new PlayerbotCrafting(bot.Session, bot.Options) };
        }

        /// <summary>One think of the crafting rung with a fresh action budget; world thread only.</summary>
        public bool Think()
        {
            Bot.Session.ManagedBudget = new ManagedActionBudget(5);
            return Crafting.Update(Player, 500);
        }

        public bool Casting() => Bot.Session.Services.GetRequiredService<SpellFeature>().System.GetState(Player.Guid)?.CurrentCast is not null;

        public async ValueTask DisposeAsync()
        {
            Bot.Brain.Stop();
            Bot.Session.Kick();
            await Bot.Session.ManagedClosed;
            await Host.DisposeAsync();
        }
    }

    /// <summary>Starts the 5 s gather and runs game time until the cast bar has ended (the loot window opened server side).</summary>
    private static async Task CastAndLandAsync(Rig rig)
    {
        Assert.True(await rig.Host.OnWorldAsync(rig.Think), "the bot did not start on the herb node");
        Assert.True(await rig.Host.OnWorldAsync(rig.Casting), "Herb Gathering is not on its cast bar");
        Assert.True(await rig.Host.World.AdvanceClockUntilAsync(6_000, () => !rig.Casting()), "the 5 s cast never landed");
    }

    [Fact]
    public async Task ALootWindowThatOpenedDuringAThinkGap_IsTaken_NotReportedAsATimeout()
    {
        await using Rig rig = await Rig.StartAsync();
        using AnomalyCapture capture = await rig.Host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(rig.Player));

        await CastAndLandAsync(rig);
        // The bot's next think comes 8 s after the cast landed (live think gap p95 13-16 s): 13 s after the cast was sent.
        await rig.Host.World.AdvanceClockAsync(8_000);

        for (int i = 0; i < 6 && await rig.Host.OnWorldAsync(() => rig.Player.Inventory.GetItemCount(GreyItem) == 0); i++)
            await rig.Host.OnWorldAsync(rig.Think);

        Assert.False(capture.Any(CraftFailed), capture.Describe());
        Assert.Equal(1u, await rig.Host.OnWorldAsync(() => rig.Player.Inventory.GetItemCount(GreyItem)));
    }

    /// <summary>
    /// A gather trip holds its own goal kind. Its target is a game object node; held as a Vendor goal it made bot QA read the entry as a creature
    /// ("creature 1617 has no spawn", GitHub #1487) and told the group and the chat the bot was heading to a vendor.
    /// </summary>
    [Fact]
    public async Task AGatherTrip_HoldsTheGatherGoal_WithTheNodeEntryAsTarget()
    {
        await using Rig rig = await Rig.StartAsync();

        Assert.True(await rig.Host.OnWorldAsync(rig.Think), "the bot did not start on the herb node");

        Assert.Equal(PlayerbotGoalKind.Gather, rig.Crafting.Goal);
        Assert.Equal(NodeChest, rig.Crafting.TargetEntry);
    }

    [Fact]
    public async Task AWindowOfAnyLootType_IsTaken_NotOnlyACorpseWindow()
    {
        // Skinning goes out as wire type 2 and Disenchant as 4 (LootTypes.ToWire, vmangos Player::SendLoot Player.cpp:7980-7995); only
        // type 0 (LOOT_NONE) is the refusal reply. The node's real window is replayed with type 2: the bot must still take and release it.
        await using Rig rig = await Rig.StartAsync();
        using AnomalyCapture capture = await rig.Host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(rig.Player));

        await CastAndLandAsync(rig);
        await rig.Host.OnWorldAsync(() =>
        {
            byte[] window = rig.Bot.Session.DrainManagedPackets(WorldOpcode.SmsgLootResponse).Single().Payload;
            Assert.Equal(1, window[8]);
            window[8] = 2;
            rig.Bot.Session.Send(WorldOpcode.SmsgLootResponse, window);
            return true;
        });

        for (int i = 0; i < 6 && await rig.Host.OnWorldAsync(() => rig.Player.Inventory.GetItemCount(GreyItem) == 0); i++)
            await rig.Host.OnWorldAsync(rig.Think);

        Assert.Equal(1u, await rig.Host.OnWorldAsync(() => rig.Player.Inventory.GetItemCount(GreyItem)));
        Assert.False(capture.Any(CraftFailed), capture.Describe());
    }

    [Fact]
    public async Task AFailedCast_IsCastAgain_NotReportedAsATimeout_UntilItFailsMaxGatherCastsTimesInARow()
    {
        // An interrupted cast stands for every cast that ends without its effects (the orange TRY_AGAIN roll above all): no window is
        // coming, so it is no loot timeout, and a player would click the node again.
        await using Rig rig = await Rig.StartAsync();
        using AnomalyCapture capture = await rig.Host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(rig.Player));

        Assert.True(await rig.Host.OnWorldAsync(rig.Think), "the bot did not go for the node");
        for (int attempt = 1; attempt <= PlayerbotCrafting.MaxGatherCasts; attempt++)
        {
            Assert.True(await rig.Host.OnWorldAsync(rig.Casting), $"cast {attempt}: no cast bar");
            await rig.Host.OnWorldAsync(() =>
            {
                rig.Bot.Session.ManagedBudget = new ManagedActionBudget(5);
                return rig.Bot.Session.TryManagedAction(WorldOpcode.CmsgCancelCast, BitConverter.GetBytes(SlowHerbSpell));
            });
            Assert.False(await rig.Host.OnWorldAsync(rig.Casting), $"cast {attempt}: the cancel did not end the cast");
            await rig.Host.World.AdvanceClockAsync(1_000);

            // The think that finds the cast over without its effects: no window is coming.
            await rig.Host.OnWorldAsync(rig.Think);
            if (attempt < PlayerbotCrafting.MaxGatherCasts)
            {
                Assert.False(capture.Any(CraftFailed), $"after failed cast {attempt}: " + capture.Describe());
                Assert.True(await rig.Host.OnWorldAsync(rig.Casting), $"after failed cast {attempt} the bot did not cast at the node again");
            }
        }

        Assert.Equal(1, capture.Count(CraftFailed));
        Assert.Contains($"failed {PlayerbotCrafting.MaxGatherCasts} times in a row", capture.Notices.Single(n => n.Key == CraftFailed).Detail, StringComparison.Ordinal);
        Assert.False(await rig.Host.OnWorldAsync(rig.Casting), "the bot went on casting at a node it had given up on");
    }

    [Fact]
    public async Task AWindowThatLeavesTheRingBeforeTheNextThink_IsTakenByTheBrain_NotReportedAsATimeout()
    {
        // GitHub #774. The window opens in the tick the cast lands, but the crafting rung only reads it on the bot's next think that reaches
        // that rung: seconds later (live think gap p95 13-16 s), and only when no higher rung (policy, quests, a fight) owns that think.
        // Meanwhile the reply sits in the bounded received-packet ring (128 packets), which every nearby SMSG_SPELL_START/GO also fills,
        // and which other readers drain (the corpse loot after a fight discards every SMSG_LOOT_RESPONSE that is not its corpse's). Here
        // the ring fills before the next think. The brain must take its gather window on the ticks between thinks, as it takes a corpse's.
        const int ThinkMs = 15_000;
        await using Rig rig = await Rig.StartAsync(new PlayerbotOptions { Enabled = true, ThinkIntervalMs = ThinkMs });
        using AnomalyCapture capture = await rig.Host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(rig.Player));
        PlayerbotBrain brain = rig.Bot.Brain;
        bool Tick(uint elapsedMs)
        {
            rig.Bot.Session.ManagedBudget = new ManagedActionBudget(5);
            brain.Update(elapsedMs);
            return true;
        }

        // The think that goes for the node: the bot stands 3 yd from it, so it casts at once.
        await rig.Host.OnWorldAsync(() => Tick(ThinkMs));
        Assert.True(await rig.Host.OnWorldAsync(rig.Casting), "the brain did not start Herb Gathering on the node");

        // The ticks between thinks while the 5 s cast bar runs and lands.
        for (int i = 0; i < 70 && await rig.Host.OnWorldAsync(rig.Casting); i++)
        {
            await rig.Host.World.AdvanceClockAsync(100);
            await rig.Host.OnWorldAsync(() => Tick(100));
        }

        Assert.False(await rig.Host.OnWorldAsync(rig.Casting), "the 5 s cast never landed");
        for (int i = 0; i < 5; i++)
        {
            await rig.Host.World.AdvanceClockAsync(100);
            await rig.Host.OnWorldAsync(() => Tick(100));
        }

        // Before the next think, the casts of the creatures and players around fill the ring past its 128 packets.
        await rig.Host.OnWorldAsync(() =>
        {
            for (int i = 0; i < 160; i++)
                rig.Bot.Session.Send(WorldOpcode.SmsgSpellGo, new byte[24]);
            return true;
        });
        await rig.Host.World.AdvanceClockAsync(8_000);
        for (int i = 0; i < 3; i++)
            await rig.Host.OnWorldAsync(() => Tick(ThinkMs));

        Assert.False(capture.Any(CraftFailed), capture.Describe());
        Assert.Equal(1u, await rig.Host.OnWorldAsync(() => rig.Player.Inventory.GetItemCount(GreyItem)));
    }

    [Fact]
    public async Task ALootReplyThatNeverComes_IsStillReported_OnceTheCastAndItsGraceAreOver()
    {
        await using Rig rig = await Rig.StartAsync();
        using AnomalyCapture capture = await rig.Host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(rig.Player));

        await CastAndLandAsync(rig);
        // The reply is lost (as a reader that drains and discards the loot replies would lose it).
        Assert.NotEmpty(await rig.Host.OnWorldAsync(() => rig.Bot.Session.DrainManagedPackets(WorldOpcode.SmsgLootResponse)));

        // Within the grace after the cast bar: still waiting, nothing reported.
        await rig.Host.OnWorldAsync(rig.Think);
        Assert.False(capture.Any(CraftFailed), capture.Describe());

        await rig.Host.World.AdvanceClockAsync(8_000);
        await rig.Host.OnWorldAsync(rig.Think);
        Assert.Equal(1, capture.Count(CraftFailed));
        Assert.Contains($"for target {new ObjectGuid(ChestGuid(Node))}", capture.Notices.Single(n => n.Key == CraftFailed).Detail, StringComparison.Ordinal);

        // Reported once: the wait is over.
        await rig.Host.OnWorldAsync(rig.Think);
        Assert.Equal(1, capture.Count(CraftFailed));
    }

    private static ArcaneCore.Game.GameObjects.GameObject NodeOf(Rig rig)
        => (ArcaneCore.Game.GameObjects.GameObject)rig.Player.Map!.FindObject(new ObjectGuid(ChestGuid(Node)))!;

    private static int ViewersOf(Rig rig) => NodeOf(rig).Loot?.Viewers.Count ?? 0;

    private static byte[] GatherCastPacket(Rig rig)
    {
        var writer = new PacketWriter(15);
        writer.WriteUInt32(SlowHerbSpell);
        writer.WriteUInt16((ushort)ArcaneCore.Game.Spells.SpellCastTargetFlags.GameObject);
        writer.WritePackedGuid(ChestGuid(Node));
        return writer.ToArray();
    }

    [Fact]
    public async Task ALootWindowDrainedBySomeoneElse_IsReleasedByTheTimeout_SoTheNodeIsNotLeftInUse()
    {
        // Live (#1468): the reply was drained elsewhere, the bot timed out and walked off, and the server kept the window open: the node
        // stayed "in use" (ChestInUse for every later cast), never despawned or respawned.
        await using Rig rig = await Rig.StartAsync();
        await CastAndLandAsync(rig);
        Assert.Equal(1, await rig.Host.OnWorldAsync(() => ViewersOf(rig)));
        Assert.NotEmpty(await rig.Host.OnWorldAsync(() => rig.Bot.Session.DrainManagedPackets(WorldOpcode.SmsgLootResponse)));

        await rig.Host.World.AdvanceClockAsync(8_000);
        await rig.Host.OnWorldAsync(rig.Think); // the timeout
        await rig.Host.World.AdvanceClockAsync(200);

        Assert.Equal(0, await rig.Host.OnWorldAsync(() => ViewersOf(rig)));
    }

    [Fact]
    public async Task TheTimeoutRelease_ClosesTheWindow_EvenWhenTheManagedActionBudgetIsSpent()
    {
        // The release used to be a managed CMSG_LOOT_RELEASE whose refusal (a spent budget) was ignored, dropping it for good.
        await using Rig rig = await Rig.StartAsync();
        await CastAndLandAsync(rig);
        Assert.Equal(1, await rig.Host.OnWorldAsync(() => ViewersOf(rig)));
        Assert.NotEmpty(await rig.Host.OnWorldAsync(() => rig.Bot.Session.DrainManagedPackets(WorldOpcode.SmsgLootResponse)));

        await rig.Host.World.AdvanceClockAsync(8_000);
        await rig.Host.OnWorldAsync(() =>
        {
            rig.Bot.Session.ManagedBudget = new ManagedActionBudget(0);
            Assert.False(rig.Bot.Session.TryManagedAction(WorldOpcode.CmsgLootRelease, new byte[8]), "the budget was not spent");
            rig.Crafting.UpdateGatherLoot(rig.Player); // the timeout, with every managed action refused
            return true;
        });

        Assert.Equal(0, await rig.Host.OnWorldAsync(() => ViewersOf(rig)));
        Assert.Null(await rig.Host.OnWorldAsync(() => OpenLootOf(rig)));
    }

    [Fact]
    public async Task TheTimeoutRelease_LeavesAWindowOfAnotherSourceOpen()
    {
        // The release is for the gather's own window: a window opened on another source during the wait (a corpse) is not closed by it.
        await using Rig rig = await Rig.StartAsync();
        await CastAndLandAsync(rig);
        Assert.Equal(1, await rig.Host.OnWorldAsync(() => ViewersOf(rig)));
        Assert.NotEmpty(await rig.Host.OnWorldAsync(() => rig.Bot.Session.DrainManagedPackets(WorldOpcode.SmsgLootResponse)));
        await rig.Host.OnWorldAsync(() =>
        {
            typeof(PlayerbotCrafting).GetField("_gatheringTarget", Private)!.SetValue(rig.Crafting, new ObjectGuid(ChestGuid(Node) + 1));
            return true;
        });

        await rig.Host.World.AdvanceClockAsync(8_000);
        await rig.Host.OnWorldAsync(() => rig.Crafting.UpdateGatherLoot(rig.Player));

        // The timeout really ran (the wait ended and the other target is backed off), and it left the node's window alone.
        Assert.False(await rig.Host.OnWorldAsync(() => Field<bool>(rig.Crafting, "_waitingForGatherLoot")));
        Assert.True(await rig.Host.OnWorldAsync(() => Field<Dictionary<ulong, uint>>(rig.Crafting, "_refusedUntil").ContainsKey(ChestGuid(Node) + 1)));
        Assert.Equal(1, await rig.Host.OnWorldAsync(() => ViewersOf(rig)));
        Assert.NotNull(await rig.Host.OnWorldAsync(() => OpenLootOf(rig)));
    }

    [Fact]
    public async Task TheTimeoutRelease_IsSkippedWhileAQuestSettlementIsPending()
    {
        // The client path drops requests while a settlement freezes the character; the bot's server-side release follows it (a later mount step still closes the window).
        await using Rig rig = await Rig.StartAsync();
        await CastAndLandAsync(rig);
        Assert.NotEmpty(await rig.Host.OnWorldAsync(() => rig.Bot.Session.DrainManagedPackets(WorldOpcode.SmsgLootResponse)));
        var operation = Guid.NewGuid();
        Assert.True(await rig.Host.OnWorldAsync(() => rig.Player.BeginQuestSettlement(operation)));

        await rig.Host.World.AdvanceClockAsync(8_000);
        await rig.Host.OnWorldAsync(() => rig.Crafting.UpdateGatherLoot(rig.Player));
        // The timeout really ran (the wait ended, the node is backed off) and the release was skipped.
        Assert.False(await rig.Host.OnWorldAsync(() => Field<bool>(rig.Crafting, "_waitingForGatherLoot")));
        Assert.True(await rig.Host.OnWorldAsync(() => Field<Dictionary<ulong, uint>>(rig.Crafting, "_refusedUntil").ContainsKey(ChestGuid(Node))));
        Assert.Equal(1, await rig.Host.OnWorldAsync(() => ViewersOf(rig)));

        await rig.Host.OnWorldAsync(() => rig.Player.EndQuestSettlement(operation));
        await rig.Host.OnWorldAsync(() => { MountStep(rig); return true; });
        Assert.Equal(0, await rig.Host.OnWorldAsync(() => ViewersOf(rig)));
    }

    [Fact]
    public async Task AMountStep_LeavesTheWindowOpen_WhileAQuestSettlementIsPending()
    {
        await using Rig rig = await Rig.StartAsync();
        await CastAndLandAsync(rig);
        Assert.Equal(1, await rig.Host.OnWorldAsync(() => ViewersOf(rig)));
        var operation = Guid.NewGuid();
        Assert.True(await rig.Host.OnWorldAsync(() => rig.Player.BeginQuestSettlement(operation)));

        await rig.Host.OnWorldAsync(() => { MountStep(rig); return true; });

        Assert.Equal(1, await rig.Host.OnWorldAsync(() => ViewersOf(rig)));
        await rig.Host.OnWorldAsync(() => rig.Player.EndQuestSettlement(operation));

        // The settlement skipped the flag clear too: once it ends, the next mount step closes the window.
        await rig.Host.OnWorldAsync(() => { MountStep(rig); return true; });
        Assert.Equal(0, await rig.Host.OnWorldAsync(() => ViewersOf(rig)));
    }

    private static readonly System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

    private static T Field<T>(PlayerbotCrafting crafting, string name)
        => (T)typeof(PlayerbotCrafting).GetField(name, Private)!.GetValue(crafting)!;

    [Theory]
    [InlineData(1, true)]
    [InlineData(64, false)]
    public async Task AFailedCast_ClearsTheNonDiceStreaksOnlyWhenTheCastTableIsFull(int tracked, bool keptStreaksSurvive)
    {
        // Pins the braces of the Count >= 64 reset in OnLootCastFailed: the non-dice set is cleared with the cast table, not on every failure.
        await using Rig rig = await Rig.StartAsync();
        await rig.Host.OnWorldAsync(() =>
        {
            var casts = Field<Dictionary<ulong, int>>(rig.Crafting, "_failedLootCasts");
            var nonDice = Field<HashSet<ulong>>(rig.Crafting, "_failedLootNonDice");
            for (ulong key = 1; key <= (ulong)tracked; key++)
                casts[key] = 1;
            nonDice.Add(1);
            typeof(PlayerbotCrafting).GetField("_gatheringTarget", Private)!.SetValue(rig.Crafting, new ObjectGuid(ChestGuid(Node)));
            typeof(PlayerbotCrafting).GetMethod("OnLootCastFailed", Private)!.Invoke(rig.Crafting, [rig.Player]);

            Assert.Equal(keptStreaksSurvive, nonDice.Contains(1));
            Assert.Equal(keptStreaksSurvive ? tracked + 1 : 1, casts.Count);
            return true;
        });
    }

    [Fact]
    public async Task ANodeAnotherPlayerHasOpen_IsNeverPicked_AndIsPickedOnceItIsFree()
    {
        await using Rig rig = await Rig.StartAsync();
        Bot other = await EnterAsync(rig.Host, "OTHERGATHER", "Gathertwo", 1, new PlayerbotOptions { Enabled = true }, new PlayerbotClaims(8), () => 0);
        int casts = 0;
        await rig.Host.OnWorldAsync(() =>
        {
            var node = NodeOf(rig);
            var bag = new ArcaneCore.Game.Loot.LootBag(node.Guid, ArcaneCore.Game.Loot.LootSourceKind.GameObject, ArcaneCore.Game.Loot.LootType.Corpse) { Gold = 1 };
            bag.Viewers.Add(other.Player); // another looter holds the node
            node.Loot = bag;
            rig.Bot.Session.ManagedDispatchObserver = op => { if (op == WorldOpcode.CmsgCastSpell) casts++; };
            rig.Think();
            return true;
        });
        Assert.Equal(0, casts);

        await rig.Host.OnWorldAsync(() =>
        {
            NodeOf(rig).Loot!.Viewers.Clear();
            rig.Think();
            return true;
        });
        Assert.Equal(1, casts);
        other.Brain.Stop();
        other.Session.Kick();
        await other.Session.ManagedClosed;
    }

    [Fact]
    public async Task ANodeOnlyTheBotItselfHasOpen_IsStillPicked()
    {
        await using Rig rig = await Rig.StartAsync();
        int casts = 0;
        await rig.Host.OnWorldAsync(() =>
        {
            var node = NodeOf(rig);
            var bag = new ArcaneCore.Game.Loot.LootBag(node.Guid, ArcaneCore.Game.Loot.LootSourceKind.GameObject, ArcaneCore.Game.Loot.LootType.Corpse) { Gold = 1 };
            bag.Viewers.Add(rig.Player);
            node.Loot = bag;
            rig.Bot.Session.ManagedDispatchObserver = op => { if (op == WorldOpcode.CmsgCastSpell) casts++; };
            rig.Think();
            return true;
        });
        Assert.Equal(1, casts);
    }

    [Fact]
    public async Task ATurnOnlyMovementBlock_KeepsTheNodeWindowOpen()
    {
        await using Rig rig = await Rig.StartAsync();
        await CastAndLandAsync(rig);
        Assert.Equal(1, await rig.Host.OnWorldAsync(() => ViewersOf(rig)));

        Assert.True(await rig.Host.OnWorldAsync(() =>
        {
            MovementInfo turning = rig.Player.Movement;
            turning.Flags |= MovementFlags.TurnLeft; // not MOVEFLAG_MASK_MOVING; position unchanged
            var writer = new PacketWriter(64);
            turning.Write(writer);
            rig.Bot.Session.ManagedBudget = new ManagedActionBudget(5);
            return rig.Bot.Session.TryManagedAction(WorldOpcode.MsgMoveStartTurnLeft, writer.AsMemory().ToArray());
        }));

        // The handler provably ran: the turn flag is on the player now.
        Assert.NotEqual(MovementFlags.None, await rig.Host.OnWorldAsync(() => rig.Player.Movement.Flags & MovementFlags.TurnLeft));
        Assert.Equal(1, await rig.Host.OnWorldAsync(() => ViewersOf(rig)));

        // Revert guard: a MASK_MOVING block through the same path does close it, so "open" above is the turn exclusion, not a missing release.
        await rig.Host.OnWorldAsync(() =>
        {
            MovementInfo moving = rig.Player.Movement;
            moving.Flags = (moving.Flags & ~MovementFlags.TurnLeft) | MovementFlags.Forward;
            moving.X -= 0.5f;
            var writer = new PacketWriter(64);
            moving.Write(writer);
            rig.Bot.Session.ManagedBudget = new ManagedActionBudget(5);
            return rig.Bot.Session.TryManagedAction(WorldOpcode.MsgMoveStartForward, writer.AsMemory().ToArray());
        });
        Assert.Equal(0, await rig.Host.OnWorldAsync(() => ViewersOf(rig)));
    }

    [Fact]
    public async Task ACastRefusedChestInUse_BacksTheNodeOffAtOnce_WithoutACraftFailedReport()
    {
        // ChestInUse is raised where the cast lands (GatheringSpells: the non-strict check), after the 5 s bar.
        await using Rig rig = await Rig.StartAsync();
        using AnomalyCapture capture = await rig.Host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(rig.Player));
        int casts = 0;
        await rig.Host.OnWorldAsync(() =>
        {
            rig.Bot.Session.ManagedDispatchObserver = op => { if (op == WorldOpcode.CmsgCastSpell) casts++; };
            return rig.Think();
        });
        Assert.Equal(1, casts);
        Assert.True(await rig.Host.OnWorldAsync(rig.Casting), "the bot did not start on the node");

        // Another looter takes the node while the bar runs: the landing is refused with ChestInUse.
        await rig.Host.OnWorldAsync(() =>
        {
            NodeOf(rig).Flags |= ArcaneCore.Game.GameObjects.GameObjectFlags.InUse;
            return true;
        });
        Assert.True(await rig.Host.World.AdvanceClockUntilAsync(6_000, () => !rig.Casting()), "the cast never ended");
        await rig.Host.OnWorldAsync(() =>
        {
            NodeOf(rig).Flags &= ~ArcaneCore.Game.GameObjects.GameObjectFlags.InUse; // free again: the back-off, not the flag, holds the bot
            return true;
        });

        for (int i = 0; i < 4; i++)
        {
            await rig.Host.OnWorldAsync(rig.Think);
            await rig.Host.World.AdvanceClockAsync(1_000);
        }

        Assert.Equal(1, casts);
        Assert.False(await rig.Host.OnWorldAsync(rig.Casting), "the bot cast again at the node it had just been refused");
        Assert.False(capture.Any(CraftFailed), capture.Describe());
    }

    [Fact]
    public async Task AGatherersMovement_ClosesItsOpenNodeWindow_ThroughTheMovementHandler()
    {
        // vmangos HandleMoverRelocation (MovementHandler.cpp:1106-1110).
        await using Rig rig = await Rig.StartAsync();
        await CastAndLandAsync(rig);
        Assert.Equal(1, await rig.Host.OnWorldAsync(() => ViewersOf(rig)));

        await rig.Host.OnWorldAsync(() =>
        {
            MovementInfo moving = rig.Player.Movement;
            moving.Flags |= MovementFlags.Forward;
            moving.X -= 0.5f;
            var writer = new PacketWriter(64);
            moving.Write(writer);
            rig.Bot.Session.ManagedBudget = new ManagedActionBudget(5);
            return rig.Bot.Session.TryManagedAction(WorldOpcode.MsgMoveStartForward, writer.AsMemory().ToArray());
        });

        Assert.Equal(0, await rig.Host.OnWorldAsync(() => ViewersOf(rig)));
        Assert.Equal(UnitFlags.None, await rig.Host.OnWorldAsync(() => rig.Player.UnitFlags & UnitFlags.Looting));
    }

    [Fact]
    public async Task ACastRequest_ClosesTheCastersOpenWindow_SoItsOwnStaleWindowDoesNotRefuseIt()
    {
        // vmangos@0e3ff01 HandleCastSpellOpcode (SpellHandler.cpp:301-306): casting spells interrupts looting.
        await using Rig rig = await Rig.StartAsync();
        await CastAndLandAsync(rig);
        Assert.Equal(1, await rig.Host.OnWorldAsync(() => ViewersOf(rig)));

        await rig.Host.OnWorldAsync(() =>
        {
            rig.Bot.Session.ManagedBudget = new ManagedActionBudget(5);
            return rig.Bot.Session.TryManagedAction(WorldOpcode.CmsgCastSpell, GatherCastPacket(rig));
        });

        Assert.Equal(0, await rig.Host.OnWorldAsync(() => ViewersOf(rig)));
        Assert.True(await rig.Host.OnWorldAsync(rig.Casting), "the cast was refused against the caster's own stale window");
    }

    private static void MountStep(Rig rig)
    {
        rig.Bot.Session.ManagedBudget = new ManagedActionBudget(5);
        var route = new PlayerbotRoute(
            [new System.Numerics.Vector3(rig.Player.X, rig.Player.Y, rig.Player.Z), new System.Numerics.Vector3(rig.Player.X + 200f, rig.Player.Y, rig.Player.Z)], 200f);
        PlayerbotMount.BeforeStep(rig.Bot.Session, rig.Player, route, new PlayerbotOptions { Enabled = true });
    }

    [Fact]
    public async Task ARouteStepsMountStep_ReleasesTheOpenNodeWindow_NotJustTheFlag()
    {
        // The mount step used to clear UnitFlags.Looting and leave the bag listing the bot, which switched off both release triggers
        // (they gate on the flag) and left the node in use for everyone.
        await using Rig rig = await Rig.StartAsync();
        await CastAndLandAsync(rig);
        Assert.Equal(1, await rig.Host.OnWorldAsync(() => ViewersOf(rig)));

        await rig.Host.OnWorldAsync(() => { MountStep(rig); return true; });

        Assert.Equal(0, await rig.Host.OnWorldAsync(() => ViewersOf(rig)));
        Assert.Null(await rig.Host.OnWorldAsync(() => OpenLootOf(rig)));
        Assert.Equal(UnitFlags.None, await rig.Host.OnWorldAsync(() => rig.Player.UnitFlags & UnitFlags.Looting));
    }

    [Fact]
    public async Task AGatherCastAfterARouteStep_IsNotRefusedAgainstTheBotsOwnStaleWindow()
    {
        await using Rig rig = await Rig.StartAsync();
        await CastAndLandAsync(rig);
        await rig.Host.OnWorldAsync(() => { MountStep(rig); return true; });
        await rig.Host.OnWorldAsync(() => rig.Bot.Session.DrainManagedPackets(WorldOpcode.SmsgLootResponse));

        await rig.Host.OnWorldAsync(() =>
        {
            rig.Bot.Session.ManagedBudget = new ManagedActionBudget(5);
            return rig.Bot.Session.TryManagedAction(WorldOpcode.CmsgCastSpell, GatherCastPacket(rig));
        });
        Assert.True(await rig.Host.OnWorldAsync(rig.Casting), "the cast did not start");
        Assert.True(await rig.Host.World.AdvanceClockUntilAsync(6_000, () => !rig.Casting()), "the 5 s cast never landed");

        // Refused with CHEST_IN_USE against its own viewer, the landing would open no window.
        Assert.NotEmpty(await rig.Host.OnWorldAsync(() => rig.Bot.Session.DrainManagedPackets(WorldOpcode.SmsgLootResponse)));
    }

    private static byte[] CastPacket(uint spellId)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt32(spellId);
        writer.WriteUInt16(0); // no targets
        return writer.ToArray();
    }

    private static ArcaneCore.Game.Loot.LootBag? OpenLootOf(Rig rig)
        => rig.Bot.Session.Services.GetRequiredService<ArcaneCore.World.GameObjects.GameObjectLootFeature>().FindSystem(rig.Player.Map!)?.Loot?.OpenLootOf(rig.Player);

    [Theory]
    [InlineData(0u)]
    [InlineData(HerbSpell)]
    public async Task ACastRequestTheServerRefuses_LeavesTheCastersOpenWindowOpen(uint spellId)
    {
        // vmangos@0e3ff01 HandleCastSpellOpcode (SpellHandler.cpp:283-306): an unknown spell (0 is in no store) and a spell the player does not know
        // (HerbSpell exists but the bot only learned SlowHerbSpell) return before the loot release.
        await using Rig rig = await Rig.StartAsync();
        await CastAndLandAsync(rig);
        Assert.Equal(1, await rig.Host.OnWorldAsync(() => ViewersOf(rig)));

        await rig.Host.OnWorldAsync(() =>
        {
            rig.Bot.Session.ManagedBudget = new ManagedActionBudget(5);
            return rig.Bot.Session.TryManagedAction(WorldOpcode.CmsgCastSpell, CastPacket(spellId));
        });

        Assert.NotNull(await rig.Host.OnWorldAsync(() => OpenLootOf(rig)));
        Assert.Equal(1, await rig.Host.OnWorldAsync(() => ViewersOf(rig)));
        Assert.Equal(UnitFlags.Looting, await rig.Host.OnWorldAsync(() => rig.Player.UnitFlags & UnitFlags.Looting));

        // Revert guard: a request the server accepts does close the same window, so "left open" above is the refusal, not a missing release.
        await rig.Host.OnWorldAsync(() =>
        {
            rig.Bot.Session.ManagedBudget = new ManagedActionBudget(5);
            return rig.Bot.Session.TryManagedAction(WorldOpcode.CmsgCastSpell, GatherCastPacket(rig));
        });
        Assert.Equal(0, await rig.Host.OnWorldAsync(() => ViewersOf(rig)));
    }

    [Fact]
    public async Task LargeBoundingRadiusNodeBeyondCentreReach_IsNotCastFromAfar_AndDespawnedNodeWaitDoesNotReportTimeout()
    {
        await using Rig rig = await Rig.StartAsync();

        bool castAttemptedFromAfar = false;
        await rig.Host.OnWorldAsync(() =>
        {
            Player player = rig.Player;
            var nodeGuid = new ObjectGuid(ChestGuid(Node));
            var node = (ArcaneCore.Game.GameObjects.GameObject)player.Map!.FindObject(nodeGuid)!;
            node.ReplaceTemplate(new ArcaneCore.Kernel.WorldData.GameObjects.GameObjectTemplate
            {
                Entry = node.Entry,
                Type = (uint)ArcaneCore.Game.GameObjects.GameObjectType.Chest,
                Name = node.Template.Name,
                Size = 4.0f,
                Data = node.Template.Data,
            });
            // Place at 4.8 yd centre-to-centre (> GatherInteractYards 4.5f, even though DistanceTo(player) < 4.5f due to Size=4.0f bounding radius).
            node.SetPosition(player.X + 4.8f, player.Y, player.Z, 0f);
            Assert.True(node.DistanceTo(player) < 4.5f);

            int castPackets = 0;
            rig.Bot.Session.ManagedDispatchObserver = op =>
            {
                if (op == WorldOpcode.CmsgCastSpell) castPackets++;
            };

            rig.Think();
            castAttemptedFromAfar = castPackets > 0;
            rig.Bot.Session.ManagedDispatchObserver = null;
            node.SetPosition(player.X + 3.0f, player.Y, player.Z, 0f);
            return true;
        });

        Assert.False(castAttemptedFromAfar);
        await rig.Host.World.AdvanceClockAsync(61_000);

        using AnomalyCapture capture = await rig.Host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(rig.Player));
        await CastAndLandAsync(rig);
        await rig.Host.OnWorldAsync(() =>
        {
            _ = rig.Bot.Session.DrainManagedPackets(WorldOpcode.SmsgLootResponse);
            var node = (ArcaneCore.Game.GameObjects.GameObject)rig.Player.Map!.FindObject(new ObjectGuid(ChestGuid(Node)))!;
            rig.Player.Map!.RemoveObject(node);
            return true;
        });
        await rig.Host.World.AdvanceClockAsync(8_000);
        await rig.Host.OnWorldAsync(rig.Think);
        Assert.False(capture.Any(CraftFailed), capture.Describe());
    }
}

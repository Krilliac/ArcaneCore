using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
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
}

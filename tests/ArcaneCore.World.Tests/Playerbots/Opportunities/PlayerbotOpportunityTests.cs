using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Protocol;
using System.Numerics;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.World.Tests.GameObjects;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Opportunities.OpportunityWorld;

namespace ArcaneCore.World.Tests.Playerbots.Opportunities;

/// <summary>
/// The opportunity goal (<see cref="PlayerbotOpportunities"/>) over a real world: chests with real loot tables, locks and items, real
/// guards, the real risk model and the real game object use. The expected values are derived from the documented rules
/// (docs/areas/playbots-opportunities.md): an unguarded find worth more than <c>MinValue</c> is taken; a pack is cleared one pull at a time;
/// elites three levels above the bot and any guard five levels above it are refused; a failed find is set aside 60 s, doubling to 300 s.
/// </summary>
public sealed class PlayerbotOpportunityTests
{
    private static readonly ChestSpawn Free = new(FreeChest, 1, 3f, 0f);

    private static string Key(string code) => PlayerbotAnomalies.Key("opportunity", code);

    private sealed class Rig : IAsyncDisposable
    {
        public WorldTestHost Host = null!;
        public GameObjectTestContext Context = null!;
        public Bot Bot = null!;
        public uint Clock = 1_000_000;
        public PlayerbotClaims Claims = new(64);

        public Player Player => Bot.Player;

        public static async Task<Rig> StartAsync(IEnumerable<ChestSpawn> chests, byte playerClass = 1, Action<PlayerbotOptions>? tweak = null)
        {
            ChestSpawn[] spawns = [.. chests];
            var rig = new Rig { Host = OpportunityWorld.Start(spawns, out GameObjectTestContext context), Context = context };
            var options = new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500 };
            tweak?.Invoke(options);
            rig.Bot = await EnterAsync(rig.Host, "OPPBOT" + playerClass, "Oppone", playerClass, options, rig.Claims, () => rig.Clock);
            foreach (ChestSpawn spawn in spawns)
                await rig.Host.WaitForWorldAsync(() => rig.Player.VisibleObjects.Contains(new ObjectGuid(ChestGuid(spawn))), "chest " + spawn.Entry + " visible");
            return rig;
        }

        public Task<T> World<T>(Func<T> action) => Host.OnWorldAsync(action);

        public GameObject ChestOf(ChestSpawn spawn) => (GameObject)Player.Map!.FindObject(new ObjectGuid(ChestGuid(spawn)))!;

        public async ValueTask DisposeAsync()
        {
            Bot.Brain.Stop();
            Bot.Session.Kick();
            await Bot.Session.ManagedClosed;
            await Host.DisposeAsync();
        }
    }

    /// <summary>Steps the controller (real time passing for the server's replies) until <paramref name="done"/> or the step budget ends.</summary>
    private static async Task<bool> RunAsync(Rig rig, Func<bool> done, int steps = 60)
    {
        for (int i = 0; i < steps; i++)
        {
            if (await rig.World(() => { rig.Bot.Step(); return done(); })) return true;
            await Task.Delay(25);
        }

        return false;
    }

    /// <summary>The loot actions the controller queued, played to the server the way the brain's loot queue does.</summary>
    private static async Task PlayLootAsync(Rig rig)
        => await rig.World(() =>
        {
            foreach ((WorldOpcode opcode, byte[] payload) in rig.Bot.Sink)
            {
                rig.Bot.Session.ManagedBudget = new ManagedActionBudget(5);
                Assert.True(rig.Bot.Session.TryManagedAction(opcode, payload));
            }

            rig.Bot.Sink.Clear();
            return true;
        });

    private static async Task<Creature> AddGuardAsync(Rig rig, uint low, float dx, float dy, byte level = 1, uint rank = 0, uint health = 20)
    {
        Creature guard = await rig.World(() => AddCreature(rig.Host, rig.Player, low, dx, dy, level, rank, health));
        await rig.Host.WaitForWorldAsync(() => rig.Player.VisibleObjects.Contains(guard.Guid), "guard " + low + " visible");
        return guard;
    }

    private static Task Kill(Rig rig, Creature guard) => rig.World(() =>
    {
        rig.Player.Map!.Combat.DealDamage(rig.Player, guard, 100_000);
        Assert.False(guard.IsAlive);
        return true;
    });

    /// <summary>Steps the controller while the fake clock advances (so a backoff or the loot wait can run out) until <paramref name="done"/>.</summary>
    private static async Task<bool> RunClockAsync(Rig rig, Func<bool> done, uint stepMs = 500, int steps = 400)
    {
        for (int i = 0; i < steps; i++)
        {
            if (await rig.World(() => { rig.Bot.Step(); return done(); })) return true;
            rig.Clock += stepMs;
            await Task.Delay(2);
        }

        return false;
    }

    private static async Task<bool> RunBrainAsync(Rig rig, Func<bool> done, int steps = 200)
    {
        for (int i = 0; i < steps; i++)
        {
            if (await rig.World(() => { rig.Bot.Session.ManagedBudget = new ManagedActionBudget(5); rig.Bot.Brain.Update(500); return done(); })) return true;
            await Task.Delay(25);
        }

        return false;
    }

    /// <summary>Gives the bot what a picker of that Lockpicking skill has: the Pick Lock spell in its book and the skill.</summary>
    private static void LearnPickLock(Rig rig, ushort skill, uint spell = PickLockSpell)
    {
        Assert.True(rig.Bot.Session.Services.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>().Spellbook.LearnSpell(rig.Player, spell));
        rig.Player.Skills!.Set(SkillIds.Lockpicking, skill, skill);
    }

    private static ulong KeyOf(ChestSpawn spawn) => ChestGuid(spawn);

    [Fact]
    public async Task AnUnguardedChest_IsOpened_AndItsLootTaken_WithNoAnomaly()
    {
        await using Rig rig = await Rig.StartAsync([Free]);
        using AnomalyCapture capture = await rig.World(() => PlayerbotAnomalies.CaptureFor(rig.Player));

        Assert.True(await RunAsync(rig, () => rig.Bot.Sink.Any(a => a.Opcode == WorldOpcode.CmsgLootRelease)), "the controller never reached the loot");
        await PlayLootAsync(rig);

        Assert.Equal(1u, await rig.World(() => rig.Player.Inventory.GetItemCount(GreenItem)));
        Assert.Null(rig.Bot.Controller.ActiveKey);
        Assert.False(capture.Any(Key("unreachable")) || capture.Any(Key("chest-no-loot")) || capture.Any(Key("chest-empty")), capture.Describe());
    }

    // --- the guard pack -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task AWeakGuardPack_IsClearedOnePullAtATime_ThenTheChestIsOpened()
    {
        await using Rig rig = await Rig.StartAsync([Free]);
        Creature near = await AddGuardAsync(rig, 96001, 5f, 2f);
        Creature far = await AddGuardAsync(rig, 96002, 8f, -2f);
        GameObject chest = rig.ChestOf(Free);

        OpportunityStep first = await rig.World(() => rig.Bot.Step());
        Assert.True(first.Busy);
        Assert.Same(near, first.Pull);
        Assert.Equal(PlayerbotGoalKind.Combat, first.Goal);
        OpportunityStep again = await rig.World(() => rig.Bot.Step());
        Assert.Same(near, again.Pull); // the guard being fought stays the pull
        Assert.Null(await rig.World(() => chest.Loot));

        await Kill(rig, near);
        OpportunityStep second = await rig.World(() => rig.Bot.Step());
        Assert.Same(far, second.Pull); // one pull at a time: the second guard only once the first is dead
        Assert.Equal(GameObjectLootState.Ready, await rig.World(() => chest.LootState));

        await Kill(rig, far);
        Assert.True(await RunAsync(rig, () => rig.Bot.Sink.Any(a => a.Opcode == WorldOpcode.CmsgLootRelease)), "the chest was never opened after the pack died");
        await PlayLootAsync(rig);
        Assert.Equal(1u, await rig.World(() => rig.Player.Inventory.GetItemCount(GreenItem)));
    }

    [Theory]
    [InlineData("elite-above", 4, 1u, 1, 4)]
    [InlineData("over-level", 6, 0u, 1, 4)]
    [InlineData("pack-of-2", 1, 0u, 2, 1)]
    public async Task ADangerousPack_IsRefused_AndTheChestIsLeftAlone(string reason, int guardLevel, uint rank, int guards, int maxGuards)
    {
        await using Rig rig = await Rig.StartAsync([Free], tweak: o => o.Opportunities.MaxGuards = maxGuards);
        for (int i = 0; i < guards; i++) await AddGuardAsync(rig, (uint)(96100 + i), 5f + i, 2f, (byte)guardLevel, rank, 20);

        Assert.True(await RunClockAsync(rig, () => rig.Bot.Controller.LastVerdict is not null, steps: 20));
        Assert.Equal(OpportunityDecision.Refuse, rig.Bot.Controller.LastVerdict!.Value.Decision);
        Assert.Equal(reason, rig.Bot.Controller.LastVerdict!.Value.Reason);
        Assert.Null(rig.Bot.Controller.ActiveKey);
        Assert.Empty(rig.Bot.Sink);
        GameObject chest = rig.ChestOf(Free);
        Assert.Equal(GameObjectLootState.Ready, await rig.World(() => chest.LootState));
        Assert.True(await rig.World(() => chest.User.IsEmpty));
    }

    [Fact]
    public async Task TheSameGuardPack_IsTakenByABoldBot_AndRefusedByACautiousOne()
    {
        // A level-3 guard against a level-1 bot with a modest payoff: the line between the two appetites.
        OpportunityVerdict cautious, bold;
        await using (Rig rig = await Rig.StartAsync([Free], tweak: o => { o.Opportunities.RiskAppetite = 0.25f; o.Opportunities.PersonalityVariance = 0f; o.Opportunities.MaxDeathProbability = 0.5f; }))
        {
            await AddGuardAsync(rig, 96200, 5f, 2f, 3, 0, 120);
            Assert.True(await RunClockAsync(rig, () => rig.Bot.Controller.LastVerdict is not null, steps: 20));
            cautious = rig.Bot.Controller.LastVerdict!.Value;
        }

        await using (Rig rig = await Rig.StartAsync([Free], tweak: o => { o.Opportunities.RiskAppetite = 4f; o.Opportunities.PersonalityVariance = 0f; o.Opportunities.MaxDeathProbability = 0.5f; }))
        {
            await AddGuardAsync(rig, 96200, 5f, 2f, 3, 0, 120);
            Assert.True(await RunClockAsync(rig, () => rig.Bot.Controller.LastVerdict is not null, steps: 20));
            bold = rig.Bot.Controller.LastVerdict!.Value;
        }

        Assert.Equal(cautious.Risk, bold.Risk, 3); // the same fight...
        Assert.True(cautious.Decision == OpportunityDecision.Refuse, cautious.ToString());
        Assert.True(bold.Decision == OpportunityDecision.Engage, bold.ToString()); // ...judged by the appetite
    }

    // --- locked chests ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AKeyedChest_IsIgnoredWithoutTheKey_AndOpenedByAKeyHolder()
    {
        ChestSpawn keyed = new(KeyChest, 2, 3f, 0f);
        await using (Rig rig = await Rig.StartAsync([keyed]))
        {
            await RunClockAsync(rig, () => false, steps: 12);
            Assert.Null(rig.Bot.Controller.ActiveKey);
            Assert.Null(rig.Bot.Controller.LastVerdict);
        }

        await using (Rig rig = await Rig.StartAsync([keyed]))
        {
            await rig.World(() => { Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, rig.Player.Inventory.AddItem(KeyItem, 1, out _)); return true; });
            Assert.True(await RunAsync(rig, () => rig.Bot.Sink.Any(a => a.Opcode == WorldOpcode.CmsgLootRelease)), "the key holder never opened the chest");
            await PlayLootAsync(rig);
            Assert.Equal(1u, await rig.World(() => rig.Player.Inventory.GetItemCount(GreenItem)));
        }
    }

    [Fact]
    public async Task APickLockChest_IsTakenOnlyByAPickerWhoseSkillReachesTheLock()
    {
        ChestSpawn locked = new(PickChest, 3, 3f, 0f);
        // A bot without the spell cannot pick; one with too little Lockpicking cannot yet; with the skill the picker picks it.
        await using (Rig plain = await Rig.StartAsync([locked], playerClass: 1))
        {
            await RunClockAsync(plain, () => false, steps: 12);
            Assert.Null(plain.Bot.Controller.LastVerdict);
            Assert.Null(plain.Bot.Controller.ActiveKey);
        }

        await using (Rig picker = await Rig.StartAsync([locked]))
        {
            await picker.World(() => { LearnPickLock(picker, 10); return true; });
            await RunClockAsync(picker, () => false, steps: 12);
            Assert.Null(picker.Bot.Controller.LastVerdict);
            Assert.Null(picker.Bot.Controller.ActiveKey);
        }

        await using (Rig picker = await Rig.StartAsync([locked]))
        {
            await picker.World(() => { LearnPickLock(picker, 25); return true; });
            Assert.True(await RunClockAsync(picker, () => picker.Bot.Controller.ActiveKey == KeyOf(locked), steps: 40), "the picker never started on the chest");
            Assert.True(await RunAsync(picker, () => picker.Bot.Sink.Any(a => a.Opcode == WorldOpcode.CmsgLootRelease), steps: 400), "the picker never picked the lock: " + picker.Bot.Controller.Report + " " + string.Join(",", picker.Bot.Session.DrainManagedPackets().Select(p => p.Opcode + ":" + Convert.ToHexString(p.Payload))));
            await PlayLootAsync(picker);
            Assert.Equal(1u, await picker.World(() => picker.Player.Inventory.GetItemCount(GreenItem)));
        }
    }

    [Fact]
    public async Task LockedChests_AreLeftAlone_WhenTheOptionIsOff()
    {
        ChestSpawn keyed = new(KeyChest, 2, 3f, 0f);
        await using Rig rig = await Rig.StartAsync([keyed], tweak: o => o.Opportunities.LockedChests = false);
        await rig.World(() => { Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, rig.Player.Inventory.AddItem(KeyItem, 1, out _)); return true; });
        await RunClockAsync(rig, () => false, steps: 12);
        Assert.Null(rig.Bot.Controller.ActiveKey);
        Assert.Null(rig.Bot.Controller.LastVerdict);
    }

    private static async Task<Rig> PickingBotAsync(ChestSpawn locked)
    {
        Rig rig = await Rig.StartAsync([locked]);
        await rig.World(() => { LearnPickLock(rig, 25); return true; });
        Assert.True(await RunAsync(rig, () => rig.Bot.Controller.Report.Contains("phase=await"), steps: 40), "the cast was never sent: " + rig.Bot.Controller.Report);
        return rig;
    }

    private static Task SendCastFailure(Rig rig, SpellCastResult reason, uint spell = PickLockSpell) => rig.World(() =>
    {
        var failure = new PacketWriter();
        failure.WriteUInt32(spell);
        failure.WriteByte((byte)SpellCastResultStatus.Failure);
        failure.WriteByte((byte)reason);
        rig.Bot.Session.Send(WorldOpcode.SmsgCastResult, failure);
        return true;
    });

    [Fact]
    public async Task APickThatSlips_IsCastAgain_NotGivenUp()
    {
        ChestSpawn locked = new(PickChest, 3, 3f, 0f);
        await using Rig rig = await PickingBotAsync(locked);
        using AnomalyCapture capture = await rig.World(() => PlayerbotAnomalies.CaptureFor(rig.Player));
        await SendCastFailure(rig, SpellCastResult.TryAgain);
        await rig.World(() => { rig.Bot.Step(); return true; });
        Assert.Equal(KeyOf(locked), rig.Bot.Controller.ActiveKey);
        Assert.Contains("phase=open", rig.Bot.Controller.Report);
        Assert.False(rig.Bot.Controller.Ledger.IsDeferred(KeyOf(locked), rig.Clock));
        Assert.False(capture.Any(Key("chest-pick-failed")), capture.Describe());
    }

    [Fact]
    public async Task APickThatFailsForGood_IsReportedOnce_AndSetAside()
    {
        ChestSpawn locked = new(PickChest, 3, 3f, 0f);
        await using Rig rig = await PickingBotAsync(locked);
        using AnomalyCapture capture = await rig.World(() => PlayerbotAnomalies.CaptureFor(rig.Player));
        await SendCastFailure(rig, SpellCastResult.Interrupted);
        await rig.World(() => { rig.Bot.Step(); return true; });
        Assert.Null(rig.Bot.Controller.ActiveKey);
        Assert.True(rig.Bot.Controller.Ledger.IsDeferred(KeyOf(locked), rig.Clock));
        Assert.Equal(1, capture.Count(Key("chest-pick-failed")));
    }

    // --- looted by someone else ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AChestSomeoneElseTook_IsDroppedAtOnce_WithNoBackoffAndNoAnomaly(bool opened)
    {
        await using Rig rig = await Rig.StartAsync([Free]);
        using AnomalyCapture capture = await rig.World(() => PlayerbotAnomalies.CaptureFor(rig.Player));
        Creature guard = await AddGuardAsync(rig, 96300, 5f, 2f);
        OpportunityStep started = await rig.World(() => rig.Bot.Step());
        Assert.Same(guard, started.Pull);
        Assert.Equal(KeyOf(Free), rig.Bot.Controller.ActiveKey);

        GameObject chest = rig.ChestOf(Free);
        await rig.World(() =>
        {
            if (opened) chest.User = ObjectGuid.Player(424242);
            else rig.Player.Map!.FindUpdater<GameObjectMapSystem>()!.Despawn(chest);
            return true;
        });
        if (!opened) await rig.Host.WaitForWorldAsync(() => !rig.Player.VisibleObjects.Contains(chest.Guid), "chest gone");

        OpportunityStep next = await rig.World(() => rig.Bot.Step());
        Assert.False(next.Busy);
        Assert.Null(rig.Bot.Controller.ActiveKey);
        Assert.False(rig.Bot.Controller.Ledger.IsDeferred(KeyOf(Free), rig.Clock), "someone else's chest earned a backoff");
        Assert.Empty(rig.Bot.Sink);
        Assert.False(capture.Any(Key("unreachable")) || capture.Any(Key("chest-no-loot")), capture.Describe());
    }

    // --- failure, backoff, respawn ------------------------------------------------------------------------------------

    [Fact]
    public async Task AFailedAttempt_IsSetAsideWithGrowingBackoff_ReportedOncePerEpisode_AndNeverHotRetried()
    {
        await using Rig rig = await Rig.StartAsync([Free]);
        await rig.World(() => { rig.Player.Map!.FindUpdater<GameObjectMapSystem>()!.DirectUseRefused = (_, _) => true; return true; }); // the server never opens it
        using AnomalyCapture capture = await rig.World(() => PlayerbotAnomalies.CaptureFor(rig.Player));
        ulong key = KeyOf(Free);

        Assert.True(await RunClockAsync(rig, () => rig.Bot.Controller.ActiveKey == key, steps: 10), "no attempt");
        Assert.True(await RunClockAsync(rig, () => rig.Bot.Controller.ActiveKey is null), "the attempt never ended");
        Assert.Equal(1, capture.Count(Key("chest-no-loot")));
        uint retryAt = rig.Bot.Controller.Ledger.DeferredUntil(key, rig.Clock);
        Assert.InRange(retryAt - rig.Clock, 59_000u, 60_000u);

        // Never hot-retried: until the backoff ends the chest is not touched, however often the bot thinks.
        bool retried = false;
        while (unchecked((int)(retryAt - rig.Clock)) > 1000)
        {
            await rig.World(() => { rig.Bot.Step(); return true; });
            retried |= rig.Bot.Controller.ActiveKey is not null;
            rig.Clock += 500;
        }

        Assert.False(retried, "the bot went back to the chest inside its backoff");
        Assert.Equal(1, capture.Count(Key("chest-no-loot")));

        // After the backoff it tries again, fails again, and waits twice as long.
        rig.Clock = retryAt + 1;
        Assert.True(await RunClockAsync(rig, () => rig.Bot.Controller.ActiveKey == key, steps: 10), "no second attempt after the backoff");
        Assert.True(await RunClockAsync(rig, () => rig.Bot.Controller.ActiveKey is null), "the second attempt never ended");
        uint wait2 = rig.Bot.Controller.Ledger.DeferredUntil(key, rig.Clock) - rig.Clock;
        Assert.InRange(wait2, 119_000u, 120_000u);
    }

    [Fact]
    public async Task ATakenChest_IsLookedAtAgainOnlyAfterItsRespawn()
    {
        await using Rig rig = await Rig.StartAsync([Free]);
        ulong key = KeyOf(Free);
        GameObject chest = rig.ChestOf(Free);
        Assert.True(await RunAsync(rig, () => rig.Bot.Sink.Any(a => a.Opcode == WorldOpcode.CmsgLootRelease)));
        await PlayLootAsync(rig);
        Assert.Null(rig.Bot.Controller.ActiveKey);
        uint takenAt = rig.Clock;
        Assert.True(rig.Bot.Controller.Ledger.IsDeferred(key, takenAt + 299_000), "a taken chest is rechecked before it can have respawned");
        Assert.False(rig.Bot.Controller.Ledger.IsDeferred(key, takenAt + 300_001));

        // The chest is back in the world at once (forced); the bot still leaves it alone until the respawn time has passed.
        await rig.World(() =>
        {
            GameObjectMapSystem system = rig.Player.Map!.FindUpdater<GameObjectMapSystem>()!;
            if (chest.IsSpawned) system.Despawn(chest);
            system.ForceRespawn(chest);
            return true;
        });
        await rig.Host.WaitForWorldAsync(() => rig.Player.VisibleObjects.Contains(chest.Guid) && chest.LootState == GameObjectLootState.Ready, "chest respawned");
        rig.Clock = takenAt + 120_000;
        Assert.False(await RunClockAsync(rig, () => rig.Bot.Controller.ActiveKey is not null, steps: 20), "the bot went back to a chest it had just emptied");
        rig.Clock = takenAt + 301_000;
        Assert.True(await RunClockAsync(rig, () => rig.Bot.Controller.ActiveKey == key, steps: 20), "the bot ignored the respawned chest");
    }

    // --- contention ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TwoBots_OneChest_OnlyTheFirstClaimantTakesIt()
    {
        await using Rig rig = await Rig.StartAsync([Free]);
        Bot second = await EnterAsync(rig.Host, "OPPBOT9", "Oppnine", 1, new PlayerbotOptions { Enabled = true }, rig.Claims, () => rig.Clock);
        try
        {
            await rig.Host.WaitForWorldAsync(() => second.Player.VisibleObjects.Contains(new ObjectGuid(ChestGuid(Free))), "chest visible to the second bot");
            // A weak guard holds the first bot on its pull, so the chest stays unopened while the second bot weighs it: only the claim keeps it off.
            Creature guard = await AddGuardAsync(rig, 96600, 5f, 2f);
            await rig.Host.WaitForWorldAsync(() => second.Player.VisibleObjects.Contains(guard.Guid), "guard visible to the second bot");
            OpportunityStep pull = await rig.World(() => rig.Bot.Step());
            Assert.Same(guard, pull.Pull);
            Assert.Equal(KeyOf(Free), rig.Bot.Controller.ActiveKey);
            await rig.World(() => { second.Step(); return true; });
            Assert.True(second.Controller.ActiveKey is null, "the second bot took a claimed chest: " + second.Controller.Report);
            await Kill(rig, guard);
            Assert.NotNull(second.Controller.LastVerdict); // it did weigh the chest; the claim alone kept it off

            Assert.True(await RunAsync(rig, () => rig.Bot.Sink.Any(a => a.Opcode == WorldOpcode.CmsgLootRelease)));
            await rig.World(() => { second.Step(); return true; });
            Assert.Empty(second.Sink);
            await PlayLootAsync(rig);
            Assert.Equal(1u, await rig.World(() => rig.Player.Inventory.GetItemCount(GreenItem)));
            Assert.Equal(0u, await rig.World(() => second.Player.Inventory.GetItemCount(GreenItem)));
        }
        finally
        {
            second.Brain.Stop();
            second.Session.Kick();
            await second.Session.ManagedClosed;
        }
    }

    [Fact]
    public async Task ARealPlayerAtTheChest_MakesTheBotStandAside_WithNoStrike()
    {
        await using Rig rig = await Rig.StartAsync([Free]);
        await using WorldTestClient client = await rig.Host.EnterWorldAsync("OPPREAL", "Realone");
        Player human = await rig.Host.PlayerAsync("Realone");
        await rig.Host.WaitForWorldAsync(() => rig.Player.VisibleObjects.Contains(human.Guid), "the player is visible");
        OpportunityStep step = await rig.World(() => rig.Bot.Step());
        Assert.False(step.Busy);
        Assert.Null(rig.Bot.Controller.ActiveKey);
        Assert.Empty(rig.Bot.Sink);
        // Set aside for the short contested wait, not the failure backoff.
        uint until = rig.Bot.Controller.Ledger.DeferredUntil(KeyOf(Free), rig.Clock);
        Assert.InRange(until - rig.Clock, 1u, PlayerbotOpportunities.ContestedMs);
    }

    // --- the brain ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheBrain_DoesNotTakeAnOpportunity_WhileItIsRecovering()
    {
        await using Rig rig = await Rig.StartAsync([Free]);
        await rig.World(() => { rig.Player.Health = 20; rig.Bot.Brain.Risk.BeginWait(rig.Player); return true; });
        for (int i = 0; i < 6; i++)
        {
            await rig.World(() => { rig.Bot.Session.ManagedBudget = new ManagedActionBudget(5); rig.Bot.Brain.Update(500); return true; });
            Assert.Equal(PlayerbotGoalKind.Rest, rig.Bot.Brain.Goal);
            Assert.Null(rig.Bot.Brain.Opportunities.ActiveKey);
            Assert.Null(rig.Bot.Brain.Opportunities.LastVerdict); // never even looked: the urgent goal held the think
        }
    }

    [Fact]
    public async Task TheBrain_TakesAnUnguardedChest_ThroughItsOwnLadder()
    {
        await using Rig rig = await Rig.StartAsync([Free]);
        Assert.True(await RunBrainAsync(rig, () => rig.Bot.Brain.Opportunities.ActiveKey == KeyOf(Free)), "the brain never started on the chest");
        Assert.Equal(PlayerbotGoalKind.Loot, rig.Bot.Brain.Goal);
        Assert.True(await RunBrainAsync(rig, () => rig.Player.Inventory.GetItemCount(GreenItem) > 0), "the brain never took the loot");
    }

    /// <summary>
    /// A stall give-up on a chest used to leave the episode running (no creature to set aside). The same think took
    /// Loot on that gameobject again (bot QA #962: target 19019, phase opportunities, goal still Loot after stall-giveup).
    /// </summary>
    [Fact]
    public async Task TheBrain_GivesUpAStalledChest_AndDoesNotTakeItAgain()
    {
        await using Rig rig = await Rig.StartAsync([Free]);
        Assert.True(await RunBrainAsync(rig, () => rig.Bot.Brain.Goal == PlayerbotGoalKind.Loot && rig.Bot.Brain.Opportunities.ActiveKey == KeyOf(Free)),
            "the brain never started on the chest");

        await rig.World(() =>
        {
            uint now = rig.Host.World.NowMs;
            rig.Bot.Brain.GiveUpStalledGoal(rig.Player, shareable: false);
            Assert.Null(rig.Bot.Brain.Opportunities.ActiveKey);
            Assert.True(rig.Bot.Brain.Opportunities.Ledger.IsDeferred(KeyOf(Free), now));
            Assert.NotEqual(FreeChest, rig.Bot.Brain.TargetEntry);
            rig.Bot.Session.ManagedBudget = new ManagedActionBudget(5);
            rig.Bot.Brain.Update(500);
            Assert.Null(rig.Bot.Brain.Opportunities.ActiveKey);
            Assert.NotEqual(PlayerbotGoalKind.Loot, rig.Bot.Brain.Goal);
            return true;
        });
    }

    [Fact]
    public async Task TheBrain_FightsTheChestsGuard_BeforeOpeningIt()
    {
        await using Rig rig = await Rig.StartAsync([Free]);
        await rig.World(() => { InstallOpenGround(rig.Host); return true; });
        Creature guard = await AddGuardAsync(rig, 96400, 5f, 2f);
        Assert.True(await RunBrainAsync(rig, () => ReferenceEquals(rig.Bot.Brain.InspectionTarget, guard), steps: 60), "the brain never went for the guard: " + rig.Bot.Brain.Opportunities.Report);
        Assert.Equal(KeyOf(Free), rig.Bot.Brain.Opportunities.ActiveKey); // the guard is the chest's pull, not a grind target
    }

    // --- nodes --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ANodeVeto_AppliesOnlyToAGuardedNodeTheBotWouldNotFight()
    {
        ChestSpawn node = new(NodeChest, 4, 3f, 0f);
        await using Rig rig = await Rig.StartAsync([node]);
        GameObject go = rig.ChestOf(node);
        Assert.False(await rig.World(() => rig.Bot.Controller.NodeVeto(go))); // unguarded: never vetoed
        await AddGuardAsync(rig, 96500, 5f, 2f, 9, 1, 400);
        rig.Clock += 20_000; // past the veto cache
        Assert.True(await rig.World(() => rig.Bot.Controller.NodeVeto(go)), "an elite beside the node did not veto it");
        await rig.World(() => { rig.Bot.Options.Opportunities.Nodes = false; return true; });
        rig.Clock += 20_000;
        Assert.False(await rig.World(() => rig.Bot.Controller.NodeVeto(go)), "the Nodes option off still vetoed");
    }

    // --- the node veto: risk, never value -------------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(30)]
    public async Task ANodeWithOneWeakSameLevelGuard_IsNotVetoed_ButAnEliteFarAboveIs(int level)
    {
        ChestSpawn node = new(NodeChest, 4, 3f, 0f); // its loot is one grey item: worth far less than MinValue, which must not matter
        await using Rig rig = await Rig.StartAsync([node], tweak: o => o.Opportunities.MinValue = 5f);
        await rig.World(() => { rig.Player.Level = (byte)level; return true; });
        GameObject go = rig.ChestOf(node);
        await AddGuardAsync(rig, 96510, 5f, 2f, (byte)level);
        Assert.False(await rig.World(() => rig.Bot.Controller.NodeVeto(go)), "one ordinary same-level guard vetoed the node");

        await AddGuardAsync(rig, 96511, 6f, 2f, (byte)(level + 9), 1, 400);
        rig.Clock += 20_000; // past the veto cache
        Assert.True(await rig.World(() => rig.Bot.Controller.NodeVeto(go)), "an elite far above the bot did not veto the node");
    }

    // --- crafting asks the veto -----------------------------------------------------------------------------------------

    private static async Task<PlayerbotCrafting> GatheringBotAsync(Rig rig)
    {
        await rig.World(() =>
        {
            Assert.True(rig.Bot.Session.Services.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>().Spellbook.LearnSpell(rig.Player, HerbSpell));
            rig.Player.Skills!.Set(SkillIds.Herbalism, 10, 10);
            return true;
        });
        return new PlayerbotCrafting(rig.Bot.Session, rig.Bot.Options);
    }

    private static Task<bool> GatherThink(Rig rig, PlayerbotCrafting crafting) => rig.World(() =>
    {
        rig.Bot.Session.ManagedBudget = new ManagedActionBudget(5);
        return crafting.Update(rig.Player, 500);
    });

    [Fact]
    public async Task Gathering_LeavesANodeTheVetoRefuses_AndTakesOneItDoesNot()
    {
        ChestSpawn node = new(NodeChest, 4, 3f, 0f);
        await using Rig rig = await Rig.StartAsync([node]);
        PlayerbotCrafting crafting = await GatheringBotAsync(rig);

        crafting.GatherVeto = _ => true;
        Assert.False(await GatherThink(rig, crafting), "the bot went for a node its veto refused");

        crafting.GatherVeto = _ => false;
        Assert.True(await GatherThink(rig, crafting), "the bot ignored a node its veto allowed");
        Assert.Equal(NodeChest, crafting.TargetEntry);
    }

    [Fact]
    public async Task Gathering_AsksTheOpportunityGoal_AboutTheGuardsBesideTheNode()
    {
        ChestSpawn node = new(NodeChest, 4, 3f, 0f);
        await using Rig rig = await Rig.StartAsync([node]);
        PlayerbotCrafting crafting = await GatheringBotAsync(rig);
        crafting.GatherVeto = rig.Bot.Controller.NodeVeto;
        await AddGuardAsync(rig, 96512, 5f, 2f, 9, 1, 400);
        Assert.False(await GatherThink(rig, crafting), "the bot gathered a node an elite guards");

        await rig.World(() => { rig.Bot.Options.Opportunities.Nodes = false; return true; });
        rig.Clock += 20_000;
        Assert.True(await GatherThink(rig, crafting), "with the Nodes option off the guards must not matter");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheBrain_GathersAnUnguardedNode_ButNotOneAnEliteGuards(bool guarded)
    {
        ChestSpawn node = new(NodeChest, 4, 3f, 0f);
        await using Rig rig = await Rig.StartAsync([node]);
        await GatheringBotAsync(rig);
        if (guarded) await AddGuardAsync(rig, 96520, 5f, 2f, 12, 1, 400);

        bool gathering = await RunBrainAsync(rig, () => rig.Bot.Brain.Goal == PlayerbotGoalKind.Vendor && rig.Bot.Brain.TargetEntry == NodeChest, steps: 40);
        Assert.Equal(!guarded, gathering);
    }

    // --- the brain's targets: a guard let go, a guard it cannot take ---------------------------------------------------

    /// <summary>A fight the opportunity goal accepts at a high appetite but the brain's own grind does not choose (a level-3 guard with 200 health: a risk of about 0.9).</summary>
    private static void BoldButNotAGrinder(PlayerbotOptions o)
    {
        o.Opportunities.RiskAppetite = 4f;
        o.Opportunities.PersonalityVariance = 0f;
        o.Opportunities.MaxDeathProbability = 0.5f;
    }

    [Fact]
    public async Task TheBrain_DropsTheGuardItWasHanded_WhenAnEliteJoinsThePack()
    {
        await using Rig rig = await Rig.StartAsync([Free], tweak: BoldButNotAGrinder);
        await rig.World(() => { InstallOpenGround(rig.Host); return true; });
        Creature guard = await AddGuardAsync(rig, 96410, 5f, 2f, 3, 0, 200);
        Assert.True(await RunBrainAsync(rig, () => ReferenceEquals(rig.Bot.Brain.InspectionTarget, guard), steps: 60), "the brain never went for the guard: " + rig.Bot.Brain.Opportunities.Report);
        Assert.Equal(KeyOf(Free), rig.Bot.Brain.Opportunities.ActiveKey);

        await AddGuardAsync(rig, 96411, 6f, 2f, 12, 1, 400); // an elite far above joins the pack
        Assert.True(await RunBrainAsync(rig, () => rig.Bot.Brain.Opportunities.ActiveKey is null, steps: 30), "the episode never ended: " + rig.Bot.Brain.Opportunities.Report);
        Assert.Equal("elite-above", rig.Bot.Brain.Opportunities.LastVerdict!.Value.Reason);
        Assert.False(ReferenceEquals(rig.Bot.Brain.InspectionTarget, guard), "the brain went on with the pull the goal gave up");
    }

    [Fact]
    public async Task TheBrain_DropsTheGuardItWasHanded_WhenARealPlayerComesToTheChest()
    {
        await using Rig rig = await Rig.StartAsync([Free], tweak: BoldButNotAGrinder);
        await rig.World(() => { InstallOpenGround(rig.Host); return true; });
        Creature guard = await AddGuardAsync(rig, 96412, 5f, 2f, 3, 0, 200);
        Assert.True(await RunBrainAsync(rig, () => ReferenceEquals(rig.Bot.Brain.InspectionTarget, guard), steps: 60), "the brain never went for the guard: " + rig.Bot.Brain.Opportunities.Report);

        await using WorldTestClient client = await rig.Host.EnterWorldAsync("OPPREAL2", "Realtwo");
        Player human = await rig.Host.PlayerAsync("Realtwo");
        await rig.Host.WaitForWorldAsync(() => rig.Player.VisibleObjects.Contains(human.Guid), "the player is visible");
        Assert.True(await RunBrainAsync(rig, () => rig.Bot.Brain.Opportunities.ActiveKey is null, steps: 30), "the episode never ended: " + rig.Bot.Brain.Opportunities.Report);
        Assert.False(ReferenceEquals(rig.Bot.Brain.InspectionTarget, guard), "the brain went on with the pull the goal gave up: " + rig.Bot.Brain.Opportunities.Report + " goal=" + rig.Bot.Brain.Goal);
    }

    [Fact]
    public async Task AGuardTheBrainWillNotTake_EndsTheEpisodeAtOnce_AsUnreachable_AndIsNotPulledAgain()
    {
        await using Rig rig = await Rig.StartAsync([Free]);
        using AnomalyCapture capture = await rig.World(() => PlayerbotAnomalies.CaptureFor(rig.Player));
        Creature guard = await AddGuardAsync(rig, 96420, 5f, 2f);
        bool blocked = false;
        rig.Bot.Controller.Untakeable = (_, creature) => blocked && ReferenceEquals(creature, guard);

        OpportunityStep first = await rig.World(() => rig.Bot.Step());
        Assert.Same(guard, first.Pull);
        blocked = true; // the brain's chase found no way to it, or another player tagged it

        OpportunityStep next = await rig.World(() => rig.Bot.Step());
        Assert.Null(next.Pull);
        Assert.False(next.Busy);
        Assert.Same(guard, next.Abandon);
        Assert.Null(rig.Bot.Controller.ActiveKey);
        Assert.Equal(1, capture.Count(Key("unreachable")));
        Assert.True(rig.Bot.Controller.Ledger.IsDeferred(KeyOf(Free), rig.Clock));
        Assert.InRange(rig.Bot.Controller.Ledger.DeferredUntil(KeyOf(Free), rig.Clock) - rig.Clock, 59_000u, 60_000u);

        // Not planned again, however often the bot thinks, until the backoff ends.
        for (int i = 0; i < 6; i++)
        {
            rig.Clock += 2_500;
            OpportunityStep again = await rig.World(() => rig.Bot.Step());
            Assert.Null(again.Pull);
            Assert.Null(rig.Bot.Controller.ActiveKey);
        }

        Assert.Equal(1, capture.Count(Key("unreachable")));
    }

    [Fact]
    public async Task AFindWhoseFirstGuardTheBrainWillNotTake_IsNotStarted_AndIsLookedAtAgainLater()
    {
        await using Rig rig = await Rig.StartAsync([Free]);
        using AnomalyCapture capture = await rig.World(() => PlayerbotAnomalies.CaptureFor(rig.Player));
        Creature guard = await AddGuardAsync(rig, 96421, 5f, 2f);
        rig.Bot.Controller.Untakeable = (_, creature) => ReferenceEquals(creature, guard);

        OpportunityStep step = await rig.World(() => rig.Bot.Step());
        Assert.False(step.Busy);
        Assert.Null(step.Pull);
        Assert.Null(rig.Bot.Controller.ActiveKey);
        Assert.False(capture.Any(Key("unreachable")), capture.Describe());
        Assert.True(rig.Bot.Controller.Ledger.IsDeferred(KeyOf(Free), rig.Clock));
        Assert.False(rig.Bot.Controller.Ledger.IsDeferred(KeyOf(Free), rig.Clock + PlayerbotOpportunities.RefusedRecheckMs + 1));
    }

    [Fact]
    public async Task ASecondGuardOutsideTheHome_EndsTheEpisodeWithoutReportingUnreachable()
    {
        await using Rig rig = await Rig.StartAsync([Free]);
        using AnomalyCapture capture = await rig.World(() => PlayerbotAnomalies.CaptureFor(rig.Player));
        Creature first = await AddGuardAsync(rig, 96422, 4f, 1f);
        Creature second = await AddGuardAsync(rig, 96423, 18f, 2f);
        await rig.World(() =>
        {
            var home = new PlayerbotHomeArea(rig.Player.MapId, new Vector3(rig.Player.X, rig.Player.Y, rig.Player.Z), 10f, "test-home");
            rig.Bot.Controller.Home = home;
            rig.Bot.Controller.Untakeable = (_, creature) => !home.Contains(creature);
            return true;
        });

        Assert.Same(first, (await rig.World(() => rig.Bot.Step())).Pull);
        await Kill(rig, first);
        OpportunityStep next = await rig.World(() => rig.Bot.Step());
        Assert.False(next.Busy);
        Assert.Null(next.Pull);
        Assert.Null(rig.Bot.Controller.ActiveKey);
        Assert.False(capture.Any(Key("unreachable")), capture.Describe());
        Assert.True(rig.Bot.Controller.Ledger.IsDeferred(KeyOf(Free), rig.Clock));
    }

    [Fact]
    public async Task TheBrain_GivesUpAGuardItCannotPlanAWalkTo_InsteadOfPullingItAtEveryThink()
    {
        await using Rig rig = await Rig.StartAsync([Free]); // no ground installed: no route to the guard can be planned
        using AnomalyCapture capture = await rig.World(() => PlayerbotAnomalies.CaptureFor(rig.Player));
        Creature guard = await AddGuardAsync(rig, 96430, 5f, 2f);
        PlayerbotOpportunities controller = rig.Bot.Brain.Opportunities;
        Assert.True(await RunBrainAsync(rig, () => controller.Ledger.IsDeferred(KeyOf(Free), rig.Host.World.NowMs), steps: 80),
            "the episode went on with a guard that cannot be reached: " + controller.Report);
        Assert.Equal(1, capture.Count(Key("unreachable")));
        Assert.Null(controller.ActiveKey);

        for (int i = 0; i < 12; i++)
        {
            await rig.World(() => { rig.Bot.Session.ManagedBudget = new ManagedActionBudget(5); rig.Bot.Brain.Update(500); return true; });
            Assert.Null(controller.ActiveKey);
            Assert.False(ReferenceEquals(rig.Bot.Brain.InspectionTarget, guard), "the brain took the guard it gave up");
        }

        Assert.Equal(1, capture.Count(Key("unreachable")));
    }

    // --- rares ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARare_IsPulledAndFought_ThenLeftAloneForItsRecheck()
    {
        await using Rig rig = await Rig.StartAsync([]);
        Creature rare = await AddGuardAsync(rig, RareEntry, 5f, 2f, 1, 4, 20);

        OpportunityStep first = await rig.World(() => rig.Bot.Step());
        Assert.True(first.Busy);
        Assert.Same(rare, first.Pull); // the rare itself is the pull
        Assert.Equal(rare.Guid.Value, rig.Bot.Controller.ActiveKey);
        Assert.Contains("opportunity=rare", rig.Bot.Controller.Report);
        OpportunityStep again = await rig.World(() => rig.Bot.Step());
        Assert.Same(rare, again.Pull);

        await Kill(rig, rare);
        OpportunityStep over = await rig.World(() => rig.Bot.Step());
        Assert.False(over.Busy);
        Assert.Null(over.Abandon); // a dead rare is the brain's to loot
        Assert.Null(rig.Bot.Controller.ActiveKey);
        Assert.True(rig.Bot.Controller.Ledger.IsDeferred(rare.Guid.Value, rig.Clock + PlayerbotOpportunities.RareRecheckMs - 1));
        Assert.False(rig.Bot.Controller.Ledger.IsDeferred(rare.Guid.Value, rig.Clock + PlayerbotOpportunities.RareRecheckMs + 1));
    }

    [Fact]
    public async Task ARare_IsLeftAlone_WhenTheRaresOptionIsOff_OrTheCreatureIsNoRare()
    {
        await using (Rig off = await Rig.StartAsync([], tweak: o => o.Opportunities.Rares = false))
        {
            await AddGuardAsync(off, RareEntry, 5f, 2f, 1, 4, 20);
            await RunClockAsync(off, () => false, steps: 12);
            Assert.Null(off.Bot.Controller.ActiveKey);
            Assert.Null(off.Bot.Controller.LastVerdict);
        }

        await using (Rig plain = await Rig.StartAsync([]))
        {
            await AddGuardAsync(plain, RareEntry, 5f, 2f, 1, 0, 20); // the same loot, but an ordinary creature
            await RunClockAsync(plain, () => false, steps: 12);
            Assert.Null(plain.Bot.Controller.ActiveKey);
            Assert.Null(plain.Bot.Controller.LastVerdict);
        }
    }

    [Fact]
    public async Task ARareFarAboveTheBot_IsRefused_AndNotPulled()
    {
        await using Rig rig = await Rig.StartAsync([]);
        await AddGuardAsync(rig, RareEntry, 5f, 2f, 9, 4, 400);
        Assert.True(await RunClockAsync(rig, () => rig.Bot.Controller.LastVerdict is not null, steps: 20));
        Assert.Equal(OpportunityDecision.Refuse, rig.Bot.Controller.LastVerdict!.Value.Decision);
        Assert.Equal("over-level", rig.Bot.Controller.LastVerdict!.Value.Reason);
        Assert.Null(rig.Bot.Controller.ActiveKey);
    }

    // --- the ways an episode ends ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AFightThatGoesBadly_SetsTheFindAside_WithTheFailureBackoff()
    {
        await using Rig rig = await Rig.StartAsync([Free]);
        Creature guard = await AddGuardAsync(rig, 96440, 5f, 2f);
        Assert.Same(guard, (await rig.World(() => rig.Bot.Step())).Pull);

        // A retreat or a death in the bot's danger memory during the episode.
        await rig.World(() =>
        {
            rig.Bot.Brain.Risk.Memory.Remember(rig.Player.MapId, new Vector3(StartX, StartY, StartZ), [(guard.Guid, guard.Entry)], rig.Clock, 60);
            return true;
        });
        OpportunityStep next = await rig.World(() => rig.Bot.Step());
        Assert.False(next.Busy);
        Assert.Same(guard, next.Abandon);
        Assert.Null(rig.Bot.Controller.ActiveKey);
        Assert.Equal("fight-went-badly", rig.Bot.Controller.LastVerdict!.Value.Reason);
        Assert.InRange(rig.Bot.Controller.Ledger.DeferredUntil(KeyOf(Free), rig.Clock) - rig.Clock, 59_000u, 60_000u);
    }

    [Fact]
    public async Task AnEpisodeThatRunsPastItsLimit_IsGivenUp_WithTheFailureBackoff()
    {
        await using Rig rig = await Rig.StartAsync([Free]);
        Creature guard = await AddGuardAsync(rig, 96441, 5f, 2f);
        Assert.Same(guard, (await rig.World(() => rig.Bot.Step())).Pull);

        rig.Clock += PlayerbotOpportunities.EpisodeMaxMs - 1_000;
        Assert.Same(guard, (await rig.World(() => rig.Bot.Step())).Pull); // not yet
        rig.Clock += 2_000;
        OpportunityStep next = await rig.World(() => rig.Bot.Step());
        Assert.False(next.Busy);
        Assert.Same(guard, next.Abandon);
        Assert.Null(rig.Bot.Controller.ActiveKey);
        Assert.Equal("timeout", rig.Bot.Controller.LastVerdict!.Value.Reason);
        Assert.InRange(rig.Bot.Controller.Ledger.DeferredUntil(KeyOf(Free), rig.Clock) - rig.Clock, 59_000u, 60_000u);
    }

    [Fact]
    public async Task ADeathDuringTheEpisode_SetsTheFindAside_WithTheFailureBackoff()
    {
        await using Rig rig = await Rig.StartAsync([Free]);
        Creature guard = await AddGuardAsync(rig, 96442, 5f, 2f);
        Assert.Same(guard, (await rig.World(() => rig.Bot.Step())).Pull);
        await rig.World(() =>
        {
            rig.Player.Map!.Combat.DealDamage(guard, rig.Player, 100_000);
            Assert.False(rig.Player.IsAlive);
            return true;
        });

        OpportunityStep next = await rig.World(() => rig.Bot.Step());
        Assert.False(next.Busy);
        Assert.Null(rig.Bot.Controller.ActiveKey);
        Assert.InRange(rig.Bot.Controller.Ledger.DeferredUntil(KeyOf(Free), rig.Clock) - rig.Clock, 59_000u, 60_000u);
    }

    [Fact]
    public async Task TheBrainTellingTheGoalItDied_SetsTheFindAside_WithTheFailureBackoff()
    {
        await using Rig rig = await Rig.StartAsync([Free]);
        await AddGuardAsync(rig, 96443, 5f, 2f);
        Assert.NotNull((await rig.World(() => rig.Bot.Step())).Pull);
        await rig.World(() => { rig.Bot.Controller.BotDied(); return true; });
        Assert.Null(rig.Bot.Controller.ActiveKey);
        Assert.InRange(rig.Bot.Controller.Ledger.DeferredUntil(KeyOf(Free), rig.Clock) - rig.Clock, 59_000u, 60_000u);
    }

    private static Task FightAsync(Rig rig, Creature guard) => rig.World(() =>
    {
        rig.Player.Map!.Combat.DealDamage(rig.Player, guard, 1);
        Assert.True(rig.Player.Combat.ThreatenedBy.Contains(guard) || ReferenceEquals(guard.Combat.Victim, rig.Player), "the bot is not in combat with the guard");
        return true;
    });

    [Fact]
    public async Task TheClaimOnAFind_IsKeptAlive_WhileTheBotFightsAGuard_ButOnlyUntilTheEpisodeLimit()
    {
        await using Rig rig = await Rig.StartAsync([Free]);
        Creature guard = await AddGuardAsync(rig, 96444, 5f, 2f);
        Assert.NotNull((await rig.World(() => rig.Bot.Step())).Pull);
        await FightAsync(rig, guard);
        for (int i = 0; i < 6; i++)
        {
            rig.Clock += PlayerbotClaims.ClaimLifeMs / 2; // far longer than a claim lives, in all
            Assert.Null(await rig.World(() => rig.Bot.Controller.KeepClaim(rig.Player)));
        }

        Assert.Equal(rig.Player.Guid.Value, rig.Claims.OwnerOf(KeyOf(Free), rig.Clock));

        // A fight that never ends is bounded too: past the limit the episode is a timeout, with the backoff.
        rig.Clock += PlayerbotOpportunities.EpisodeMaxMs;
        await rig.World(() => rig.Bot.Controller.KeepClaim(rig.Player));
        Assert.Null(rig.Bot.Controller.ActiveKey);
        Assert.Equal("timeout", rig.Bot.Controller.LastVerdict!.Value.Reason);
        Assert.InRange(rig.Bot.Controller.Ledger.DeferredUntil(KeyOf(Free), rig.Clock) - rig.Clock, 59_000u, 60_000u);
    }

    [Fact]
    public async Task TheClaimOnAFind_IsNotKept_WhenTheBotIsNotFightingAGuard_AndTheEpisodeEnds()
    {
        await using Rig rig = await Rig.StartAsync([Free]);
        Creature guard = await AddGuardAsync(rig, 96445, 5f, 2f);
        Assert.Same(guard, (await rig.World(() => rig.Bot.Step())).Pull);
        Creature? given = null;
        for (int i = 0; i < 4; i++)
        {
            rig.Clock += PlayerbotClaims.ClaimLifeMs / 2;
            given = await rig.World(() => rig.Bot.Controller.KeepClaim(rig.Player)) ?? given;
        }

        Assert.Null(rig.Bot.Controller.ActiveKey);
        Assert.Same(guard, given);
        Assert.Equal(0ul, rig.Claims.OwnerOf(KeyOf(Free), rig.Clock));
    }

    [Fact]
    public async Task TheBrain_LetsTheClaimLapse_AndEndsTheEpisode_WhenAHigherGoalHoldsTheThink()
    {
        await using Rig rig = await Rig.StartAsync([Free], tweak: BoldButNotAGrinder);
        await rig.World(() => { InstallOpenGround(rig.Host); return true; });
        PlayerbotOpportunities controller = rig.Bot.Brain.Opportunities;
        controller.Clock = () => rig.Clock;
        Creature guard = await AddGuardAsync(rig, 96413, 5f, 2f, 3, 0, 200);
        Assert.True(await RunBrainAsync(rig, () => ReferenceEquals(rig.Bot.Brain.InspectionTarget, guard), steps: 60), "the brain never went for the guard: " + controller.Report);
        Assert.Equal(KeyOf(Free), controller.ActiveKey);

        // The bot is hurt and rests: the urgent goals hold every think, so the episode is never stepped.
        await rig.World(() => { rig.Player.Health = 20; rig.Bot.Brain.Risk.BeginWait(rig.Player); return true; });
        for (int i = 0; i < 6; i++)
        {
            rig.Clock += PlayerbotClaims.ClaimLifeMs / 2;
            await rig.World(() => { rig.Bot.Session.ManagedBudget = new ManagedActionBudget(5); rig.Bot.Brain.Update(500); return true; });
            Assert.Equal(PlayerbotGoalKind.Rest, rig.Bot.Brain.Goal);
        }

        Assert.Null(controller.ActiveKey);
        Assert.Equal(0ul, rig.Claims.OwnerOf(KeyOf(Free), rig.Clock));
        Assert.NotEqual(0u, rig.Claims.Join(KeyOf(Free), 777, rig.Clock)); // another bot can take the find
        Assert.Equal(777ul, rig.Claims.OwnerOf(KeyOf(Free), rig.Clock));
        Assert.False(ReferenceEquals(rig.Bot.Brain.InspectionTarget, guard));
    }

    [Fact]
    public async Task ASecondGuardTheBrainWillNotTake_EndsTheEpisodeAsUnreachable_WithTheBackoff()
    {
        await using Rig rig = await Rig.StartAsync([Free]);
        using AnomalyCapture capture = await rig.World(() => PlayerbotAnomalies.CaptureFor(rig.Player));
        Creature near = await AddGuardAsync(rig, 96446, 5f, 2f);
        Creature far = await AddGuardAsync(rig, 96447, 8f, -2f);
        rig.Bot.Controller.Untakeable = (_, creature) => ReferenceEquals(creature, far);

        Assert.Same(near, (await rig.World(() => rig.Bot.Step())).Pull); // the first guard is fine
        await Kill(rig, near);
        OpportunityStep next = await rig.World(() => rig.Bot.Step()); // the second is the one it cannot take
        Assert.Null(next.Pull);
        Assert.Null(rig.Bot.Controller.ActiveKey);
        Assert.Equal(1, capture.Count(Key("unreachable")));
        Assert.InRange(rig.Bot.Controller.Ledger.DeferredUntil(KeyOf(Free), rig.Clock) - rig.Clock, 59_000u, 60_000u);
    }

    // --- Pick Lock: the cast cap ----------------------------------------------------------------------------------------

    private static async Task<bool> TakeServerReplyAsync(Rig rig, int steps = 100)
    {
        for (int i = 0; i < steps; i++)
        {
            if (await rig.World(() => rig.Bot.Session.DrainManagedPackets(WorldOpcode.SmsgCastResult).Count > 0)) return true;
            await Task.Delay(10);
        }

        return false;
    }

    [Fact]
    public async Task APickThatKeepsSlipping_IsCastEightTimes_ThenReportedAndGivenUp()
    {
        ChestSpawn locked = new(PickChest, 3, 3f, 0f);
        await using Rig rig = await Rig.StartAsync([locked]);
        // The server turns every cast of this pick down at once (range 0), so no cast stays in flight; the replies are ours to send.
        await rig.World(() => { LearnPickLock(rig, 25, ShortPickSpell); return true; });
        using AnomalyCapture capture = await rig.World(() => PlayerbotAnomalies.CaptureFor(rig.Player));
        Assert.True(await RunAsync(rig, () => rig.Bot.Controller.Report.Contains("phase=await"), steps: 40), "the first cast was never sent: " + rig.Bot.Controller.Report);

        for (int cast = 1; cast <= PlayerbotOpportunities.MaxPickAttempts; cast++)
        {
            Assert.True(await TakeServerReplyAsync(rig), "the server never answered cast " + cast);
            await SendCastFailure(rig, SpellCastResult.TryAgain, ShortPickSpell);
            await rig.World(() => { rig.Bot.Step(); return true; });
            if (cast == PlayerbotOpportunities.MaxPickAttempts) break;
            Assert.Contains("phase=open", rig.Bot.Controller.Report);
            await rig.World(() => { rig.Bot.Step(); return true; }); // the next cast goes out
            Assert.Contains("phase=await", rig.Bot.Controller.Report);
        }

        Assert.Null(rig.Bot.Controller.ActiveKey);
        Assert.Equal("pick-failed", rig.Bot.Controller.LastVerdict!.Value.Reason);
        Assert.Equal(1, capture.Count(Key("chest-pick-failed")));
        Assert.Contains("after 8 cast(s)", capture.Describe());
        Assert.True(rig.Bot.Controller.Ledger.IsDeferred(KeyOf(locked), rig.Clock));

        // The cap holds: no ninth cast.
        for (int i = 0; i < 3; i++) await rig.World(() => { rig.Bot.Step(); return true; });
        Assert.False(await TakeServerReplyAsync(rig, steps: 15), "a ninth cast went out");
    }

    // --- refused finds are not weighed again at once ---------------------------------------------------------------------

    [Fact]
    public async Task ARefusedFind_IsNotWeighedAgainAtTheNextScan_ButIsAfterTheRecheck()
    {
        await using Rig rig = await Rig.StartAsync([Free]);
        Creature guard = await AddGuardAsync(rig, 96450, 5f, 2f, 9, 1, 400);
        await rig.World(() => { rig.Bot.Step(); return true; });
        Assert.Equal(OpportunityDecision.Refuse, rig.Bot.Controller.LastVerdict!.Value.Decision);
        Assert.True(rig.Bot.Controller.Ledger.IsDeferred(KeyOf(Free), rig.Clock));

        // The guard goes; the next scan, 2 s on, does not look at the chest (set aside), the one after the recheck does.
        await Kill(rig, guard);
        rig.Clock += PlayerbotOpportunities.ScanEveryMs + 100;
        OpportunityStep soon = await rig.World(() => rig.Bot.Step());
        Assert.False(soon.Busy);
        Assert.Null(rig.Bot.Controller.ActiveKey);
        rig.Clock += PlayerbotOpportunities.RefusedRecheckMs;
        await rig.World(() => { rig.Bot.Step(); return true; });
        Assert.Equal(KeyOf(Free), rig.Bot.Controller.ActiveKey);
    }

    // --- interrupting a quest or grind goal ------------------------------------------------------------------------------

    [Fact]
    public async Task AnInterruptingBot_AsksMoreOfAFind_ThanOneWithNothingToDrop()
    {
        await using Rig rig = await Rig.StartAsync([Free], tweak: o => { o.Opportunities.MinValue = 20f; o.Opportunities.PersonalityVariance = 0f; });
        await rig.World(() => { rig.Bot.Step(); return true; });
        OpportunityVerdict probe = rig.Bot.Controller.LastVerdict!.Value;
        Assert.Equal("low-value", probe.Reason);
        Assert.True(probe.Net > 1f, probe.ToString());

        // A bar between the net worth and 1.5 times itself: met by a bot with nothing to drop, not by one that would drop a goal.
        rig.Bot.Options.Opportunities.MinValue = probe.Net / 1.25f;
        rig.Clock += PlayerbotOpportunities.RefusedRecheckMs + 1_000;
        await rig.World(() => { rig.Bot.Step(interrupting: true); return true; });
        Assert.Null(rig.Bot.Controller.ActiveKey);
        Assert.Equal("low-value", rig.Bot.Controller.LastVerdict!.Value.Reason);

        rig.Clock += PlayerbotOpportunities.RefusedRecheckMs + 1_000;
        await rig.World(() => { rig.Bot.Step(interrupting: false); return true; });
        Assert.Equal(KeyOf(Free), rig.Bot.Controller.ActiveKey);
    }

    [Fact]
    public async Task TheBrain_AsksMoreOfAFind_WhileItIsOnAQuestGoal_ThanOnAnyOther()
    {
        await using Rig rig = await Rig.StartAsync([Free], tweak: o => { o.Opportunities.MinValue = 20f; o.Opportunities.PersonalityVariance = 0f; });
        PlayerbotOpportunities controller = rig.Bot.Brain.Opportunities;
        controller.Clock = () => rig.Clock;
        await rig.World(() => { rig.Bot.Session.ManagedBudget = new ManagedActionBudget(5); rig.Bot.Brain.Update(500); return true; });
        OpportunityVerdict probe = controller.LastVerdict!.Value;
        Assert.Equal("low-value", probe.Reason);

        rig.Bot.Options.Opportunities.MinValue = probe.Net / 1.25f;
        System.Reflection.FieldInfo goal = typeof(PlayerbotBrain).GetField("_goal", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        rig.Clock += PlayerbotOpportunities.RefusedRecheckMs + 1_000;
        await rig.World(() => { goal.SetValue(rig.Bot.Brain, PlayerbotGoalKind.Quest); rig.Bot.Session.ManagedBudget = new ManagedActionBudget(5); rig.Bot.Brain.Update(500); return true; });
        Assert.Null(controller.ActiveKey);
        Assert.Equal("low-value", controller.LastVerdict!.Value.Reason);

        rig.Clock += PlayerbotOpportunities.RefusedRecheckMs + 1_000;
        await rig.World(() => { goal.SetValue(rig.Bot.Brain, PlayerbotGoalKind.Explore); rig.Bot.Session.ManagedBudget = new ManagedActionBudget(5); rig.Bot.Brain.Update(500); return true; });
        Assert.Equal(KeyOf(Free), controller.ActiveKey);
    }

    // --- anomalies ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AChestThatOpensEmpty_IsReportedOnce()
    {
        await using Rig rig = await Rig.StartAsync([Free]);
        await rig.World(() => { rig.Player.Map!.FindUpdater<GameObjectMapSystem>()!.DirectUseRefused = (_, _) => true; return true; });
        using AnomalyCapture capture = await rig.World(() => PlayerbotAnomalies.CaptureFor(rig.Player));
        Assert.True(await RunClockAsync(rig, () => rig.Bot.Controller.Report.Contains("phase=await"), steps: 20));
        await rig.World(() =>
        {
            var window = new PacketWriter();
            window.WriteUInt64(KeyOf(Free));
            window.WriteByte(1);
            window.WriteUInt32(0);
            window.WriteByte(0);
            rig.Bot.Session.Send(WorldOpcode.SmsgLootResponse, window);
            return true;
        });
        Assert.True(await RunClockAsync(rig, () => rig.Bot.Controller.ActiveKey is null, steps: 40));
        Assert.Equal(1, capture.Count(Key("chest-empty")));
        Assert.Contains(rig.Bot.Sink, a => a.Opcode == WorldOpcode.CmsgLootRelease);
    }

    /// <summary>
    /// A chest within <see cref="PlayerbotNavigation.NearYards"/> whose only walk is a snake of more polygons than the exact search's
    /// 2048-node budget. The region graph puts both ends in one region (it can reach the chest). The exact search still refuses, and
    /// the find must not be reported unreachable.
    /// </summary>
    [Fact]
    public async Task ANearChestTheRegionGraphJoins_IsNotUnreachable_WhenTheExactSearchRunsOutOfNodes()
    {
        // Even rows are full; each odd row is the one cell at the end the row above leaves by, so the only walk snakes
        // (24 * 150 + 23 polygons) while the chest stays 46 yards off, inside the short search budget.
        const float cell = 1f;
        const int width = 150;
        const int lastRow = 46;
        ChestSpawn chest = new(FreeChest, 9, 0f, lastRow * cell);
        using var fixture = new NavMeshFixture();
        (int detourX, int detourY) = ((int)MathF.Floor(StartY / 533.3333f), (int)MathF.Floor(StartX / 533.3333f));
        (int fileX, int fileY) = TerrainTile.TileOf(StartX, StartY)!.Value;
        fixture.WriteParams(0);
        fixture.WriteTile(0, fileX, fileY, new CellTile(detourX, detourY, StartX - (cell / 2f), StartY - (cell / 2f), width, lastRow + 1, cell)
        {
            Height = (_, _) => StartZ,
            Walkable = (i, j) => (j & 1) == 0 || i == ((((j - 1) / 2) & 1) == 0 ? width - 1 : 0),
        });
        var nav = new NavMeshPathfinder(fixture.Directory);
        Assert.True(nav.LoadTile(0, fileX, fileY));
        Task<NavRegionGraph?>? pending = nav.RegionGraphTask(0, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes);
        Assert.NotNull(pending);
        Assert.NotNull(await pending);

        await using Rig rig = await Rig.StartAsync([chest], tweak: options =>
        {
            options.Opportunities.ScanRadiusYards = 80f;
            options.Opportunities.PersonalityVariance = 0f;
            options.Opportunities.MinValue = 0f;
        });
        await rig.World(() =>
        {
            WorldCollision.Of(rig.Host.World).Install(pathfinder: nav);
            GameObject box = rig.ChestOf(chest);
            Vector3 goal = new(box.X, box.Y, box.Z);
            Vector3 here = PlayerbotMotion.CurrentPosition(rig.Player);
            float apart = Vector3.Distance(here, goal);
            Assert.True(apart > PlayerbotOpportunities.InteractYards && apart <= PlayerbotNavigation.NearYards, $"straight {apart:F1}");
            TileWait wait = default;
            Assert.Equal(PlanOutcome.Refused, PlayerbotNavigation.PlanExact(rig.Player, goal, rig.Bot.Options, ref wait, rig.Clock, out _));
            NavRegionGraph graph = nav.RegionGraph(0, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes)!;
            var locate = new PathOptions { Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes, LoadTiles = true, NonBlockingTiles = true };
            int from = nav.RegionAt(graph, here, locate), to = nav.RegionAt(graph, goal, locate);
            Assert.True(from >= 0 && from == to, $"regions {from}, {to}");
            Assert.Equal(NavRegionRouteStatus.Found, graph.FindRoute(from, here, to, goal, PlayerbotNavigation.RegionSearchNodes, out List<Vector3> portals));
            Assert.Empty(portals);
            Assert.Equal(PlanOutcome.Route, PlayerbotNavigation.PlanFar(rig.Player, goal, rig.Bot.Options, PlayerbotNavigation.MaxSearchNodes, ref wait, rig.Clock, out PlayerbotRoute? wide));
            Assert.NotNull(wide);
            return true;
        });

        using AnomalyCapture capture = await rig.World(() => PlayerbotAnomalies.CaptureFor(rig.Player));
        OpportunityStep step = await rig.World(() => rig.Bot.Step());
        Assert.True(step.Busy, rig.Bot.Controller.Report);
        Assert.Equal(KeyOf(chest), rig.Bot.Controller.ActiveKey);
        Assert.Equal(0, capture.Count(Key("unreachable")));
        Assert.False(rig.Bot.Controller.Ledger.IsDeferred(KeyOf(chest), rig.Clock));
    }

    /// <summary>
    /// The chest the exact search refuses (a snake of more polygons than its budget) lies in the next region of the graph. The region route
    /// is asked through the cache like the legs of a far goal: while its answer is outstanding (here a lost search, marked asked) the chest
    /// answers pending - neither searched on the world thread nor refused - and once the pair is answered the bot walks to it.
    /// </summary>
    [Fact]
    public async Task AChestAcrossARegionBoundary_AnswersPending_UntilTheRegionRouteIsSearched()
    {
        const float cell = 1f;
        const int width = 150;
        const int lastRow = 46;
        ChestSpawn chest = new(FreeChest, 9, 0f, lastRow * cell + 2f);
        using var fixture = new NavMeshFixture();
        (int detourX, int detourY) = ((int)MathF.Floor(StartY / 533.3333f), (int)MathF.Floor(StartX / 533.3333f));
        (int fileX, int fileY) = TerrainTile.TileOf(StartX, StartY)!.Value;
        fixture.WriteParams(0);
        fixture.WriteTile(0, fileX, fileY, new CellTile(detourX, detourY, StartX - (cell / 2f), StartY - (cell / 2f), width, lastRow + 1, cell)
        {
            Height = (_, _) => StartZ,
            Walkable = (i, j) => (j & 1) == 0 || i == ((((j - 1) / 2) & 1) == 0 ? width - 1 : 0),
            LinkedSides = [0],
        });
        fixture.WriteTile(0, fileX + 1, fileY, new CellTile(detourX + 1, detourY, StartX - (cell / 2f), StartY - (cell / 2f) + ((lastRow + 1) * cell), width, 10, cell)
        {
            Height = (_, _) => StartZ,
            LinkedSides = [4],
        });
        var nav = new NavMeshPathfinder(fixture.Directory);
        Assert.True(nav.LoadTile(0, fileX, fileY));
        Assert.True(nav.LoadTile(0, fileX + 1, fileY));
        Task<NavRegionGraph?>? building = nav.RegionGraphTask(0, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes);
        Assert.NotNull(building);
        NavRegionGraph graph = (await building)!;

        await using Rig rig = await Rig.StartAsync([chest], tweak: options =>
        {
            options.Opportunities.ScanRadiusYards = 80f;
            options.Opportunities.PersonalityVariance = 0f;
            options.Opportunities.MinValue = 0f;
        });
        (int from, int to, Vector3 here, Vector3 goal) = await rig.World(() =>
        {
            WorldCollision.Of(rig.Host.World).Install(pathfinder: nav);
            GameObject box = rig.ChestOf(chest);
            Vector3 position = PlayerbotMotion.CurrentPosition(rig.Player);
            Vector3 target = new(box.X, box.Y, box.Z);
            var locate = new PathOptions { Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes, LoadTiles = true, NonBlockingTiles = true };
            return (nav.RegionAt(graph, position, locate), nav.RegionAt(graph, target, locate), position, target);
        });
        Assert.True(from >= 0 && to >= 0 && from != to, $"regions {from}, {to}");
        PlayerbotRegionRoutes.RouteCache cache = PlayerbotRegionRoutes.Of(graph);
        cache.MarkAsked(from, to, long.MaxValue / 2); // a search that never comes back, never old enough to be searched here

        using AnomalyCapture capture = await rig.World(() => PlayerbotAnomalies.CaptureFor(rig.Player));
        for (int think = 0; think < 3; think++)
        {
            await rig.World(() => rig.Bot.Step()); // pending is no verdict: the find is neither started nor set aside
        }

        Assert.Equal(0, cache.Searches);
        Assert.Equal(1, cache.AskedCount);
        Assert.NotEqual(KeyOf(chest), rig.Bot.Controller.ActiveKey); // Pending is no verdict: the chest is not taken (a direct FindRoute would take it at once)
        Assert.Equal(0, capture.Count(Key("unreachable")));
        Assert.False(rig.Bot.Controller.Ledger.IsDeferred(KeyOf(chest), rig.Clock));

        cache.Search(from, here, to, goal);
        Assert.Equal(1, cache.Searches);
        Assert.True(await RunClockAsync(rig, () => rig.Bot.Controller.ActiveKey == KeyOf(chest), steps: 20), "the chest was never taken up: " + rig.Bot.Controller.Report);
        Assert.Equal(1, cache.Searches);
        Assert.Equal(0, capture.Count(Key("unreachable")));
        Assert.False(rig.Bot.Controller.Ledger.IsDeferred(KeyOf(chest), rig.Clock));
    }

    /// <summary>
    /// A far chest whose tile is never read (the read does not finish) answers pending while the tile wait runs, and is set aside once the
    /// wait is spent (<see cref="TileWait.MaxMs"/>, wall clock and game clock): it is not asked again at every scan for ever.
    /// </summary>
    [Fact]
    public async Task AChestWhoseTileNeverArrives_IsSetAside_OnceTheTileWaitIsSpent()
    {
        const float cell = 1f;
        const int width = 150;
        const int lastRow = 46;
        ChestSpawn chest = new(FreeChest, 9, 0f, lastRow * cell + 2f);
        using var fixture = new NavMeshFixture();
        using var gate = new ManualResetEventSlim();
        bool blockReads = false;
        (int detourX, int detourY) = ((int)MathF.Floor(StartY / 533.3333f), (int)MathF.Floor(StartX / 533.3333f));
        (int fileX, int fileY) = TerrainTile.TileOf(StartX, StartY)!.Value;
        fixture.WriteParams(0);
        fixture.WriteTile(0, fileX, fileY, new CellTile(detourX, detourY, StartX - (cell / 2f), StartY - (cell / 2f), width, lastRow + 1, cell)
        {
            Height = (_, _) => StartZ,
            Walkable = (i, j) => (j & 1) == 0 || i == ((((j - 1) / 2) & 1) == 0 ? width - 1 : 0),
            LinkedSides = [0],
        });
        fixture.WriteTile(0, fileX + 1, fileY, new CellTile(detourX + 1, detourY, StartX - (cell / 2f), StartY - (cell / 2f) + ((lastRow + 1) * cell), width, 10, cell)
        {
            Height = (_, _) => StartZ,
            LinkedSides = [4],
        });
        var nav = new NavMeshPathfinder(fixture.Directory)
        {
            FileReader = path =>
            {
                if (blockReads && path.EndsWith(".mmtile", StringComparison.Ordinal)) gate.Wait();
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            },
        };
        try
        {
            Assert.True(nav.LoadTile(0, fileX, fileY)); // the bot's own tile is in; the chest's tile is read on the pool and never finishes
            Assert.NotNull(await nav.RegionGraphTask(0, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes)!);
            blockReads = true;

            await using Rig rig = await Rig.StartAsync([chest], tweak: options =>
            {
                options.Opportunities.ScanRadiusYards = 80f;
                options.Opportunities.PersonalityVariance = 0f;
                options.Opportunities.MinValue = 0f;
            });
            long realTicks = 0;
            rig.Bot.Controller.ChestWaitRealTicks = () => Interlocked.Read(ref realTicks);
            await rig.World(() => { WorldCollision.Of(rig.Host.World).Install(pathfinder: nav); return true; });

            bool setAside = false;
            for (int second = 1; second <= 60 && !setAside; second++)
            {
                await rig.World(() => rig.Bot.Step());
                rig.Clock += 1_000;
                Interlocked.Add(ref realTicks, System.Diagnostics.Stopwatch.Frequency);
                setAside = rig.Bot.Controller.Ledger.IsDeferred(KeyOf(chest), rig.Clock);
                if (second <= 5) Assert.False(setAside, $"set aside after {second} s, while the tile wait still runs");
            }

            Assert.True(setAside, "the chest was asked again at every scan for a minute and never set aside: " + rig.Bot.Controller.Report);
            Assert.Null(rig.Bot.Controller.ActiveKey);
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public async Task AChestTheBotCannotWalkTo_IsReportedUnreachableOnce_AndSetAside()
    {
        ChestSpawn far = new(FreeChest, 5, 25f, 0f); // beyond the interaction range, and no ground to plan a route on
        await using Rig rig = await Rig.StartAsync([far]);
        using AnomalyCapture capture = await rig.World(() => PlayerbotAnomalies.CaptureFor(rig.Player));
        Assert.True(await RunClockAsync(rig, () => rig.Bot.Controller.Ledger.IsDeferred(KeyOf(far), rig.Clock), steps: 20), "the chest was never given up: " + rig.Bot.Controller.Report);
        Assert.Equal(1, capture.Count(Key("unreachable")));
        Assert.True(rig.Bot.Controller.Ledger.IsDeferred(KeyOf(far), rig.Clock));
    }

    /// <summary>A trial plan that answers pending is no refusal: the chest is neither rejected nor accepted yet, whichever step answered it.</summary>
    [Theory]
    [InlineData("Pending", "Refused", "Refused", "Pending")]
    [InlineData("Refused", "Refused", "Pending", "Pending")]
    [InlineData("Pending", "Refused", "Route", "Route")]
    [InlineData("Pending", "Route", "Refused", "Route")]
    [InlineData("Route", "Refused", "Refused", "Route")]
    [InlineData("Refused", "Refused", "Refused", "Refused")]
    [InlineData("Refused", "Pending", "Refused", "Pending")]
    public void TheChestVerdict_KeepsAPendingTrialFromBecomingARefusal(string exact, string graph, string nearest, string expected)
        => Assert.Equal(Enum.Parse<PlanOutcome>(expected),
            PlayerbotOpportunities.ChestVerdict(Enum.Parse<PlanOutcome>(exact), Enum.Parse<PlanOutcome>(graph), Enum.Parse<PlanOutcome>(nearest)));

    [Fact]
    public async Task AChestBeyondTheNavMesh_IsRefusedInBegin_WithoutStartingAnEpisodeOrReportingUnreachable()
    {
        ChestSpawn islandChest = new(FreeChest, 11, 20f, 0f);
        using var fixture = new NavMeshFixture();
        (int detourX, int detourY) = ((int)MathF.Floor(StartY / 533.3333f), (int)MathF.Floor(StartX / 533.3333f));
        (int fileX, int fileY) = TerrainTile.TileOf(StartX, StartY)!.Value;
        fixture.WriteParams(0);
        fixture.WriteTile(0, fileX, fileY, new CellTile(detourX, detourY, StartX - 2f, StartY - 2f, 6, 6, 1f)
        {
            Height = (_, _) => StartZ,
            Walkable = (_, _) => true,
        });
        var nav = new NavMeshPathfinder(fixture.Directory);
        Assert.True(nav.LoadTile(0, fileX, fileY));
        Task<NavRegionGraph?>? pending = nav.RegionGraphTask(0, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes);
        if (pending is not null) await pending;

        await using Rig rig = await Rig.StartAsync([islandChest]);
        await rig.World(() =>
        {
            WorldCollision.Of(rig.Host.World).Install(pathfinder: nav);
            return true;
        });
        using AnomalyCapture capture = await rig.World(() => PlayerbotAnomalies.CaptureFor(rig.Player));
        OpportunityStep step = await rig.World(() => rig.Bot.Step());
        Assert.False(step.Busy);
        Assert.Null(rig.Bot.Controller.ActiveKey);
        Assert.True(rig.Bot.Controller.Ledger.IsDeferred(KeyOf(islandChest), rig.Clock));
        Assert.Equal(0, capture.Count(Key("unreachable")));
    }

    [Fact]
    public void TryParseLoot_ReadsTheWindowTheServerSends()
    {
        var window = new PacketWriter();
        window.WriteUInt64(77);
        window.WriteByte(1);
        window.WriteUInt32(12);
        window.WriteByte(2);
        for (byte slot = 0; slot < 2; slot++)
        {
            window.WriteByte(slot);
            for (int i = 0; i < 4; i++) window.WriteUInt32(i == 0 ? 5u : 0u);
            window.WriteInt32(0);
            window.WriteByte(0);
        }

        Assert.True(PlayerbotOpportunities.TryParseLoot(window.ToArray(), out PlayerbotOpportunities.LootWindow parsed));
        Assert.Equal(77ul, parsed.Source);
        Assert.Equal(12u, parsed.Coins);
        Assert.Equal(new byte[] { 0, 1 }, parsed.Slots);
        Assert.False(PlayerbotOpportunities.TryParseLoot([1, 2, 3], out _));
    }
}

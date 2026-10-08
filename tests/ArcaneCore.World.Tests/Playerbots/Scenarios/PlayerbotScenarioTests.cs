using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Protocol;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// Scripted managed bots against the real world handlers (docs/areas/playbots.md, "Scenario harness"). Every scenario
/// asserts server state (inventory, money, group roster, persisted mail and quest rows), not just packets.
/// </summary>
public sealed class PlayerbotScenarioTests
{
    [Fact]
    public async Task GroupLoot_TwoBotsKillAWolf_LeaderLootsItem_AndTheMoneyIsSplit()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        await world.RunPassingAsync(new GroupLootScenario());
    }

    [Fact]
    public async Task Trade_ItemForGold_MovesItemAndMoney()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        await world.RunPassingAsync(new TradeItemForGoldScenario());
        // The bots were stopped (saved) by the runner: the traded cloth is in B's persisted inventory.
        int b = await CharacterIdAsync(world, PlayerbotScenarioCatalog.BotB);
        IReadOnlyList<Kernel.Items.InventoryItemData> rows = await world.WithScopeAsync(sp =>
            sp.GetRequiredService<Kernel.Items.IItemStore>().GetInventoryAsync(b));
        Assert.Contains(rows, row => row.Item.Entry == LinenCloth);
    }

    [Fact]
    public async Task Mail_WithItem_IsPersistedThenTakenByTheReceiver()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        await world.RunPassingAsync(new MailItemScenario(world.Time));
        int b = await CharacterIdAsync(world, PlayerbotScenarioCatalog.BotB);
        MailRecord letter = Assert.Single(await world.WithScopeAsync(sp => sp.GetRequiredService<IEconomyStore>().GetMailsAsync(b)));
        Assert.Equal(0u, letter.ItemGuid); // taken
    }

    [Fact]
    public async Task Duel_IsFoughtToCompletion()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        await world.RunPassingAsync(new DuelScenario());
    }

    [Fact]
    public async Task Melee_AgainstACreature_KillsItAndCreditsExperience()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        await world.RunPassingAsync(new MeleeKillScenario());
    }

    [Fact]
    public async Task Quest_AcceptKillTurnIn_IsRewardedAndPersisted()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        await world.RunPassingAsync(new KillQuestScenario());
        int a = await CharacterIdAsync(world, PlayerbotScenarioCatalog.BotA);
        CharacterQuestData saved = await world.WithScopeAsync(sp => sp.GetRequiredService<ICharacterQuestStore>().LoadAsync(a));
        Assert.Contains(saved.Quests, row => row.Quest == KillQuest && row.Rewarded);
    }

    /// <summary>
    /// A reward settlement slower than the shipped 5 s settlement budget, but well inside the scenario's 30 s step timeout, still
    /// rewards the quest. Under full-suite load the settlement's SQLite work (the pre-settlement save, queued behind earlier saves, and
    /// the commit) took more than 5 s of wall time: the budget cancelled it, nothing was rewarded and no SMSG_QUESTGIVER_QUEST_COMPLETE
    /// was sent, so the scenario waited out its whole timeout (Av_ScrapsTurnIn_QuartermasterUpgrade_AndTheLandmine).
    /// </summary>
    [Fact]
    public async Task Quest_IsRewarded_WhenTheSettlementIsSlowerThanTheShippedBudget()
    {
        var store = new SlowQuestRewardCommit(TimeSpan.FromSeconds(7));
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(services =>
            services.AddScoped<ICharacterQuestRewardStore>(sp => store.Wrap(
                new Data.Quests.EfCharacterQuestRewardStore(sp.GetRequiredService<Data.Characters.CharacterDbContext>()))));
        await world.RunPassingAsync(new KillQuestScenario());
        Assert.True(store.Delayed, "the seam never held a commit");
    }

    /// <summary>Holds every quest reward commit for a fixed wall time before it reaches the database, as a loaded SQLite file can.</summary>
    private sealed class SlowQuestRewardCommit(TimeSpan delay)
    {
        private readonly TimeSpan _delay = delay;
        private int _commits;

        public bool Delayed => Volatile.Read(ref _commits) > 0;

        public ICharacterQuestRewardStore Wrap(ICharacterQuestRewardStore inner) => new Store(this, inner);

        private sealed class Store(SlowQuestRewardCommit owner, ICharacterQuestRewardStore inner) : ICharacterQuestRewardStore
        {
            public async Task<QuestRewardCommitResult> CommitAsync(CharacterQuestRewardRequest request, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref owner._commits);
                await Task.Delay(owner._delay, cancellationToken);
                return await inner.CommitAsync(request, cancellationToken);
            }
        }
    }

    [Fact]
    public async Task GroupChat_AndSmoke_BuiltinsPass()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        await world.RunPassingAsync(new SmokeScenario());
        await world.RunPassingAsync(new GroupChatScenario());
    }

    [Fact]
    public async Task AFailingExpectation_FailsTheRun_WithTheStepAndTheLastPackets()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        ScenarioReport report = await world.RunAsync(new DelegateScenario("broken", async context =>
        {
            ScenarioBot a = await context.StepAsync("login", () => context.LoginAsync(PlayerbotScenarioCatalog.BotA));
            await context.StepAsync("say", async () => Assert.True(await a.SayAsync("hello")));
            await context.StepAsync("expect impossible money", () => context.ExpectMoneyAsync(a, 123_456));
            await context.StepAsync("never reached", () => Task.CompletedTask);
        }));
        Assert.False(report.Passed);
        Assert.Equal("expect impossible money", report.FailedStep);
        Assert.Contains("money: expected 123456, got 0", report.Failure);
        Assert.Equal(3, report.Steps.Count);
        Assert.Contains(report.LastPackets[PlayerbotScenarioCatalog.BotA], p => p.Opcode == WorldOpcode.CmsgMessagechat);
        Assert.Contains("FAIL expect impossible money", report.ToString());
        // The runner released the bot: stopped and saved, no longer scripted.
        World.Playerbots.PlayerbotStatus status = world.Bots.Snapshot().Single(s => s.Name == PlayerbotScenarioCatalog.BotA);
        Assert.Equal(Kernel.Characters.ManagedPlayerbotState.Stopped, status.State);
        Assert.False(world.Bots.IsScripted(status.BotId));
    }

    [Fact]
    public async Task ABoundedWait_TimesOutOnTheManualClock()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        uint before = await world.Host.OnWorldAsync(() => world.Host.World.NowMs);
        ScenarioReport report = await world.RunAsync(new DelegateScenario("timeout", context =>
            context.StepAsync("wait for nothing", () => context.WaitUntilAsync("never", () => false, TimeSpan.FromSeconds(5)))));
        Assert.False(report.Passed);
        Assert.StartsWith("timed out waiting for: never", report.Failure);
        uint after = await world.Host.OnWorldAsync(() => world.Host.World.NowMs);
        Assert.InRange(after - before, 5000u, 5200u); // exactly the bounded game time passed (plus the last step)
        // The game budget burns in a fraction of a second; the wait still gave async work its timeout in wall time.
        Assert.True(report.Steps[^1].Wall >= TimeSpan.FromSeconds(5), report.ToString());
        Assert.Contains("game budget spent after", report.Failure);
    }

    [Fact]
    public async Task AManualClockWait_OutlastsSlowOffThreadWork_WithoutAdvancingPastItsGameBudget()
    {
        // A database-backed reply runs on real time while the manual clock runs as fast as the world can tick: under load the
        // whole game budget can be spent long before the reply lands. Here the "reply" is an off-world-thread completion 6 s of
        // wall time away, past the old 3 s grace after a 10 s game budget that burns in well under a second.
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        uint before = await world.Host.OnWorldAsync(() => world.Host.World.NowMs);
        ScenarioReport report = await world.RunAsync(new DelegateScenario("slow-reply", context =>
            context.StepAsync("wait for a slow off-thread reply", async () =>
            {
                Task reply = Task.Delay(TimeSpan.FromSeconds(6));
                await context.WaitUntilAsync("the slow reply", () => reply.IsCompleted, TimeSpan.FromSeconds(10));
            })));
        Assert.True(report.Passed, report.ToString());
        uint after = await world.Host.OnWorldAsync(() => world.Host.World.NowMs);
        Assert.InRange(after - before, 0u, 10_200u); // never more game time than the wait's own budget
    }

    [Fact]
    public async Task ScriptedMode_SuppressesTheBrain_AndDetachRestoresAutonomy()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        var created = await world.Bots.CreateAsync("Brainless", 1, 1);
        Guid id = Assert.IsType<Guid>(created.BotId);
        var controller = new CountingController();
        Assert.True((await world.Bots.StartScriptedAsync(id.ToString(), controller)).Success);
        Assert.True(world.Bots.IsScripted(id));
        await world.Clock.AdvanceAsync(TimeSpan.FromSeconds(2));
        Assert.True(controller.Ticks > 0);
        // The brain never ran: its goal is still the initial one and nothing moved the bot.
        Assert.Equal(Kernel.Characters.PlayerbotGoalKind.Explore, world.Bots.Snapshot().Single(s => s.BotId == id).Goal);
        Assert.True(await world.Bots.SetControllerAsync(id, null));
        Assert.False(world.Bots.IsScripted(id));
        Assert.Equal(1, controller.Detaches);
        int ticks = controller.Ticks;
        await world.Clock.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(ticks, controller.Ticks);
    }

    private static async Task<int> CharacterIdAsync(ScenarioTestWorld world, string name)
        => (await world.WithScopeAsync(sp => sp.GetRequiredService<Kernel.Characters.ICharacterStore>().GetAllIdentitiesAsync()))
            .Single(identity => identity.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Id;

    private sealed class CountingController : World.Playerbots.IPlayerbotController
    {
        public int Ticks;
        public int Detaches;

        public void Tick(World.Playerbots.PlayerbotControllerContext context, uint elapsedMs) => Interlocked.Increment(ref Ticks);

        public void Detached(Guid botId) => Interlocked.Increment(ref Detaches);
    }
}

internal sealed class DelegateScenario(string name, Func<ScenarioContext, Task> run) : IPlayerbotScenario
{
    public string Name => name;

    public string Description => name;

    public Task RunAsync(ScenarioContext context) => run(context);
}

/// <summary>Shared steps of the test scenarios.</summary>
internal static class TestSteps
{
    /// <summary>Stand <paramref name="bot"/> two yards west of a spawn, facing it, and wait until the creature is in its map.</summary>
    public static async Task ApproachAsync(ScenarioContext context, ScenarioBot bot, ObjectGuid creature, float x, float y, float offsetY = 0f)
    {
        await context.PlaceAsync(bot, 0, x - 2f, y + offsetY, StartZ, 0f);
        await context.WaitUntilAsync($"{bot.Name} sees {creature.Entry}", () => bot.RequirePlayerForTests().VisibleObjects.Contains(creature)
            && bot.RequirePlayerForTests().Map?.FindObject(creature) is Creature);
    }

    public static Creature? Find(ScenarioBot bot, ObjectGuid guid) => bot.RequirePlayerForTests().Map?.FindObject(guid) as Creature;

    public static Player RequirePlayerForTests(this ScenarioBot bot) => bot.RequirePlayer();
}

/// <summary>A and B group (free-for-all loot), kill the wolf together; A loots the cloth and the money is shared.</summary>
internal sealed class GroupLootScenario : IPlayerbotScenario
{
    public string Name => "group-loot";

    public string Description => "two bots group, kill a wolf, the leader loots, money is split";

    public async Task RunAsync(ScenarioContext context)
    {
        (ScenarioBot a, ScenarioBot b) = await PlayerbotScenarioCatalog.PairAsync(context);
        await ScenarioSteps.FormGroupAsync(context, a, b);
        await context.StepAsync("free-for-all loot", async () =>
        {
            long mark = b.Mark();
            ScenarioContext.Expect(await a.SetLootMethodAsync(LootMethod.FreeForAll), "loot method refused");
            await b.WaitForPacketAsync(WorldOpcode.SmsgGroupList, ScenarioDecoders.GroupList, g => g.LootMethod == LootMethod.FreeForAll, mark);
        });
        uint aMoney = await a.ReadAsync(p => p.Money);
        uint bMoney = await b.ReadAsync(p => p.Money);
        await context.StepAsync("both reach the wolf", async () =>
        {
            await TestSteps.ApproachAsync(context, a, Wolf, WolfX, WolfY);
            await TestSteps.ApproachAsync(context, b, Wolf, WolfX, WolfY, offsetY: 1.5f);
        });
        await context.StepAsync("both attack until it dies", async () =>
        {
            long mark = a.Mark();
            ScenarioContext.Expect(await a.AttackAsync(Wolf), "A attack refused");
            ScenarioContext.Expect(await b.AttackAsync(Wolf), "B attack refused");
            await a.WaitForPacketAsync(WorldOpcode.SmsgAttackerstateupdate, ScenarioDecoders.AttackerState,
                s => s.Attacker == a.Guid.Value && s.Victim == Wolf.Value, mark);
            await context.WaitUntilAsync("the wolf is dead", () => TestSteps.Find(a, Wolf) is { IsAlive: false }, TimeSpan.FromSeconds(90));
        });
        LootResponseView loot = await context.StepAsync("A opens the corpse", async () =>
        {
            long mark = a.Mark();
            ScenarioContext.Expect(await a.LootAsync(Wolf), "loot refused");
            LootResponseView view = await a.WaitForPacketAsync(WorldOpcode.SmsgLootResponse, ScenarioDecoders.LootResponse, l => l.Source == Wolf.Value, mark);
            ScenarioContext.ExpectEqual(WolfGold, view.Gold, "corpse gold");
            ScenarioContext.Expect(view.Items.Any(i => i.ItemId == LinenCloth), "the cloth is not offered");
            return view;
        });
        await context.StepAsync("A takes the money: it is split", async () =>
        {
            long markA = a.Mark();
            long markB = b.Mark();
            ScenarioContext.Expect(await a.LootMoneyAsync(), "loot money refused");
            uint shareA = await a.WaitForPacketAsync(WorldOpcode.SmsgLootMoneyNotify, ScenarioDecoders.LootMoneyNotify, since: markA);
            uint shareB = await b.WaitForPacketAsync(WorldOpcode.SmsgLootMoneyNotify, ScenarioDecoders.LootMoneyNotify, since: markB);
            ScenarioContext.ExpectEqual(WolfGold, shareA + shareB, "shares");
            await context.ExpectMoneyAsync(a, aMoney + shareA);
            await context.ExpectMoneyAsync(b, bMoney + shareB);
        });
        await context.StepAsync("A takes the cloth", async () =>
        {
            byte slot = loot.Items.First(i => i.ItemId == LinenCloth).Slot;
            ScenarioContext.Expect(await a.LootItemAsync(slot), "autostore refused");
            await context.WaitUntilAsync("the cloth is in A's bags", () => a.RequirePlayerForTests().Inventory.GetItemCount(LinenCloth) == 1);
            ScenarioContext.Expect(await a.ReleaseLootAsync(Wolf), "release refused");
            await context.ExpectItemCountAsync(b, LinenCloth, 0);
            await context.ExpectGroupAsync(a, b);
        });
    }
}

/// <summary>A mails B a Linen Cloth; the letter is persisted with the item; after the delivery delay B takes it.</summary>
internal sealed class MailItemScenario(ScenarioTimeProvider time) : IPlayerbotScenario
{
    public string Name => "mail-item";

    public string Description => "A mails an item to B, B takes it";

    public async Task RunAsync(ScenarioContext context)
    {
        (ScenarioBot a, ScenarioBot b) = await PlayerbotScenarioCatalog.PairAsync(context);
        int receiver = (int)b.Guid.Low;
        ObjectGuid item = await context.StepAsync("A gets the cloth and postage", async () =>
        {
            await context.GiveMoneyAsync(a, 100);
            return await context.GiveItemAsync(a, LinenCloth);
        });
        uint aMoney = await a.ReadAsync(p => p.Money);
        await context.StepAsync("A sends the letter", async () =>
        {
            long mark = a.Mark();
            ScenarioContext.Expect(await a.SendMailAsync(Mailbox, b.Name, "scenario", "a cloth for you", item), "send mail refused");
            MailResultView result = await a.WaitForPacketAsync(WorldOpcode.SmsgSendMailResult, ScenarioDecoders.MailResult, since: mark);
            ScenarioContext.ExpectEqual(MailResult.Ok, result.Result, "send mail result");
            ScenarioContext.ExpectEqual(MailAction.Send, result.Action, "send mail action");
            await context.ExpectItemCountAsync(a, LinenCloth, 0);
            ScenarioContext.Expect(await a.ReadAsync(p => p.Money) < aMoney, "no postage was charged");
        });
        uint mailId = await context.StepAsync("the letter is persisted with the item", async () =>
        {
            IReadOnlyList<MailRecord> mails = await ReadMailAsync(context, receiver);
            MailRecord letter = mails.Count == 1 ? mails[0] : throw new ScenarioAssertionException($"{mails.Count} letters stored for B");
            ScenarioContext.ExpectEqual(LinenCloth, letter.ItemEntry, "stored letter item");
            ScenarioContext.ExpectEqual((uint)a.Guid.Low, letter.SenderId, "stored letter sender");
            return letter.Id;
        });
        await context.StepAsync("after the delivery delay B lists and takes it", async () =>
        {
            time.Advance(TimeSpan.FromSeconds(3601)); // vmangos: letters with items arrive after an hour
            long mark = b.Mark();
            ScenarioContext.Expect(await b.GetMailListAsync(Mailbox), "mail list refused");
            int count = await b.WaitForPacketAsync(WorldOpcode.SmsgMailListResult, ScenarioDecoders.MailListCount, since: mark);
            ScenarioContext.ExpectEqual(1, count, "letters listed for B");
            long taken = b.Mark();
            ScenarioContext.Expect(await b.TakeMailItemAsync(Mailbox, mailId), "take item refused");
            MailResultView result = await b.WaitForPacketAsync(WorldOpcode.SmsgSendMailResult, ScenarioDecoders.MailResult,
                r => r.Action == MailAction.ItemTaken, taken);
            ScenarioContext.ExpectEqual(MailResult.Ok, result.Result, "take item result");
            await context.ExpectItemCountAsync(b, LinenCloth, 1);
            await context.WaitUntilAsync("the stored letter no longer holds the item", () => true);
            IReadOnlyList<MailRecord> after = await ReadMailAsync(context, receiver);
            ScenarioContext.Expect(after.Single().ItemGuid == 0, "the stored letter still holds the item");
        });
    }

    private static async Task<IReadOnlyList<MailRecord>> ReadMailAsync(ScenarioContext context, int receiver)
    {
        await using AsyncServiceScope scope = context.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IEconomyStore>().GetMailsAsync(receiver);
    }
}

/// <summary>One bot fights the wolf in melee to the death; the kill is credited with experience.</summary>
internal sealed class MeleeKillScenario : IPlayerbotScenario
{
    public string Name => "melee-kill";

    public string Description => "a bot kills a wolf in melee and is credited";

    public async Task RunAsync(ScenarioContext context)
    {
        ScenarioBot a = await context.StepAsync("login", () => context.LoginAsync(PlayerbotScenarioCatalog.BotA));
        await context.StepAsync("reach the wolf", () => TestSteps.ApproachAsync(context, a, Wolf, WolfX, WolfY));
        uint xp = await a.ReadAsync(p => p.GetUInt32(UpdateFields.PlayerXp));
        long mark = a.Mark();
        await context.StepAsync("target and swing", async () =>
        {
            ScenarioContext.Expect(await a.AttackAsync(Wolf), "attack refused");
            AttackerStateView swing = await a.WaitForPacketAsync(WorldOpcode.SmsgAttackerstateupdate, ScenarioDecoders.AttackerState,
                s => s.Attacker == a.Guid.Value, mark);
            ScenarioContext.ExpectEqual(Wolf.Value, swing.Victim, "swing victim");
            await context.ExpectAsync(a, "is in combat with the wolf", p => p.Combat.Victim?.Guid == Wolf);
        });
        XpGainView gain = await context.StepAsync("the wolf dies and the kill is credited", async () =>
        {
            await context.WaitUntilAsync("the wolf is dead", () => TestSteps.Find(a, Wolf) is { IsAlive: false }, TimeSpan.FromSeconds(90));
            return await a.WaitForPacketAsync(WorldOpcode.SmsgLogXpgain, ScenarioDecoders.XpGain, g => g.Victim == Wolf.Value, mark);
        });
        await context.StepAsync("experience matches the credit", async () =>
        {
            ScenarioContext.Expect(gain.Kill && gain.TotalXp > 0, "no kill experience");
            ScenarioContext.ExpectEqual(xp + gain.TotalXp, await a.ReadAsync(p => p.GetUInt32(UpdateFields.PlayerXp)), "player XP");
            await context.WaitUntilAsync("A left combat", () => !a.RequirePlayerForTests().Combat.IsInCombat || a.RequirePlayerForTests().Combat.Victim is null);
        });
    }
}

/// <summary>Accept the kill quest at the marshal, kill the kobold, turn the quest in and be rewarded.</summary>
internal sealed class KillQuestScenario : IPlayerbotScenario
{
    public string Name => "kill-quest";

    public string Description => "accept, kill, turn in";

    public async Task RunAsync(ScenarioContext context)
    {
        ScenarioBot a = await context.StepAsync("login", () => context.LoginAsync(PlayerbotScenarioCatalog.BotA));
        (uint money, uint xp) = await a.ReadAsync(p => (p.Money, p.GetUInt32(UpdateFields.PlayerXp)));
        await context.StepAsync("accept at the marshal", async () =>
        {
            await context.PlaceAsync(a, 0, StartX, StartY, StartZ, 0f);
            await context.WaitUntilAsync("the marshal is visible", () => a.RequirePlayerForTests().VisibleObjects.Contains(Giver));
            ScenarioContext.Expect(await a.QuestHelloAsync(Giver), "hello refused");
            ScenarioContext.Expect(await a.AcceptQuestAsync(Giver, KillQuest), "accept refused");
            await context.WaitUntilAsync("the quest is in the log", () => QuestStatusOf(context, a) is (QuestStatus.Incomplete, false));
        });
        await context.StepAsync("kill the kobold", async () =>
        {
            await TestSteps.ApproachAsync(context, a, Kobold, KoboldX, KoboldY);
            long mark = a.Mark();
            ScenarioContext.Expect(await a.AttackAsync(Kobold), "attack refused");
            QuestKillView kill = await a.WaitForPacketAsync(WorldOpcode.SmsgQuestupdateAddKill, ScenarioDecoders.QuestKill,
                k => k.Quest == KillQuest, mark, TimeSpan.FromSeconds(90));
            ScenarioContext.ExpectEqual((KoboldEntry, 1u, 1u), (kill.Entry, kill.Count, kill.Required), "kill credit");
            await context.WaitUntilAsync("the objective is complete", () => QuestStatusOf(context, a) is (QuestStatus.Complete, false));
        });
        await context.StepAsync("turn in and choose the reward", async () =>
        {
            await context.PlaceAsync(a, 0, StartX, StartY, StartZ, 0f);
            await context.WaitUntilAsync("out of combat", () => !a.RequirePlayerForTests().Combat.IsInCombat, TimeSpan.FromSeconds(30));
            long mark = a.Mark();
            ScenarioContext.Expect(await a.CompleteQuestAsync(Giver, KillQuest), "complete refused");
            ScenarioContext.Expect(await a.ChooseQuestRewardAsync(Giver, KillQuest), "choose reward refused");
            QuestCompleteView done = await a.WaitForPacketAsync(WorldOpcode.SmsgQuestgiverQuestComplete, ScenarioDecoders.QuestComplete,
                q => q.Quest == KillQuest, mark);
            ScenarioContext.ExpectEqual(KillQuestMoney, done.Money, "reward money");
        });
        await context.StepAsync("rewarded: money, experience and the settled quest row", async () =>
        {
            QuestNpcFeature quests = context.Services.GetRequiredService<QuestNpcFeature>();
            await quests.WaitForSettlementAsync((int)a.Guid.Low, context.CancellationToken);
            await context.WaitUntilAsync("the quest is rewarded", () => QuestStatusOf(context, a) is (_, true));
            await context.ExpectMoneyAsync(a, money + KillQuestMoney);
            ScenarioContext.Expect(await a.ReadAsync(p => p.GetUInt32(UpdateFields.PlayerXp)) > xp, "no experience was granted");
        });
    }

    private static (QuestStatus, bool)? QuestStatusOf(ScenarioContext context, ScenarioBot bot)
        => context.Services.GetRequiredService<QuestNpcFeature>().Services.StateOf(bot.RequirePlayerForTests())?.Quests.Get(KillQuest)
            is { } entry ? (entry.Status, entry.Rewarded) : null;
}

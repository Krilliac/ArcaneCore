using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Battlegrounds;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;
using Content = ArcaneCore.World.Tests.Playerbots.Battlegrounds.BattlegroundBotTestContent;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Tests.Playerbots.Battlegrounds;

/// <summary>
/// Arathi Basin stables and Alterac Valley objectives. Split from the former <c>PlayerbotBattlegroundWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotBattlegroundWorldTests"/>.
/// </summary>
public sealed class PlayerbotBattlegroundObjectivesWorldTests : PlayerbotBattlegroundWorldTests
{
    [Fact]
    public async Task Ab_AutonomousBot_ClaimsTheStables()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync(null, BattlegroundType.ArathiBasin);
        await RunAsync(world, "bg-ab-stables", async context =>
        {
            ScenarioBot ally = await AutonomousAsync(context, "Bgabally", race: 1);
            ScenarioBot horde = await ScriptedHordeQueuesAsync(context, "Bgabhorde", BattlegroundType.ArathiBasin, Content.HordeAbMaster);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            await PortScriptedAsync(context, horde, 529, park: null);
            await context.StepAsync("the Alliance bot is in the match", () => context.WaitUntilAsync("in the match",
                () => coordinator.Find(ally.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch, TimeSpan.FromSeconds(60)));
            var ab = (ArathiBasin)(await context.BattlegroundOfAsync(ally))!;
            await context.StepAsync("the gates open", () => context.WaitUntilAsync("in progress", () => ab.Status == BattlegroundStatus.InProgress, StartWait));

            long mark = ally.Mark();
            await context.StepAsync("the bot walks to the stables and casts 21651 at the neutral banner: the stables are claimed", async () =>
            {
                try
                {
                    await context.WaitUntilAsync("claimed", () => ab.NodeStatus(ArathiBasin.NodeStables) == ArathiBasin.StatusAllianceContested, TimeSpan.FromSeconds(90));
                }
                catch (ScenarioTimeoutException timedOut)
                {
                    string facts = await context.ReadAsync(() =>
                    {
                        Player p = ally.RequirePlayer();
                        var objects = p.Map!.FindUpdater<Game.GameObjects.GameObjectMapSystem>()!;
                        string banners = string.Join(",", objects.GameObjects.Where(g => g.Spawn is not null && g.Map == p.Map && g.IsSpawned)
                            .Select(g => FormattableString.Invariant($"{g.Entry}/{g.Spawn!.Guid}@{MathF.Sqrt(MathF.Pow(g.X - p.X, 2) + MathF.Pow(g.Y - p.Y, 2) + MathF.Pow(g.Z - p.Z, 2)):F1}")));
                        return FormattableString.Invariant($"status={ab.NodeStatus(ArathiBasin.NodeStables)} casts={coordinator.Find(ally.BotId)?.AI?.BannerCasts} activity={coordinator.Find(ally.BotId)?.AI?.Activity} at=({p.X:F1},{p.Y:F1},{p.Z:F1}) banners={banners} spellGo={ally.Received(WorldOpcode.SmsgSpellGo, SpellIdOf, mark).Count(i => i == PlayerbotBattlegroundAI.BannerSpell)}");
                    });
                    throw new ScenarioAssertionException(timedOut.Message + "; " + facts);
                }
                // The claim came from the bot's own cast: SMSG_SPELL_START and SMSG_SPELL_GO of 21651 reached it, and the match credits it.
                ScenarioContext.Expect(ally.Received(WorldOpcode.SmsgSpellGo, SpellIdOf, mark).Contains(PlayerbotBattlegroundAI.BannerSpell), "no SMSG_SPELL_GO of 21651");
                ScenarioContext.Expect(ally.Received(WorldOpcode.SmsgSpellStart, SpellIdOf, mark).Contains(PlayerbotBattlegroundAI.BannerSpell), "no SMSG_SPELL_START of 21651");
                ScenarioContext.ExpectEqual(1u, await context.ReadAsync(() => ((AbScore)ab.ScoreOf(ally.Guid)!).BasesAssaulted), "the bot's bases assaulted");
                ScenarioContext.Expect(coordinator.Find(ally.BotId)?.AI?.BannerCasts >= 1, "the AI submitted no banner cast");
            });
        });
    }

    [Fact]
    public async Task Ab_WithTheRealGooberNeutralBanner_AutonomousBotClaimsTheStables_ThroughTheTriggerSpell()
    {
        // The real data's neutral banner (classic-db 180087: a goober locked by 1479 whose data10 is 23932 "A-Mid Trigger") is routed
        // (MatchRuntime.RoutesBannerUse), so the coordinator accepts Arathi Basin. A direct use of it is refused (the goober's use runs only
        // through the opening spell), so the bot must cast Opening 21651 at it: the open-lock reaches the goober, whose use casts 23932 at the
        // bot, whose script makes the node click (MatchRuntime.OnBannerTriggered).
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync(null, NeutralBannerShape.RealGoober, BattlegroundType.ArathiBasin);
        await RunAsync(world, "bg-ab-goober", async context =>
        {
            ScenarioBot ally = await AutonomousAsync(context, "Bggooberally", race: 1);
            ScenarioBot horde = await ScriptedHordeQueuesAsync(context, "Bggoobhorde", BattlegroundType.ArathiBasin, Content.HordeAbMaster);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            await PortScriptedAsync(context, horde, 529, park: null);
            await context.StepAsync("Arathi Basin is accepted: the Alliance bot queues and is in the match", async () =>
            {
                await WaitExplainedAsync(context, "in the match", () => coordinator.Find(ally.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch,
                    TimeSpan.FromSeconds(60), ally);
                ScenarioContext.Expect(!coordinator.Refusals.Any(r => r.Type == BattlegroundType.ArathiBasin), "Arathi Basin was refused");
            });
            var ab = (ArathiBasin)(await context.BattlegroundOfAsync(ally))!;
            await context.StepAsync("the gates open", () => context.WaitUntilAsync("in progress", () => ab.Status == BattlegroundStatus.InProgress, StartWait));

            long mark = ally.Mark();
            await context.StepAsync("the bot casts 21651 at the neutral goober banner, which casts 23932 at it: the stables are claimed", async () =>
            {
                await WaitExplainedAsync(context, "claimed", () => ab.NodeStatus(ArathiBasin.NodeStables) == ArathiBasin.StatusAllianceContested,
                    TimeSpan.FromSeconds(90), ally);
                ScenarioContext.Expect(ally.Received(WorldOpcode.SmsgSpellStart, SpellIdOf, mark).Contains(PlayerbotBattlegroundAI.BannerSpell), "no SMSG_SPELL_START of 21651");
                ScenarioContext.Expect(ally.Received(WorldOpcode.SmsgSpellGo, SpellIdOf, mark).Contains(PlayerbotBattlegroundAI.BannerSpell), "no SMSG_SPELL_GO of 21651");
                ScenarioContext.Expect(ally.Received(WorldOpcode.SmsgSpellGo, SpellIdOf, mark).Contains(Content.StablesTrigger), "no SMSG_SPELL_GO of the trigger spell 23932");
                ScenarioContext.Expect(coordinator.Find(ally.BotId)?.AI?.BannerCasts >= 1, "the AI submitted no banner cast");
                ScenarioContext.ExpectEqual(1u, await context.ReadAsync(() => ((AbScore)ab.ScoreOf(ally.Guid)!).BasesAssaulted), "the bot's bases assaulted");
            });
        });
    }

    [Fact]
    public async Task Ab_WithANeutralGooberWhoseSpellIsNoBannerTrigger_IsRefused_NoBotQueues()
    {
        // A goober banner whose spell (data10) is not a banner trigger spell is not routed to the node click: a 21651 cast at it would claim
        // nothing, so Arathi Basin is refused (MissingContent) and no bot queues, though a battlemaster stands beside it.
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync(null, NeutralBannerShape.UnroutedGoober, BattlegroundType.ArathiBasin);
        await RunAsync(world, "bg-ab-unrouted", async context =>
        {
            ScenarioBot ally = await AutonomousAsync(context, "Bgunrouteda", race: 1);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            await context.StepAsync("Arathi Basin is refused for missing content and nothing queues", async () =>
            {
                await context.WaitUntilAsync("the refusal is noted", () => coordinator.Refusals.Contains((BattlegroundType.ArathiBasin, PlayerbotBattlegroundRefusal.MissingContent)),
                    TimeSpan.FromSeconds(10));
                await context.IdleAsync(TimeSpan.FromSeconds(30));
                ScenarioContext.ExpectEqual(0, coordinator.Totals.Queued, "queues joined");
                ScenarioContext.Expect(!coordinator.Holds(ally.BotId), "the bot went for a battleground");
                ScenarioContext.ExpectEqual((0, 0), await context.ReadAsync(() => Manager(context).QueuedTeamCounts(BattlegroundType.ArathiBasin)), "Arathi Basin queue");
            });
        });
    }

    // --- Alterac Valley ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Av_TwoAutonomousBots_Queue_PortIntoOneMatch_AndPlayTheObjective()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync(null, BattlegroundType.AlteracValley);
        await RunAsync(world, "bg-av-flow", async context =>
        {
            ScenarioBot ally = await AutonomousAsync(context, "Bgavally", race: 1);
            ScenarioBot horde = await AutonomousAsync(context, "Bgavhorde", race: 2);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            BattlegroundFeature feature = context.Services.GetRequiredService<BattlegroundFeature>();

            Battleground match = await context.StepAsync("both bots queue for Alterac Valley and port into the match", async () =>
            {
                await WaitExplainedAsync(context, "both bots are in the Alterac Valley match",
                    () => coordinator.Find(ally.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch
                        && coordinator.Find(horde.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch,
                    TimeSpan.FromSeconds(60), ally, horde);
                ScenarioContext.Expect(!coordinator.Refusals.Any(r => r.Type == BattlegroundType.AlteracValley), "Alterac Valley was refused");
                Battleground? a = await context.ReadAsync(() => feature.BattlegroundOf(ally.Guid));
                Battleground? h = await context.ReadAsync(() => feature.BattlegroundOf(horde.Guid));
                ScenarioContext.Expect(a is AlteracValley && ReferenceEquals(a, h), "the bots are not in one Alterac Valley match");
                coordinator.Options.QueueChancePerMinute = 0f;
                return a!;
            });

            await context.StepAsync("the gates open and the bots advance in Alterac Valley", async () =>
            {
                await context.WaitUntilAsync("the match is in progress", () => match.Status == BattlegroundStatus.InProgress, StartWait);
                await WaitExplainedAsync(context, "the Alliance bot advances or fights in Alterac Valley",
                    () => coordinator.Find(ally.BotId)?.AI?.Activity is "av-advance" or "av-assault" or "av-claim-banner" or "fighting",
                    TimeSpan.FromSeconds(30), ally, horde);
            });
        });
    }

    /// <summary>
    /// That protection is checked against a guard far stronger than the fixture's: hitting for 90 to 100 it empties an unprotected bot in one
    /// swing, yet the protected bot still kills it within the 90 s the test allows (the kill took 14 to 20 of the guard's 2 s swings: about
    /// 2000 damage, 4000 if every one were a critical hit, against 5000 health), stays alive and the guard dies.
    /// </summary>
    [Fact]
    public async Task Av_AutonomousBot_KillsTheGuard_WithoutDying_EvenWhenTheGuardHitsFarHarderThanTheFixture()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync(null, BattlegroundType.AlteracValley);
        await RunAsync(world, "bg-av-guard-duel", async context =>
        {
            ScenarioBot ally = await AutonomousAsync(context, "Bgavduel", race: 1);
            ScenarioBot horde = await ScriptedHordeQueuesAsync(context, "Bgavduelh", BattlegroundType.AlteracValley, Content.HordeAvMaster);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            await PortScriptedAsync(context, horde, 30, (-100f, 300f, Content.AvFloor));
            await context.StepAsync("the Alliance bot is in the match", () => context.WaitUntilAsync("in the match",
                () => coordinator.Find(ally.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch, TimeSpan.FromSeconds(60)));
            var av = (AlteracValley)(await context.BattlegroundOfAsync(ally))!;
            coordinator.Options.QueueChancePerMinute = 0f;
            long since = ally.Mark();
            await FortifyAsync(context, ally);
            await context.StepAsync("the gates open", () => context.WaitUntilAsync("in progress", () => av.Status == BattlegroundStatus.InProgress, StartWait));

            Game.Creatures.Creature guard = await context.ReadAsync(() =>
            {
                Game.Creatures.Creature found = ally.RequirePlayer().Map!.FindUpdater<Game.Creatures.CreatureMapSystem>()!
                    .Creatures.Single(c => c.Entry == Content.AvGuardEntry);
                found.SetFloat(UpdateFields.UnitFieldMindamage, 90f);
                found.SetFloat(UpdateFields.UnitFieldMaxdamage, 100f);
                return found;
            });
            await context.StepAsync("the bot kills the hard-hitting guard and lives", async () =>
            {
                await WaitExplainedAsync(context, "the guard is dead", () => !guard.IsAlive, TimeSpan.FromSeconds(90), ally);
                ScenarioContext.Expect(await context.ReadAsync(() => ally.RequirePlayer().IsAlive), "the bot died in the guard fight");
                ScenarioContext.ExpectEqual(5000u, await context.ReadAsync(() => ally.RequirePlayer().MaxHealth), "the bot's max health was reset after the fortification");
                (uint total, uint largest) = ExpectOnlyTheGuardHurtTheBot(ally, guard, since);
                ScenarioContext.Expect(total >= 90 && largest > 5,
                    $"the 90 to 100 damage override did not take effect (the guard did {total} damage, largest hit {largest}; the fixture's guard hits for at most 5)");
            });
        });
    }

    /// <summary>
    /// The Alterac Valley objective flow through the real paths: the Alliance bot leaves its start cave for the Horde-held Iceblood graveyard
    /// (node 4, the first actionable step of its lane), kills the hostile guard before the banner, casts 21651 at the Horde banner (178364) and
    /// the match turns the opened banner into the assault (node 4 Alliance-owned and assaulted, its graveyards-assaulted score 1). Killed
    /// there, it is released to its team's nearest held graveyard (the start cave: Iceblood is only assaulted) and waits within the spirit
    /// guide's wave for the resurrection instead of running back to its body 60 yards away.
    /// </summary>
    [Fact]
    public async Task Av_AutonomousBot_KillsTheGuard_AssaultsTheIcebloodGraveyard_AndWhenKilledWaitsForTheSpiritGuide()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync(null, BattlegroundType.AlteracValley);
        await RunAsync(world, "bg-av-assault", async context =>
        {
            ScenarioBot ally = await AutonomousAsync(context, "Bgavassa", race: 1);
            ScenarioBot horde = await ScriptedHordeQueuesAsync(context, "Bgavassh", BattlegroundType.AlteracValley, Content.HordeAvMaster);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            await PortScriptedAsync(context, horde, 30, (-100f, 300f, Content.AvFloor));
            await context.StepAsync("the Alliance bot is in the match", () => context.WaitUntilAsync("in the match",
                () => coordinator.Find(ally.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch, TimeSpan.FromSeconds(60)));
            var av = (AlteracValley)(await context.BattlegroundOfAsync(ally))!;
            coordinator.Options.QueueChancePerMinute = 0f;
            long since = ally.Mark();
            await FortifyAsync(context, ally);
            await context.StepAsync("the gates open", () => context.WaitUntilAsync("in progress", () => av.Status == BattlegroundStatus.InProgress, StartWait));

            Game.Creatures.Creature guard = await context.ReadAsync(() => ally.RequirePlayer().Map!.FindUpdater<Game.Creatures.CreatureMapSystem>()!
                .Creatures.Single(c => c.Entry == Content.AvGuardEntry));
            await context.StepAsync("the bot fights and kills the hostile guard on its way", async () =>
            {
                await WaitExplainedAsync(context, "the guard is dead", () => !guard.IsAlive, TimeSpan.FromSeconds(90), ally);
                _ = ExpectOnlyTheGuardHurtTheBot(ally, guard, since);
            });

            long mark = ally.Mark();
            await context.StepAsync("the bot casts 21651 at the Horde banner and Iceblood is assaulted by the Alliance", async () =>
            {
                await WaitExplainedAsync(context, "Iceblood assaulted",
                    () => av.NodeOwner(AlteracValley.NodeIcebloodGrave) == 0 && av.NodeState(AlteracValley.NodeIcebloodGrave) == AlteracValley.PointAssaulted,
                    TimeSpan.FromSeconds(90), ally);
                ScenarioContext.ExpectEqual(1, await context.ReadAsync(() => av.NodeTotalOwner(AlteracValley.NodeIcebloodGrave)), "the total owner (Horde until taken)");
                ScenarioContext.Expect(ally.Received(WorldOpcode.SmsgSpellGo, SpellIdOf, mark).Contains(PlayerbotBattlegroundAI.BannerSpell), "no SMSG_SPELL_GO of 21651");
                ScenarioContext.ExpectEqual(1u, await context.ReadAsync(() => ((AvScore)av.ScoreOf(ally.Guid)!).GraveyardsAssaulted), "the bot's graveyards assaulted");
                ScenarioContext.Expect(coordinator.Find(ally.BotId)?.AI?.BannerCasts >= 1, "the AI submitted no banner cast");
            });

            System.Numerics.Vector3 body = default;
            await context.StepAsync("killed at the graveyard, the bot waits at the spirit guide and never runs to its body", async () =>
            {
                body = await context.ReadAsync(() =>
                {
                    Player p = ally.RequirePlayer();
                    p.Health = 0;
                    p.Map!.Combat.KillPlayer(p);
                    return new System.Numerics.Vector3(p.X, p.Y, p.Z);
                });
                await WaitExplainedAsync(context, "the ghost is at the spirit guide", () => coordinator.Find(ally.BotId)?.AI?.Activity == "spirit-guide",
                    TimeSpan.FromSeconds(60), ally);
                float closestToBody = float.MaxValue, farthestFromGuide = 0f;
                for (int second = 0; second < 40; second++)
                {
                    await context.IdleAsync(TimeSpan.FromSeconds(1));
                    (bool ghost, System.Numerics.Vector3 at) = await context.ReadAsync(() =>
                    {
                        Player p = ally.RequirePlayer();
                        return ((p.Flags & PlayerFlags.Ghost) != 0, new System.Numerics.Vector3(p.X, p.Y, p.Z));
                    });
                    ScenarioContext.Expect(ghost, $"the bot is no ghost after {second} s (it reclaimed its body)");
                    closestToBody = MathF.Min(closestToBody, System.Numerics.Vector3.Distance(at, body));
                    farthestFromGuide = MathF.Max(farthestFromGuide, System.Numerics.Vector3.Distance(at, Content.AvSpiritGuideAt));
                }

                ScenarioContext.Expect(farthestFromGuide <= 20f, $"the ghost left the spirit guide's wave ({farthestFromGuide:F1} yd)");
                ScenarioContext.Expect(closestToBody >= 30f, $"the ghost ran towards its body ({closestToBody:F1} yd)");
                ScenarioContext.ExpectEqual("spirit-guide", coordinator.Find(ally.BotId)?.AI?.Activity, "activity");
            });
        });
    }
}

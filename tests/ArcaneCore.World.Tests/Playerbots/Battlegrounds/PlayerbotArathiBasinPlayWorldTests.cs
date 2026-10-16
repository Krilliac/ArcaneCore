using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Npc;
using ArcaneCore.Protocol;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Playerbots.Battlegrounds;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Playerbots.Dungeon;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Content = ArcaneCore.World.Tests.Playerbots.Battlegrounds.BattlegroundBotTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Battlegrounds;

/// <summary>
/// An autonomous bot playing Arathi Basin end to end (the real handlers, the manual clock, <see cref="BattlegroundBotTestContent"/>): it
/// assaults the Horde's claim of the stables through its own 21651 cast, defends the stables against an enemy that comes to them, steps on
/// the stables' buff, and, dead, waits at the spirit guide for its wave instead of running to its corpse. The Horde opponent is a scripted
/// managed bot (it never fights back; the coordinator never drives it).
/// </summary>
public sealed class PlayerbotArathiBasinPlayWorldTests
{
    private sealed class Scenario(string name, Func<ScenarioContext, Task> body) : IPlayerbotScenario
    {
        public string Name => name;

        public string Description => name;

        public Task RunAsync(ScenarioContext context) => body(context);
    }

    private static readonly TimeSpan StartWait = TimeSpan.FromMinutes(3);

    /// <summary>Where the scripted Horde player waits, out of the Alliance bot's sight (the test Horde start).</summary>
    private static readonly (float X, float Y, float Z) HordeParking = (900f, 900f, Content.AbFloor);

    private static async Task RunAsync(ScenarioTestWorld world, string name, Func<ScenarioContext, Task> body)
    {
        ScenarioReport report = await world.RunAsync(new Scenario(name, body), new ScenarioRunOptions
        {
            StepTimeout = TimeSpan.FromSeconds(60),
            MaxDuration = TimeSpan.FromMinutes(15),
        });
        Assert.True(report.Passed, report + Environment.NewLine + world.Trace());
    }

    private static BattlegroundManager Manager(ScenarioContext context) => context.Services.GetRequiredService<BattlegroundFeature>().Manager;

    private static PlayerbotBattlegroundCoordinator Coordinator(ScenarioContext context) => context.Services.GetRequiredService<PlayerbotBattlegroundCoordinator>();

    /// <summary>An Alliance bot and a scripted Horde player in one Arathi Basin match, its gates open; the Horde player parked far away.</summary>
    private static async Task<(ScenarioBot Ally, ScenarioBot Horde, ArathiBasin Ab)> MatchAsync(ScenarioContext context, string prefix)
    {
        ScenarioBot ally = await context.LoginAsync(prefix + "ally", race: 1, characterClass: 1);
        await context.AutonomousAsync(ally);
        ScenarioBot horde = await context.LoginAsync(prefix + "horde", race: 2, characterClass: 1);
        await context.StepAsync("the Horde player queues for Arathi Basin", async () =>
        {
            ObjectGuid guid = ObjectGuid.WithEntry(HighGuid.Unit, Content.HordeAbMaster, Content.HordeAbMaster);
            Vector3 at = Content.MasterAt(Content.HordeAbMaster);
            await context.PlaceAsync(horde, 0, at.X - 1.5f, at.Y, at.Z, 0f);
            long mark = horde.Mark();
            ScenarioContext.Expect(await horde.BattlemasterHelloAsync(guid), "hello refused");
            await horde.WaitForPacketAsync(WorldOpcode.SmsgBattlefieldList, p => p, since: mark);
            ScenarioContext.Expect(await horde.JoinBattlegroundAsync(guid, BattlegroundManager.MapOfType(BattlegroundType.ArathiBasin)), "join refused");
            await context.WaitUntilAsync("the Horde player is queued", () => Manager(context).StateOf(horde.Guid).InAnyQueue);
        });
        await context.StepAsync("the Horde player ports in and waits far away", async () =>
        {
            await context.WaitUntilAsync("invited", () => Manager(context).StateOf(horde.Guid).InvitedInstance(BattlegroundQueueType.ArathiBasin) != 0, StartWait);
            ScenarioContext.Expect(await horde.PortBattlegroundAsync(529), "port refused");
            await context.WaitUntilAsync("in the match", () => horde.Session!.Player is { IsInWorld: true } p && p.MapId == 529
                && context.Services.GetRequiredService<BattlegroundFeature>().BattlegroundOf(p.Guid)?.PlayerTeam(p.Guid) is not null);
            await context.PlaceAsync(horde, 529, HordeParking.X, HordeParking.Y, HordeParking.Z);
        });
        await context.StepAsync("the Alliance bot is in the match", () => context.WaitUntilAsync("in the match",
            () => Coordinator(context).Find(ally.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch, TimeSpan.FromSeconds(60)));
        var ab = (ArathiBasin)(await context.BattlegroundOfAsync(ally))!;
        await context.StepAsync("the gates open", () => context.WaitUntilAsync("in progress", () => ab.Status == BattlegroundStatus.InProgress, StartWait));
        return (ally, horde, ab);
    }

    private static string Facts(ScenarioContext context, ScenarioBot bot, ArathiBasin ab)
    {
        Player p = bot.RequirePlayer();
        PlayerbotBattlegroundAI? ai = Coordinator(context).Find(bot.BotId)?.AI;
        return FormattableString.Invariant(
            $"stables={ab.NodeStatus(ArathiBasin.NodeStables)} activity={ai?.Activity} target={ai?.Target?.Guid} casts={ai?.BannerCasts} alive={p.IsAlive} health={p.Health}/{p.MaxHealth} at=({p.X:F1},{p.Y:F1},{p.Z:F1}) {Coordinator(context).Describe(bot.BotId)}");
    }

    private static async Task WaitExplainedAsync(ScenarioContext context, string what, Func<bool> condition, TimeSpan timeout, ScenarioBot bot, ArathiBasin ab)
    {
        try
        {
            await context.WaitUntilAsync(what, condition, timeout);
        }
        catch (ScenarioTimeoutException timedOut)
        {
            throw new ScenarioAssertionException($"{timedOut.Message}; {await context.ReadAsync(() => Facts(context, bot, ab))}");
        }
    }

    private static float Distance(WorldObject a, Vector3 b) => Vector3.Distance(new Vector3(a.X, a.Y, a.Z), b);

    [Fact]
    public async Task Ab_Bot_AssaultsTheHordesClaimOfTheStables_ThroughItsOwnBannerCast()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync(null, BattlegroundType.ArathiBasin);
        await RunAsync(world, "bg-ab-assault-enemy", async context =>
        {
            (ScenarioBot ally, ScenarioBot horde, ArathiBasin ab) = await MatchAsync(context, "Abas");
            await context.StepAsync("the Horde claims the stables (contested by the Horde)", async () =>
            {
                await context.ReadAsync(() =>
                {
                    ab.EventPlayerClickedOnFlag(horde.Guid, new BattlegroundObjectUse(180087, ArathiBasin.NodeStables, ArathiBasin.StatusNeutral, true));
                    return true;
                });
                ScenarioContext.ExpectEqual(ArathiBasin.StatusHordeContested, await context.ReadAsync(() => ab.NodeStatus(ArathiBasin.NodeStables)), "stables");
            });

            await context.StepAsync("the Alliance bot casts 21651 at the Horde's banner: the stables are contested by the Alliance", async () =>
            {
                await WaitExplainedAsync(context, "assaulted", () => ab.NodeStatus(ArathiBasin.NodeStables) == ArathiBasin.StatusAllianceContested,
                    TimeSpan.FromSeconds(90), ally, ab);
                ScenarioContext.ExpectEqual(1u, await context.ReadAsync(() => ((AbScore)ab.ScoreOf(ally.Guid)!).BasesAssaulted), "the bot's bases assaulted");
                ScenarioContext.Expect(Coordinator(context).Find(ally.BotId)?.AI?.BannerCasts >= 1, "the AI submitted no banner cast");
            });
        });
    }

    [Fact]
    public async Task Ab_Defender_FightsAnEnemyThatComesToItsNode_BeyondItsOwnEngageRange()
    {
        // The stables are the bot's only node once claimed: it defends them. An enemy 38 yards from the banner is beyond the 30 yards a bot
        // engages on its own, but within the 40 yards the defence covers: the defender goes for it.
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync(null, BattlegroundType.ArathiBasin);
        await RunAsync(world, "bg-ab-defend", async context =>
        {
            (ScenarioBot ally, ScenarioBot horde, ArathiBasin ab) = await MatchAsync(context, "Abde");
            await context.StepAsync("the bot claims the stables", () => WaitExplainedAsync(context, "claimed",
                () => ab.NodeStatus(ArathiBasin.NodeStables) == ArathiBasin.StatusAllianceContested, TimeSpan.FromSeconds(90), ally, ab));

            await context.StepAsync("the bot stands by the stables banner, defending", () => WaitExplainedAsync(context, "defending",
                () => Coordinator(context).Find(ally.BotId)?.AI?.Activity == "defending" && Distance(ally.RequirePlayer(), Content.Stables) <= PlayerbotBattlegroundAI.GuardYards,
                TimeSpan.FromSeconds(30), ally, ab));

            Vector3 intruder = Content.Stables + new Vector3(38f, 0f, 0f);
            await context.PlaceAsync(horde, 529, intruder.X, intruder.Y, intruder.Z);
            await context.StepAsync("the Horde player comes 38 yards from the banner: the defender fights it", async () =>
            {
                ScenarioContext.Expect(await context.ReadAsync(() => Distance(ally.RequirePlayer(), intruder) > PlayerbotBattlegroundAI.EngageYards),
                    "the intruder is within the bot's own engage range: the test proves nothing");
                await WaitExplainedAsync(context, "fighting the intruder",
                    () => Coordinator(context).Find(ally.BotId)?.AI?.Target?.Guid == horde.Guid, TimeSpan.FromSeconds(20), ally, ab);
                await WaitExplainedAsync(context, "swinging at the intruder",
                    () => ally.RequirePlayer().Combat.Victim?.Guid == horde.Guid || horde.RequirePlayer().Health < horde.RequirePlayer().MaxHealth,
                    TimeSpan.FromSeconds(30), ally, ab);
            });
        });
    }

    [Fact]
    public async Task Ab_Bot_StepsOnTheBuffAtItsNode()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync(null, BattlegroundType.ArathiBasin);
        await RunAsync(world, "bg-ab-buff", async context =>
        {
            (ScenarioBot ally, ScenarioBot _, ArathiBasin ab) = await MatchAsync(context, "Abbu");
            GameObject? buff = null;
            await context.StepAsync("the stables' buff is up (one of three, at random), on the test floor", async () =>
            {
                await context.WaitUntilAsync("a buff spawned near the stables", () =>
                {
                    Player p = ally.RequirePlayer();
                    buff = p.Map!.FindUpdater<GameObjectMapSystem>()!.GameObjects.FirstOrDefault(g => g.IsSpawned
                        && BattlegroundConstants.BuffEntries.Contains(g.Entry) && Vector2.Distance(new(g.X, g.Y), new(1185.566f, 1184.629f)) < 1f);
                    return buff is not null;
                }, TimeSpan.FromSeconds(30));
                // The real buff stands at the real ground height (-56.4); the test map is flat at -50, and the trap fires within 3 yards.
                await context.ReadAsync(() =>
                {
                    buff!.SetPosition(buff.X, buff.Y, Content.AbFloor, buff.Orientation);
                    return true;
                });
            });

            uint spell = Content.BuffSpellOf(buff!.Entry);
            await context.StepAsync($"the hurt bot steps on the buff ({buff.Entry}) by the stables and wears its spell {spell}", () => WaitExplainedAsync(context, "buffed",
                () =>
                {
                    Player p = ally.RequirePlayer();
                    // Kept hurt, so even the restoration buff is worth taking.
                    if (p.IsAlive && p.Health > p.MaxHealth / 2) p.Health = p.MaxHealth / 2;
                    return context.Services.GetRequiredService<SpellFeature>().System.HasAura(p, spell);
                }, TimeSpan.FromSeconds(90), ally, ab));
        });
    }

    [Fact]
    public async Task Ab_DeadBot_WaitsAtTheSpiritGuide_InsteadOfRunningToItsCorpse()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync(null, BattlegroundType.ArathiBasin);
        await RunAsync(world, "bg-ab-spirit-guide", async context =>
        {
            (ScenarioBot ally, ScenarioBot _, ArathiBasin ab) = await MatchAsync(context, "Absg");
            // The Alliance start graveyard (890, the test's WorldSafeLoc) and its spirit guide; the bot dies 150 yards away.
            var graveyard = new Vector3(Content.Stables.X - 20f, Content.Stables.Y, Content.AbFloor);
            var deathAt = new Vector3(Content.Stables.X + 130f, Content.Stables.Y, Content.AbFloor);
            await context.PlaceAsync(ally, 529, deathAt.X, deathAt.Y, deathAt.Z);
            await context.StepAsync("a spirit guide stands at the graveyard; the bot is killed far from it", async () =>
            {
                await context.ReadAsync(() =>
                {
                    Player p = ally.RequirePlayer();
                    DungeonBotHost.AddCreature(p.Map!, 13116u, graveyard.X, graveyard.Y, graveyard.Z, (uint)NpcFlags.SpiritGuide, context.World.NowMs, faction: 12);
                    p.Health = 0;
                    p.Map!.Combat.KillPlayer(p);
                    return true;
                });
                await WaitExplainedAsync(context, "released at the graveyard", () => ally.RequirePlayer() is { } p
                    && (p.Flags & PlayerFlags.Ghost) != 0 && Distance(p, graveyard) <= 20f, TimeSpan.FromSeconds(30), ally, ab);
            });

            await context.StepAsync("the ghost stays by the spirit guide for its wave and never runs to its corpse", async () =>
            {
                float farthest = 0f;
                for (int i = 0; i < 20; i++)
                {
                    uint until = await context.ReadAsync(() => context.World.NowMs + 1_000);
                    await context.WaitUntilAsync("a second passes", () => unchecked((int)(context.World.NowMs - until)) >= 0, TimeSpan.FromSeconds(5));
                    (bool ghost, float fromGraveyard) = await context.ReadAsync(() =>
                    {
                        Player p = ally.RequirePlayer();
                        return ((p.Flags & PlayerFlags.Ghost) != 0, Distance(p, graveyard));
                    });
                    if (!ghost) break;
                    farthest = MathF.Max(farthest, fromGraveyard);
                }

                ScenarioContext.Expect(farthest <= 20f, FormattableString.Invariant($"the ghost left the spirit guide for its corpse: {farthest:F1} yd away at most"));
                ScenarioContext.Expect(await context.ReadAsync(() => Distance(ally.RequirePlayer(), deathAt) > 100f), "the ghost walked back to its corpse");
                ScenarioContext.ExpectEqual("corpse-run", Coordinator(context).Find(ally.BotId)?.AI?.Activity, "activity (the recovery drives the dead bot)");
            });
        });
    }
}

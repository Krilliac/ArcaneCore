using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Battlegrounds;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Playerbots.Dungeon;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Battlegrounds.PlayerbotBattlegroundWorldTests;
using Content = ArcaneCore.World.Tests.Playerbots.Battlegrounds.BattlegroundBotTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Battlegrounds;

/// <summary>
/// How bots play Warsong Gulch and get there, end to end in the battleground scenario world (<see cref="BattlegroundBotTestWorld"/>: real
/// handlers, the manual clock, managed bots; a compact synthetic Warsong Gulch on map 489, since the test world has no map 489 navigation
/// data). Live 2026-10-16 ~20:20 UTC: a real Horde player in Warsong Gulch with four bots reported "bots just stand there" and "bots running
/// to their bodies instead of waiting to be rezzed by the spirit healer"; world.debug.log showed attackers holding with no flag to take
/// (activity no-flag, Zoustak/Zarmun/Ziavos/Vigeigrern), casters below 10% mana holding at range in the Horde flag room (Bokae, Poforound),
/// and bots walking again and again to battlemasters they could not reach (Gitrol 62 times; the Warsong Emissary spawns of the Call to
/// Arms event while it was off).
/// </summary>
public sealed class PlayerbotWarsongPlayWorldTests
{
    private static readonly string IdleInMatch = PlayerbotAnomalies.Key("bg", "idle-in-match");
    private static readonly string ObjectiveUnreachable = PlayerbotAnomalies.Key("bg", "objective-unreachable");

    private static Task<AnomalyCapture> CaptureAsync(ScenarioBot bot) => bot.ReadAsync(p => PlayerbotAnomalies.CaptureFor(p));

    private static Vector3 At(Player p) => new(p.X, p.Y, p.Z);

    /// <summary>An Alliance bot and a parked scripted Horde opponent in one Warsong Gulch match whose gates are open.</summary>
    private static async Task<(ScenarioBot Ally, ScenarioBot Horde, WarsongGulch Wsg)> MatchAsync(ScenarioContext context, string name, byte allyClass = 1,
        Vector3? park = null)
    {
        ScenarioBot ally = await context.LoginAsync(name, 1, allyClass);
        await context.AutonomousAsync(ally);
        ScenarioBot horde = await ScriptedHordeQueuesAsync(context, name + "h", BattlegroundType.WarsongGulch, Content.HordeWsgMaster);
        PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
        Vector3 at = park ?? Content.HordeParking;
        await PortScriptedAsync(context, horde, 489, (at.X, at.Y, at.Z));
        await context.StepAsync("the Alliance bot is in the match", () => context.WaitUntilAsync("in the match",
            () => coordinator.Find(ally.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch, TimeSpan.FromSeconds(60)));
        var wsg = (WarsongGulch)(await context.BattlegroundOfAsync(ally))!;
        coordinator.Options.QueueChancePerMinute = 0f;
        await context.StepAsync("the gates open", () => context.WaitUntilAsync("in progress", () => wsg.Status == BattlegroundStatus.InProgress, StartWait));
        return (ally, horde, wsg);
    }

    /// <summary>
    /// After a capture both flags are away for 23 s (BattleGroundWS.cpp: both flag events despawn, FlagRespawnTimeMs). Before, an attacker
    /// found no stand object to walk to and held where it stood ("no-flag", idle-in-match), in its own flag room 60 yards from the enemy flag;
    /// mangosbot's BGTactics::selectObjective sends it to the enemy flag position (WS_FLAG_POS_*) whatever the flag's state, so it is there when
    /// the flag comes back.
    /// </summary>
    [Fact]
    public async Task AfterACapture_TheAttackerWaitsAtTheEnemyStand_WhileTheFlagRespawns_InsteadOfHolding()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync();
        await RunAsync(world, "bg-wsg-respawn-wait", async context =>
        {
            (ScenarioBot ally, _, WarsongGulch wsg) = await MatchAsync(context, "Bgrespawn");
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            await context.StepAsync("the bot captures once", () => context.WaitUntilAsync("captured", () => wsg.TeamScore(Team.Alliance) == 1, TimeSpan.FromSeconds(90)));
            ScenarioContext.Expect(wsg.FlagState(Team.Horde) == WsgFlagState.WaitRespawn, $"the Horde flag is {wsg.FlagState(Team.Horde)} right after the capture");

            await context.StepAsync("during the respawn the bot walks to the Horde stand", async () =>
            {
                // 60 yards at run speed is about nine seconds; the flag is back after 23.
                await WaitExplainedAsync(context, "at the Horde stand while the flag respawns",
                    () => wsg.FlagState(Team.Horde) == WsgFlagState.WaitRespawn
                        && Vector3.Distance(At(ally.RequirePlayer()), Content.HordeFlag) <= PlayerbotBattlegroundAI.GuardYards + 2f,
                    TimeSpan.FromSeconds(18), ally);
                ScenarioContext.Expect(coordinator.Describe(ally.BotId)?.EndsWith(":waiting-flag", StringComparison.Ordinal) == true, coordinator.Describe(ally.BotId) ?? "none");
            });
        });
    }

    /// <summary>
    /// A caster with nothing to cast (no spell for the fight, or below 10% mana) closes in and swings. Before, the battleground AI never
    /// did (idleTooLong was always false): it held at its 25-yard range for the whole fight (live: Bokae, mage, and Poforound, shaman, in
    /// the Horde flag room below 10% mana for 45 s of one fight). vmangos CombatBotBaseAI::BeginChasing; PlayerbotBrain.RangedIdleLimitMs.
    /// </summary>
    [Fact]
    public async Task RangedBotWithNothingToCast_ClosesInAndSwings_InsteadOfStandingAtRange()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync();
        await RunAsync(world, "bg-wsg-ranged-idle", async context =>
        {
            // A mage (class 8) that knows no attack spell: at range it has nothing to cast.
            (ScenarioBot ally, ScenarioBot horde, WarsongGulch wsg) = await MatchAsync(context, "Bgrangedmg", allyClass: 8);
            Vector3 start = await ally.ReadAsync(At);
            await context.PlaceAsync(horde, 489, start.X + 15f, start.Y, start.Z);
            await FlagForPvpAsync(horde);
            await context.StepAsync("the mage closes in and swings at the Horde player within 30 s", () => WaitExplainedAsync(context, "swinging in melee",
                () => ally.RequirePlayer() is { } mage && horde.RequirePlayer() is { } enemy && ReferenceEquals(mage.Combat.Victim, enemy)
                    && MapCombat.CanReachWithMeleeAutoAttack(mage, enemy),
                TimeSpan.FromSeconds(30), ally, horde));
        });
    }

    /// <summary>
    /// The owner saw "bots running to their bodies instead of waiting to be rezzed by the spirit healer". A dead bot in Warsong Gulch waits
    /// beside the graveyard's spirit guide for its area resurrection wave (cmangos battlegrounds.cpp npc_spirit_guideAI; the fix is
    /// PlayerbotRecovery's, branch claude/fix-bot-bg-spirit-healer 91c6f53e, merged here): it never runs back to its corpse in the enemy flag room.
    /// </summary>
    [Fact]
    public async Task DeadBot_WaitsAtTheSpiritGuide_AndNeverRunsToItsBody()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync();
        await RunAsync(world, "bg-wsg-spirit-guide", async context =>
        {
            (ScenarioBot ally, _, WarsongGulch wsg) = await MatchAsync(context, "Bgghostwait");
            Vector3 corpse = Content.HordeFlag + new Vector3(0f, 5f, 0f);
            await context.PlaceAsync(ally, 489, corpse.X, corpse.Y, corpse.Z);
            await context.ReadAsync(() =>
            {
                Player player = ally.RequirePlayer();
                // The Alliance spirit guides at the Alliance graveyards (the release puts the ghost at one of them).
                foreach ((uint id, float dx, float dy) in new[] { (769u, -15f, 0f), (771u, -20f, 10f) })
                    DungeonBotHost.AddCreature(player.Map!, 13116u + id, Content.AllianceFlag.X + dx - 3f, Content.AllianceFlag.Y + dy, Content.WsgFloor,
                        (uint)NpcFlags.SpiritGuide, context.World.NowMs, faction: 1);
                player.Health = 0;
                player.Map!.Combat.KillPlayer(player);
                return true;
            });

            float nearestToCorpse = float.MaxValue;
            await context.StepAsync("the bot releases", () => context.WaitUntilAsync("a ghost", () => (ally.RequirePlayer().Flags & PlayerFlags.Ghost) != 0, TimeSpan.FromSeconds(30)));
            await context.StepAsync("for 40 s the ghost stays by the spirit guide (or is raised there)", async () =>
            {
                for (int second = 0; second < 40; second++)
                {
                    await context.IdleAsync(TimeSpan.FromSeconds(1));
                    nearestToCorpse = MathF.Min(nearestToCorpse, await ally.ReadAsync(p => Vector3.Distance(At(p), corpse)));
                }

                ScenarioContext.Expect(nearestToCorpse > 30f, $"the ghost ran to its body: {nearestToCorpse:F1} yd from the corpse at the closest");
                ScenarioContext.Expect(wsg.Status == BattlegroundStatus.InProgress, "the match ended");
            });
        });
    }

    /// <summary>
    /// A battlemaster spawned only by a game event (the Warsong Emissary 15105, spawn 190554, in game event 19 "Call to Arms: Warsong
    /// Gulch") is no battlemaster while the event is off. Live: Bokae, Slouviabrias, Lurudiand, Dragliak and others walked to its spot in the
    /// Undercity, found nobody ("no living battlemaster at (1633.35, 180.9, -62.2)") and were set aside, again and again.
    /// </summary>
    [Fact]
    public async Task BattlemasterSpawnedOnlyByAGameEvent_IsNotWalkedTo_WhileTheEventIsOff()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync();
        await RunAsync(world, "bg-master-event-gated", async context =>
        {
            ScenarioBot ally = await context.LoginAsync("Bgmastergate", 1, characterClass: 1);
            using AnomalyCapture capture = await CaptureAsync(ally);
            await context.ReadAsync(() =>
            {
                // The game events refuse the Alliance Warsong Gulch battlemaster's spawn (its event is off): it is not in the world.
                CreatureMapSystem creatures = ally.RequirePlayer().Map!.FindUpdater<CreatureMapSystem>()!;
                creatures.SpawnGate = new RefuseSpawns(Content.AllianceWsgMaster);
                return true;
            });
            await context.AutonomousAsync(ally);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            await context.IdleAsync(TimeSpan.FromSeconds(30));
            ScenarioContext.Expect(!coordinator.Events.Any(e => e.Contains("goes to the WarsongGulch battlemaster", StringComparison.Ordinal)),
                string.Join(" | ", coordinator.Events));
            ScenarioContext.ExpectEqual(0, capture.Count(ObjectiveUnreachable), capture.Describe());
        });
    }

    /// <summary>
    /// A battlemaster whose route was refused is not tried again from the same zone. Live: Gitrol, in Silverpine Forest, was sent 62 times to
    /// the Undercity battlemaster (inside the city, reached by its elevators, which the navigation mesh does not join): each time the route
    /// was refused, the type was set aside for ten minutes, and then the same battlemaster was picked again.
    /// </summary>
    [Fact]
    public async Task BattlemasterWhoseRouteWasRefused_IsNotPickedAgainFromTheSameZone_AfterTheSetAside()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync();
        await RunAsync(world, "bg-master-refused", async context =>
        {
            ScenarioBot ally = await context.LoginAsync("Bgmasterrefu", 1, characterClass: 1);
            Vector3 start = await ally.ReadAsync(At);
            // The bot stands 40 yards from the battlemasters and no route reaches them.
            await context.PlaceAsync(ally, 0, start.X + 40f, start.Y, start.Z);
            await context.ReadAsync(() =>
            {
                WorldCollision.Of(context.World).Install(pathfinder: new NoRoutePathfinder());
                return true;
            });
            using AnomalyCapture capture = await CaptureAsync(ally);
            await context.AutonomousAsync(ally);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            int Walks() => coordinator.Events.Count(e => e.Contains("goes to the WarsongGulch battlemaster", StringComparison.Ordinal));
            await context.StepAsync("the first walk is refused", () => context.WaitUntilAsync("set aside",
                () => coordinator.Events.Any(e => e.Contains("could not walk to the WarsongGulch battlemaster", StringComparison.Ordinal)), TimeSpan.FromSeconds(60)));
            ScenarioContext.ExpectEqual(1, Walks(), string.Join(" | ", coordinator.Events));

            // Past the ten-minute set-aside the same battlemaster is still out of reach from here.
            await context.IdleAsync(TimeSpan.FromMilliseconds(PlayerbotBattlegroundCoordinator.SetAsideMs + 60_000));
            ScenarioContext.ExpectEqual(1, Walks(), string.Join(" | ", coordinator.Events));
            ScenarioContext.ExpectEqual(1, capture.Count(ObjectiveUnreachable), capture.Describe());
        });
    }

    /// <summary>
    /// Four Alliance bots against a Horde player: every bot has a duty every think (the carrier, defenders, the rest at the enemy flag or the
    /// middle), the defender fights the Horde player once it stands in the Alliance flag room, nobody is idle, no objective is given up as a
    /// loop, and the flag goes across. Both used to fail now and then, depending on where the fight left the bots: a bot that chased the
    /// intruder between two walks to its objective had the objective refused by the loop watch for a minute (the fight's walk was not
    /// planned as a chase), and a defender that held its room and was then sent to the middle was idle at once (its still time under the
    /// old duty counted under the new one).
    /// </summary>
    [Fact]
    public async Task FourBots_EachHasADuty_TheDefenderFightsTheIntruder_AndTheFlagGoesAcross()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync();
        // Four Alliance bots against one Horde player: the default balanced invitations (type 1, vmangos Battleground.InvitationType) would
        // keep three of them in the queue until the Horde side had as many, so the queue invites in order.
        world.Battlegrounds().Options.InvitationType = 0;
        await RunAsync(world, "bg-wsg-four", async context =>
        {
            ScenarioBot[] allies = new ScenarioBot[4];
            for (int i = 0; i < allies.Length; i++)
            {
                allies[i] = await context.LoginAsync("Bgfour" + (char)('a' + i), 1, 1);
                await context.AutonomousAsync(allies[i]);
            }

            // The idle reports name the bot, its duty and what it stood still at: the failure message carries them.
            using AnomalyCapture captureA = await CaptureAsync(allies[0]);
            using AnomalyCapture captureB = await CaptureAsync(allies[1]);
            using AnomalyCapture captureC = await CaptureAsync(allies[2]);
            using AnomalyCapture captureD = await CaptureAsync(allies[3]);
            AnomalyCapture[] captures = [captureA, captureB, captureC, captureD];
            ScenarioBot horde = await ScriptedHordeQueuesAsync(context, "Bgfourh", BattlegroundType.WarsongGulch, Content.HordeWsgMaster);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            await PortScriptedAsync(context, horde, 489, (Content.HordeParking.X, Content.HordeParking.Y, Content.HordeParking.Z));
            await context.StepAsync("the four bots are in the match", () => WaitExplainedAsync(context, "in the match",
                () => allies.All(a => coordinator.Find(a.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch), TimeSpan.FromMinutes(2), allies));
            var wsg = (WarsongGulch)(await context.BattlegroundOfAsync(allies[0]))!;
            coordinator.Options.QueueChancePerMinute = 0f;
            await context.StepAsync("the gates open", () => context.WaitUntilAsync("in progress", () => wsg.Status == BattlegroundStatus.InProgress, StartWait));

            await context.StepAsync("every bot has an order", () => context.WaitUntilAsync("orders", () => allies.All(a => coordinator.Find(a.BotId)?.AI?.Order is not null), TimeSpan.FromSeconds(5)));
            PlayerbotWsgDuty[] duties = await context.ReadAsync(() => allies.Select(a => coordinator.Find(a.BotId)!.AI!.Order!.Value.Duty).ToArray());
            ScenarioContext.ExpectEqual(1, duties.Count(d => d == PlayerbotWsgDuty.DefendFlagRoom), "defenders: " + string.Join(",", duties));

            await context.StepAsync("the Horde player walks into the Alliance flag room: a defender fights it", async () =>
            {
                await context.PlaceAsync(horde, 489, Content.AllianceFlag.X + 6f, Content.AllianceFlag.Y + 6f, Content.WsgFloor);
                await FlagForPvpAsync(horde);
                await WaitExplainedAsync(context, "a bot attacks the intruder", () => allies.Any(a => ReferenceEquals(a.RequirePlayer().Combat.Victim, horde.RequirePlayer())),
                    TimeSpan.FromSeconds(20), allies);
                await context.PlaceAsync(horde, 489, Content.HordeParking.X, Content.HordeParking.Y, Content.HordeParking.Z);
            });

            await context.StepAsync("the flag goes across", () => WaitExplainedAsync(context, "captured", () => wsg.TeamScore(Team.Alliance) >= 1, TimeSpan.FromSeconds(120), allies));
            // Each bot's loop watch (PlayerbotMotion): a fight between two walks to an objective is no back-and-forth to give the objective up for.
            string loops = await context.ReadAsync(() => string.Join(" | ", allies.Select(a => a.RequirePlayer()).Select(p =>
                $"{p.Name} loops {PlayerbotMotion.LoopCount(p)} refused [{(PlayerbotMotion.TryGetState(p, out PlayerbotMotion.MotionState? motion) ? string.Join("; ", motion.Blacklist.Select(b => b.Destination)) : "")}]")));
            int looped = await context.ReadAsync(() => allies.Sum(a => PlayerbotMotion.LoopCount(a.RequirePlayer())));
            ScenarioContext.ExpectEqual(0, looped, "objectives given up as loops: " + loops);
            int idle = await context.ReadAsync(() => allies.Sum(a => coordinator.Find(a.BotId)?.AI?.IdleThinks ?? 0));
            ScenarioContext.ExpectEqual(0, idle, "idle thinks: " + string.Join(" | ", allies.Select(a => coordinator.Describe(a.BotId)))
                + Environment.NewLine + string.Join(Environment.NewLine, captures.Select(c => c.Describe())) + Environment.NewLine + loops);
        });
    }

    /// <summary>
    /// A bot bound to its match but standing on another map routes nothing (the objectives are coordinates of map 489).
    /// </summary>
    [Fact]
    public async Task BoundBotOffTheMatchMap_RoutesNoBattlegroundObjective()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync();
        await RunAsync(world, "bg-wsg-off-map", async context =>
        {
            (ScenarioBot ally, _, WarsongGulch wsg) = await MatchAsync(context, "Bgoffmap");
            using AnomalyCapture capture = await CaptureAsync(ally);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            Vector3 start = new(StartXOf(), StartYOf(), StartZOf());
            await context.PlaceAsync(ally, 0, start.X, start.Y, start.Z);
            bool bound = await context.ReadAsync(() => context.Services.GetRequiredService<BattlegroundFeature>().Manager.StateOf(ally.Guid).InBattleground);
            if (!bound) return; // the server unbinds a player that leaves the match's map: nothing to route
            await context.IdleAsync(TimeSpan.FromSeconds(10));
            Vector3 now = await ally.ReadAsync(At);
            ScenarioContext.Expect(Vector3.Distance(now, start) < 2f, $"the bot walked on map 0 towards a Warsong Gulch objective: now at {now}");
            ScenarioContext.ExpectEqual(0, capture.Count(ObjectiveUnreachable), capture.Describe());
        });
    }

    private const uint DrinkSpell = 993_401;
    private const uint DrinkItem = 993_402;

    /// <summary>
    /// Bot QA #813 (Warsong Gulch, live 2026-10-17 03:18-04:41 UTC): priests below 10% mana for 45 s of one fight, the same priest again 85 s
    /// later in its next fight (Flugeikund 04:04:53 and 04:06:18): between fights the battleground AI never ate or drank, so a caster went
    /// from one fight into the next on empty. vmangos BattleBotAI::DrinkAndEat (BattleBotAI.cpp:173-229, called out of combat at :849-853)
    /// drinks whenever the bot is out of combat with no victim. A priest at 5% mana with water in its bags and no enemy near sits and drinks,
    /// and its mana comes back before it walks on.
    /// </summary>
    [Fact]
    public async Task LowManaPriest_DrinksBetweenFights_InsteadOfGoingOnEmpty()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync();
        await RunAsync(world, "bg-wsg-drink", async context =>
        {
            (ScenarioBot ally, _, _) = await MatchAsync(context, "Bgdrinkpr", allyClass: 5);
            await context.ReadAsync(() =>
            {
                ArcaneCore.Game.Spells.SpellSystem spells = context.Services.GetRequiredService<SpellFeature>().System;
                spells.Store = new ArcaneCore.Game.Spells.SpellStore([.. spells.Store.All, DrinkAura()], [], []);
                Player priest = ally.RequirePlayer();
                // The scenario world's item content has no drink: this priest's bags know one more template.
                var drink = new ArcaneCore.Kernel.Items.ItemTemplate
                {
                    Entry = DrinkItem, Name = "test water", Class = 0, InventoryType = 0, Stackable = 20,
                    Spells = [new ArcaneCore.Kernel.Items.ItemSpell(DrinkSpell, 0, -1, 0, 0, 0, 0)],
                };
                priest.Inventory.Templates = new ArcaneCore.Game.Items.ItemTemplateStore(
                    [.. (priest.Inventory.Templates as ArcaneCore.Game.Items.ItemTemplateStore)?.All ?? [], drink]);
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, priest.Inventory.AddItem(DrinkItem, 5, out _));
                priest.SetUInt32(UpdateFields.UnitFieldMaxpower1, 1000);
                ArcaneCore.Game.Spells.SpellSystem.SetPower(priest, PowerType.Mana, 50);
                return true;
            });

            await context.StepAsync("the priest drinks within 10 s", () => WaitExplainedAsync(context, "drinking",
                () => context.Services.GetRequiredService<SpellFeature>().System.HasAura(ally.RequirePlayer(), DrinkSpell),
                TimeSpan.FromSeconds(10), ally));
            await context.StepAsync("it keeps drinking until its mana is back over half", () => WaitExplainedAsync(context, "mana back",
                () => ArcaneCore.Game.Spells.SpellSystem.GetPower(ally.RequirePlayer(), PowerType.Mana) >= 500,
                TimeSpan.FromSeconds(30), ally));
        });
    }

    /// <summary>A drink as the item consumables have it: sitting, standing cancels it, MOD_POWER_REGEN on mana (500 per 5 s here).</summary>
    private static ArcaneCore.Game.Spells.SpellInfo DrinkAura() => new()
    {
        Id = DrinkSpell, Name = "Drink", Duration = new ArcaneCore.Game.Spells.SpellDuration(30_000, 0, 30_000),
        Attributes = ArcaneCore.Game.Spells.SpellAttributes.AllowWhileSitting,
        AuraInterruptFlags = ArcaneCore.Game.Spells.SpellAuraInterruptFlags.StandingCancels,
        Effects =
        [
            new ArcaneCore.Game.Spells.SpellEffectInfo
            {
                Effect = ArcaneCore.Game.Spells.SpellEffectName.ApplyAura, AuraType = ArcaneCore.Game.Spells.AuraType.ModPowerRegen,
                BasePoints = 499, BaseDice = 1, DieSides = 1, MiscValue = (int)PowerType.Mana,
                TargetA = ArcaneCore.Game.Spells.SpellImplicitTarget.UnitCaster,
            },
            new(), new(),
        ],
    };

    private static float StartXOf() => ScenarioTestContent.StartX;

    private static float StartYOf() => ScenarioTestContent.StartY;

    private static float StartZOf() => ScenarioTestContent.StartZ;

    /// <summary>
    /// A game-event gate that refuses the listed creature spawns (their event is off). It wraps, as a battleground map's gate does: the world's
    /// game-event spawn feature re-installs its own gate on every map each world tick (GameEventSpawnFeature.InstallGate) and replaces a gate
    /// that does not wrap, which would put the refused battlemaster back into the world.
    /// </summary>
    private sealed class RefuseSpawns(params uint[] refused) : IWrappingSpawnGate
    {
        public ISpawnGate? Inner { get; set; }

        public bool AllowsCreature(uint spawnGuid) => !refused.Contains(spawnGuid) && (Inner?.AllowsCreature(spawnGuid) ?? true);

        public bool AllowsGameObject(uint spawnGuid) => Inner?.AllowsGameObject(spawnGuid) ?? true;

        public IEnumerable<uint> GatedCreatures => Inner is null ? refused : refused.Concat(Inner.GatedCreatures);

        public IEnumerable<uint> GatedGameObjects => Inner?.GatedGameObjects ?? [];
    }

    /// <summary>No route anywhere (the Undercity's elevators, for the navigation mesh).</summary>
    private sealed class NoRoutePathfinder : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => PathResult.None(start);
    }
}

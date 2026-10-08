using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.BlackrockSpire;
using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Instances.Scripts.Scholomance;
using ArcaneCore.Game.Instances.Scripts.Stratholme;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.Extensions.Logging;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// The production paths into the ScriptDev2 dungeon scripts (the spell scripts that send their events, the shared use and event hooks) and
/// the reference branches the first port missed: the stadium fail, the Gyth Rend summon, the Gordok tribute without Cho'Rush and the
/// Stratholme slaughter square wipes.
/// </summary>
public sealed partial class ScriptDev2DungeonTests
{
    private sealed class UseProbe(Map map, bool takeUse) : InstanceData(map)
    {
        public List<uint> Used { get; } = [];

        public override bool OnGameObjectUse(Player player, GameObject go)
        {
            Used.Add(go.Entry);
            return takeUse;
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }

    [Fact]
    public void SpellScripts_TheProductionDiscoveryFindsShadowPortalAndEmberseerGrowing()
    {
        // SpellScriptFeature installs SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly) on the world's spell system.
        SpellScriptRegistry registry = SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly);
        Assert.IsType<ShadowPortalScript>(registry.Find(ShadowPortalScript.SpellId));
        Assert.IsType<EmberseerGrowingScript>(registry.Find(EmberseerGrowingScript.SpellId));
    }

    [Fact]
    public void ShadowPortal_DummyCastsARoomPortal_ThatTeleportsTheTargetAndSendsTheRoomEvent()
    {
        // Spell.sql 17863/17939/17943/17944/17946/17948: TELEPORT_UNITS to spell_target_position, SEND_EVENT 5618-5623. The 17950 dummy and
        // the portals target the caster here, so one player is enough to follow the chain.
        uint[] events = [5618, 5619, 5620, 5621, 5622, 5623];
        SpellInfo[] portals = [.. ShadowPortalScript.RoomPortals.Select((id, i) => SpellTestKit.Spell(id,
            SpellTestKit.Effect(SpellEffectName.TeleportUnits, 0, targetB: SpellImplicitTarget.LocationDatabase),
            SpellTestKit.Effect(SpellEffectName.SendEvent, 0, misc: (int)events[i])))];
        var store = new SpellStore(
            [.. SpellTestKit.DefaultSpells(), SpellTestKit.Spell(ShadowPortalScript.SpellId, SpellTestKit.Effect(SpellEffectName.Dummy, 0)), .. portals],
            [],
            ShadowPortalScript.RoomPortals.Select((id, i) => (id, new SpellTargetPosition(0, 100f + (i * 10f), 200f, 83.5f, 0.5f))));
        using WorldRuntime world = TestWorld.CreateRuntime();
        var spells = new SpellSystem(store, () => 10_000, random: new Random(1)) { MapUpdateIntervalMs = 0 };
        SpellScriptDispatcher.Install(spells, new SpellScriptRegistry([new ShadowPortalScript()]));
        var session = new FakeSession(1);
        Player player = TestWorld.CreatePlayer(1, 0, 0, session);
        world.AddPlayer(player);
        world.RunTick(0);
        var probe = new EventProbe(player.Map!);
        player.Map!.AddUpdater(probe);

        Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(player, ShadowPortalScript.SpellId, SpellCastTargets.ForSelf(), triggered: true));

        int room = Array.IndexOf(events, probe.EventId);
        Assert.InRange(room, 0, 5);
        Assert.Equal((100f + (room * 10f), 200f), (player.X, player.Y));
    }

    [Fact]
    public void SpellSendEvent_NoInstanceScriptTakesTheEvent_IsReportedAsNotImplemented()
    {
        const uint spellId = 901235;
        var logger = new CapturingLogger();
        var store = new SpellStore([SpellTestKit.Spell(spellId, SpellTestKit.Effect(SpellEffectName.SendEvent, 0, misc: 4242))], [], []);
        using WorldRuntime world = TestWorld.CreateRuntime();
        var spells = new SpellSystem(store, () => 10_000, logger: logger) { MapUpdateIntervalMs = 0 };
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(1));
        world.AddPlayer(player);
        world.RunTick(0);

        Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(player, spellId, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Contains(logger.Lines, line => line.Contains("send event 4242", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GameObjectUse_AnInstanceScriptThatTakesTheUse_KeepsTheGooberFromCastingItsSpell(bool takeUse)
    {
        GameObjectTypeRig rig = GameObjectTypeRig.Create([GameObjectTestKit.GoSpawn(1, GameObjectTypeRig.SpellGoober, 3, 0)]);
        var probe = new UseProbe(rig.Map, takeUse);
        rig.Map.AddUpdater(probe);
        (Player player, _) = rig.Join(1);
        GameObject goober = rig.Single(GameObjectTypeRig.SpellGoober);

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, goober.Guid));
        Assert.Equal([GameObjectTypeRig.SpellGoober], probe.Used);
        Assert.Equal(GameObjectLootState.Activated, goober.LootState); // the object still activates
        Assert.Equal(takeUse ? 0 : 1, rig.Spells.Casts.Count);
    }

    [Fact]
    public void Spire_AltarEventFreesTheIncarcerators_AndTwentyGrowingEventsFreeEmberseer()
    {
        using var f = new InstanceFixture();
        Map map = f.World.GetMap(BlackrockSpireInstance.MapId, 97);
        var spire = new BlackrockSpireInstance(map);
        spire.Initialize();
        map.AddUpdater(spire);
        CreatureTemplate emberseerTemplate = Template(BlackrockSpireInstance.NpcEmberseer);
        CreatureTemplate incarceratorTemplate = Template(BlackrockSpireInstance.NpcIncarcerator);
        var creatures = new CreatureMapSystem(map, Content([emberseerTemplate, incarceratorTemplate], []));
        map.AddUpdater(creatures);
        Creature emberseer = creatures.SpawnTemporary(emberseerTemplate, 140f, -250f, 92f, 0f);
        Creature incarcerator = creatures.SpawnTemporary(incarceratorTemplate, 145f, -250f, 92f, 0f);
        incarcerator.UnitFlags |= UnitFlags.ImmuneToPlayer | UnitFlags.ImmuneToNpc;
        Assert.IsType<PyroguardEmberseerAI>(emberseer.AI);
        Assert.True((emberseer.UnitFlags & UnitFlags.NotSelectable) != 0);

        // ProcessEventId_event_spell_altar_emberseer: only a player source starts DoProcessEmberseerEvent.
        Assert.True(spire.OnSpellEvent(emberseer, BlackrockSpireInstance.EventAltarEmberseer));
        Assert.True((incarcerator.UnitFlags & UnitFlags.ImmuneToPlayer) != 0);
        Assert.True(spire.OnSpellEvent(f.AddPlayer(1), BlackrockSpireInstance.EventAltarEmberseer));
        Assert.Equal(UnitFlags.None, incarcerator.UnitFlags & (UnitFlags.ImmuneToPlayer | UnitFlags.ImmuneToNpc));

        for (int i = 0; i < 20; i++)
        {
            emberseer.ReceiveAiEvent(5, emberseer, emberseer); // AI_EVENT_CUSTOM_EVENTAI_A is not the growing event
        }

        Assert.True((emberseer.UnitFlags & UnitFlags.NotSelectable) != 0);
        for (int i = 0; i < 19; i++)
        {
            emberseer.ReceiveAiEvent(PyroguardEmberseerAI.AiEventCustomA, emberseer, emberseer);
        }

        Assert.True((emberseer.UnitFlags & UnitFlags.NotSelectable) != 0);
        emberseer.ReceiveAiEvent(PyroguardEmberseerAI.AiEventCustomA, emberseer, emberseer);
        Assert.Equal(UnitFlags.None, emberseer.UnitFlags & (UnitFlags.NotSelectable | UnitFlags.ImmuneToPlayer));
        Assert.Equal(EncounterState.InProgress, spire.GetData(BlackrockSpireInstance.TypeEmberseer));
    }

    [Fact]
    public void Spire_StadiumWipeDespawnsNefariusAndRend_SoTheTriggerStartsAgainWithoutDuplicates()
    {
        using ScriptRun run = Enter(map => new BlackrockSpireInstance(map), [],
            [(BlackrockSpireInstance.GoGythEntry, -12f), (BlackrockSpireInstance.GoGythCombat, -10f),
             (BlackrockSpireInstance.GoGythExit, -8f)],
            [BlackrockSpireInstance.NpcNefarius, BlackrockSpireInstance.NpcRend,
             BlackrockSpireInstance.NpcGyth, BlackrockSpireInstance.NpcWhelp,
             BlackrockSpireInstance.NpcDragon, BlackrockSpireInstance.NpcHandler, 9819, 10317]);
        run.Data.OnAreaTrigger(run.Player, 2026);
        run.Data.Update(7_000);
        run.Data.Update(5_000);
        Creature waveMob = run.Creatures.Creatures.First(c => c.Spawn is null && c.Template.Entry == BlackrockSpireInstance.NpcWhelp);

        run.Data.OnCreatureEvade(waveMob); // a summoned stadium mob evades: SetData(TYPE_STADIUM, FAIL)
        Assert.Equal(EncounterState.Fail, run.Data.GetData(BlackrockSpireInstance.TypeStadium));
        run.Fixture.Tick();
        Assert.DoesNotContain(run.Creatures.Creatures, c => c.IsAlive
            && c.Template.Entry is BlackrockSpireInstance.NpcNefarius or BlackrockSpireInstance.NpcRend or 9819 or 10317);

        run.Data.OnAreaTrigger(run.Player, 2026);
        Assert.Equal(EncounterState.InProgress, run.Data.GetData(BlackrockSpireInstance.TypeStadium));
        Assert.Single(run.Creatures.Creatures, c => c.IsAlive && c.Template.Entry == BlackrockSpireInstance.NpcNefarius);
        Assert.Single(run.Creatures.Creatures, c => c.IsAlive && c.Template.Entry == BlackrockSpireInstance.NpcRend);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Spire_GythSummonsRendOnce_ThroughTheSpellWhenItCasts_ElseDirectly(bool spellSystem)
    {
        const string aiName = "GythUnderTest";
        CreatureContent content = Content(
            [Template(BlackrockSpireInstance.NpcGyth, t => t.AIName = aiName), Template(BlackrockSpireInstance.NpcRend)],
            [Spawn(1, BlackrockSpireInstance.NpcGyth, 5, 0)]);
        var factory = new CreatureAiFactory();
        factory.Register(aiName, c => new GythAI(c));
        FakeCaster? caster = spellSystem ? new FakeCaster() : null;
        (WorldRuntime world, Map map, CreatureMapSystem creatures) = CreatureAiTestSupport.CreateAiSystem(content,
            new CreatureAiServices { Hostility = new AlwaysHostile(), Factory = factory, Spells = caster });
        using WorldRuntime runtime = world;
        (Player player, _) = CreatureAiTestSupport.AddPlayer(world, 1, 0, 0);
        Creature gyth = Assert.Single(creatures.Creatures);
        gyth.AI!.CombatMovement = false;
        map.Combat.DealDamage(player, gyth, 1, direct: false);
        Assert.Same(player, gyth.Combat.Victim);
        gyth.Health = gyth.MaxHealth / 20; // under 11 %

        gyth.AI.OnUpdate(100);
        gyth.AI.OnUpdate(100);

        Assert.Equal(0u, gyth.InvincibilityHpThreshold); // SetDeathPrevention(false)
        int rends = creatures.Creatures.Count(c => c.Template.Entry == BlackrockSpireInstance.NpcRend);
        if (spellSystem)
        {
            // CAST_OK: Rend comes from the spell's SUMMON_WILD (the fake caster summons nothing), never a second, direct one.
            Assert.Single(caster!.Casts, c => c.Spell == GythAI.SpellSummonRend);
            Assert.Equal(0, rends);
        }
        else
        {
            Assert.Equal(1, rends);
        }
    }

    [Fact]
    public void DireMaul_KingDeathAfterChorushFell_StillSummonsMizzleFromHisCorpse()
    {
        using ScriptRun run = Enter(map => new DireMaulInstance(map),
            [(1, DireMaulInstance.NpcKingGordok, -12f, 61.78f),
             (2, DireMaulInstance.NpcChorush, -10f, 61.78f)], [], [DireMaulInstance.NpcMizzle]);
        run.Kill(2); // Cho'Rush fought beside the king
        run.Kill(1);

        Creature mizzle = Assert.Single(run.Creatures.Creatures, c => c.Template.Entry == DireMaulInstance.NpcMizzle);
        Assert.Equal(0u, mizzle.NpcFlags & (uint)ArcaneCore.Game.Npc.NpcFlags.Gossip);
        var dire = (DireMaulInstance)run.Data;
        Assert.Equal(5, dire.TributeGuardsSpared);
        Assert.True(dire.TributeConditionMet(5));
    }

    [Fact]
    public void Stratholme_AbominationWipeReopensTheGauntletPort_AndStopsTheWalkingAbominations()
    {
        using ScriptRun run = Enter(map => new StratholmeInstance(map),
            [(1, StratholmeInstance.NpcBileAbom, -12f, 61.78f),
             (2, StratholmeInstance.NpcVenomAbom, -10f, 61.78f)],
            [(StratholmeInstance.GoGauntletPort, -12f), (StratholmeInstance.GoSlaughterPort, -6f)]);
        GameObject port = run.Object(StratholmeInstance.GoGauntletPort);
        run.Objects.ToggleDoorOrButton(port); // StartSlaughterSquare opened it
        Creature bile = run.Creature(1);
        Creature venom = run.Creature(2);

        run.Data.OnCreatureEnterCombat(bile); // SetData(TYPE_RAMSTEIN, SPECIAL): close the port, start calling abominations
        Assert.Equal(GameObjectState.Ready, port.State);
        run.Data.Update(20_000);
        Assert.Equal(MovementGeneratorType.Point, bile.Motion.CurrentType); // m_slaughterSquareTimer called the first one
        Assert.NotEqual(MovementGeneratorType.Point, venom.Motion.CurrentType);

        run.Data.OnCreatureEvade(venom); // a wipe on the abominations: SetData(TYPE_RAMSTEIN, FAIL)
        Assert.Equal(EncounterState.Fail, run.Data.GetData(StratholmeInstance.TypeRamstein));
        Assert.Equal(GameObjectState.Active, port.State);
        Assert.NotEqual(MovementGeneratorType.Point, bile.Motion.CurrentType);
        run.Data.Update(60_000);
        Assert.NotEqual(MovementGeneratorType.Point, venom.Motion.CurrentType); // the timer stopped

        run.Data.OnCreatureEnterCombat(venom); // the next try closes it again
        Assert.Equal(GameObjectState.Ready, port.State);
    }

    [Fact]
    public void Stratholme_RamsteinWipeReopensThePortAndTheSlaughterhouse_AndHisNextAggroClosesThePort()
    {
        using ScriptRun run = Enter(map => new StratholmeInstance(map),
            [(1, StratholmeInstance.NpcBileAbom, -12f, 61.78f),
             (2, StratholmeInstance.NpcVenomAbom, -10f, 61.78f)],
            [(StratholmeInstance.GoGauntletPort, -12f), (StratholmeInstance.GoSlaughterhouse, -10f)],
            [StratholmeInstance.NpcRamstein]);
        GameObject port = run.Object(StratholmeInstance.GoGauntletPort);
        GameObject slaughterhouse = run.Object(StratholmeInstance.GoSlaughterhouse);
        run.Objects.ToggleDoorOrButton(port);
        run.Kill(1);
        run.Kill(2);
        Creature ramstein = Assert.Single(run.Creatures.Creatures, c => c.Template.Entry == StratholmeInstance.NpcRamstein);
        Assert.Equal(GameObjectState.Ready, port.State);
        run.Data.Update(10_000); // the slaughterhouse closes behind Ramstein
        Assert.Equal(GameObjectState.Ready, slaughterhouse.State);

        run.Data.OnCreatureEnterCombat(ramstein);
        run.Data.OnCreatureEvade(ramstein);
        Assert.Equal(EncounterState.Fail, run.Data.GetData(StratholmeInstance.TypeRamstein));
        Assert.Equal(GameObjectState.Active, port.State);
        Assert.Equal(GameObjectState.Active, slaughterhouse.State); // DoOpenSlaughterhouseDoor(true): he walks back in

        run.Data.OnCreatureEnterCombat(ramstein); // IN_PROGRESS after FAIL
        Assert.Equal(GameObjectState.Ready, port.State);
        Assert.Single(run.Creatures.Creatures, c => c.Template.Entry == StratholmeInstance.NpcRamstein);
    }
}

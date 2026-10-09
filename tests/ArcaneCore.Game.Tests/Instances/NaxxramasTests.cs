using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Naxxramas;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Game.Tests.Conditions;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class NaxxramasTests
{
    private sealed class RecordingTeleports : ITeleportSink
    {
        public List<(uint Map, float X, float Y, float Z)> Calls { get; } = [];
        public bool Teleport(Unit unit, uint mapId, float x, float y, float z, float orientation)
        { Calls.Add((mapId, x, y, z)); return true; }
        public bool CanTeleport(Unit unit, uint mapId, float x, float y, float z, float orientation) => true;
    }
    private sealed class ObjectCaster(SpellSystem spells) : IGameObjectSpells
    {
        public bool Cast(GameObject source, uint spellId, Unit target, Unit? unitCaster)
            => spells.CastForGameObject(source, spellId, target, unitCaster) == SpellCastResult.CastOk;
        public float? MaxRange(uint spellId) => spells.Store.Get(spellId)?.Range.Max;
        public bool IsChanneling(Unit unit) => false;
        public void StartRitualAnimation(Player helper, uint animSpellId, GameObject ritual) { }
        public bool CastRitualSpell(GameObject ritual, uint spellId, Unit caster, ObjectGuid summonTarget) => false;
        public void StartCreatingSpellCooldown(Player owner, uint spellId) { }
    }
    private sealed class Raid : IDisposable
    {
        public WorldRuntime World { get; } = TestWorld.CreateRuntime();
        public Map Map { get; private set; } = null!;
        public NaxxramasInstance Instance { get; private set; } = null!;
        public FakeCaster Caster { get; } = new();
        public CreatureMapSystem Creatures { get; private set; } = null!;
        public Creature Boss { get; private set; } = null!;
        public Player Tank { get; private set; } = null!;
        public NaxxramasBossAI AI => Assert.IsType<NaxxramasBossAI>(Boss.AI);

        public Raid(uint entry, params uint[] extras)
        {
            Map = World.GetMap(533);
            Instance = new NaxxramasInstance(Map); Instance.Initialize(); Map.AddUpdater(Instance);
            uint[] entries = [entry, .. extras];
            Creatures = new CreatureMapSystem(Map, Content(entries.Select(e => Template(e)).ToArray(), []),
                new CreatureOptions { AggroRate = 0, RespawnPacifyMs = 0 }, random: new Random(1),
                aiServices: new CreatureAiServices { Spells = Caster, Hostility = new AlwaysHostile() });
            Map.AddUpdater(Creatures);
            Tank = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(), 533);
            Tank.Relocate(0, 0, 0, 0, 0); World.AddPlayer(Tank); World.RunTick(0);
            Boss = Creatures.SpawnTemporary(Template(entry), 1, 0, 0, 0);
            AI.AttackStart(Tank);
        }
        public void Dispose() => World.Dispose();
    }
    [Fact]
    public void QuarterDoorsAndPortals_FollowBossState_AndReload()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(533);
        var raid = Assert.IsType<NaxxramasInstance>(InstanceScriptRegistry.Default.Create(map));
        raid.Initialize(); map.AddUpdater(raid);
        uint[] ids = [181195, 181209, 181201, 181241, 181575, 181577];
        var objects = new GameObjectMapSystem(map, new GameObjectContent(
            ids.Select(id => GameObjectTestKit.GoTemplate(id, GameObjectType.Door)), [], [], [], []));
        map.AddUpdater(objects);
        var gos = ids.ToDictionary(id => id, id => objects.Summon(id, 0, 0, 0, 0)!);
        Assert.Equal(GameObjectState.Ready, gos[181195].State);
        raid.SetData(0, EncounterState.Done);
        raid.SetData(1, EncounterState.Done);
        raid.SetData(3, EncounterState.Done);
        raid.SetData(4, EncounterState.Done);
        raid.SetData(2, EncounterState.Done);
        raid.SetData(5, EncounterState.Done);
        Assert.All(ids, id => Assert.Equal(GameObjectState.Active, gos[id].State));
        string saved = raid.GetSaveData()!;
        raid.Initialize(); raid.Load(saved);
        Assert.Equal(15, raid.EncounterStates.Count);
        Assert.Equal(EncounterState.Done, raid.GetData(5));
    }

    [Fact]
    public void AnubAndHeiganEntryDoors_RespectInitialAndPriorBossStates()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(533);
        var raid = new NaxxramasInstance(map); raid.Initialize(); map.AddUpdater(raid);
        var objects = new GameObjectMapSystem(map, new GameObjectContent(
            [GameObjectTestKit.GoTemplate(181126, GameObjectType.Door),
             GameObjectTestKit.GoTemplate(181202, GameObjectType.Door)], [], [], [], []));
        map.AddUpdater(objects);
        GameObject anub = objects.Summon(181126, 0, 0, 0, 0)!;
        GameObject heigan = objects.Summon(181202, 0, 0, 0, 0)!;
        Assert.Equal(GameObjectState.Ready, anub.State);
        Assert.Equal(GameObjectState.Ready, heigan.State);
        raid.SetData(0, EncounterState.Fail);
        Assert.Equal(GameObjectState.Active, anub.State);
        raid.SetData(3, EncounterState.Done);
        Assert.Equal(GameObjectState.Active, heigan.State);
        raid.SetData(4, EncounterState.InProgress);
        Assert.Equal(GameObjectState.Ready, heigan.State);
    }

    [Theory]
    [InlineData(181575u, 2u)]
    [InlineData(181576u, 12u)]
    [InlineData(181577u, 5u)]
    [InlineData(181578u, 8u)]
    public void ClearedWingPortal_CastsClassicDbReturnSpell(uint entry, uint boss)
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(533);
        var raid = new NaxxramasInstance(map); raid.Initialize(); map.AddUpdater(raid);
        var spells = new FakeObjectSpells();
        var objects = new GameObjectMapSystem(map, new GameObjectContent(
            [GameObjectTestKit.GoTemplate(entry, GameObjectType.Goober, (10, 28444))], [], [], [], []))
        { Spells = spells };
        map.AddUpdater(objects);
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(), 533);
        player.Relocate(0, 0, 0, 0, 0); world.AddPlayer(player); world.RunTick(0);
        GameObject portal = objects.Summon(entry, 0, 0, 0, 0)!;
        Assert.Equal(GameObjectUseResult.NotUsable, objects.Use(player, portal.Guid));
        raid.SetData(boss, EncounterState.Done);
        Assert.Equal(GameObjectUseResult.Ok, objects.Use(player, portal.Guid));
        Assert.Equal(28444u, Assert.Single(spells.Casts).Spell);
    }

    [Fact]
    public void FourWingClears_EnableHubPortalAndFrostwyrmTrigger()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(533);
        var raid = new NaxxramasInstance(map); raid.Initialize(); map.AddUpdater(raid);
        var teleports = new RecordingTeleports();
        var objects = new GameObjectMapSystem(map, new GameObjectContent(
            [GameObjectTestKit.GoTemplate(181229, GameObjectType.Door)], [], [], [], []))
        { Teleports = teleports };
        map.AddUpdater(objects);
        GameObject hub = objects.Summon(181229, 0, 0, 0, 0)!;
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(), 533);
        player.Relocate(0, 0, 0, 0, 0); world.AddPlayer(player); world.RunTick(0);
        raid.OnAreaTrigger(player, 4156);
        Assert.Empty(teleports.Calls);
        foreach (uint boss in new uint[] { 2, 5, 8, 12 }) raid.SetData(boss, EncounterState.Done);
        Assert.True(raid.WingsCleared);
        Assert.Equal(GameObjectState.Active, hub.State);
        raid.OnAreaTrigger(player, 4156);
        Assert.Equal((533u, 3498.13f, -5349.6f, 144.967f), Assert.Single(teleports.Calls));
    }

    [Fact]
    public void ArachnidPortal_TeleportsToClassicDbHubPosition()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(533);
        var raid = new NaxxramasInstance(map); raid.Initialize(); map.AddUpdater(raid);
        SpellInfo teleport = SpellTestKit.Spell(28444,
            SpellTestKit.Effect(SpellEffectName.TeleportUnits, 0,
                targetB: SpellImplicitTarget.LocationDatabase));
        var spells = new SpellSystem(new SpellStore([teleport], [],
            [(28444, new SpellTargetPosition(533, 3005.74f, -3434.27f, 304.196f, 0))]), () => 10000);
        var objects = new GameObjectMapSystem(map, new GameObjectContent(
            [GameObjectTestKit.GoTemplate(181575, GameObjectType.Goober, (10, 28444))], [], [], [], []))
        { Spells = new ObjectCaster(spells) };
        map.AddUpdater(objects);
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(), 533);
        player.Relocate(0, 0, 0, 0, 0); world.AddPlayer(player); world.RunTick(0);
        GameObject portal = objects.Summon(181575, 0, 0, 0, 0)!;
        raid.SetData(2, EncounterState.Done);
        Assert.Equal(GameObjectUseResult.Ok, objects.Use(player, portal.Guid));
        Assert.Equal(3005.74f, player.X);
        Assert.Equal(-3434.27f, player.Y);
        Assert.Equal(304.196f, player.Z);
    }

    [Theory]
    [InlineData(15956u, 0u, 28785u, 120000u)]
    [InlineData(15953u, 1u, 28798u, 60000u)]
    [InlineData(15952u, 2u, 29484u, 40000u)]
    [InlineData(15954u, 3u, 29216u, 90000u)]
    [InlineData(15936u, 4u, 30211u, 90000u)]
    [InlineData(16011u, 5u, 29204u, 120000u)]
    public void Boss_EntersPhaseAndCompletesOnDeath(uint entry, uint slot, uint phaseSpell, uint phaseAt)
    {
        using var raid = new Raid(entry);
        Assert.Equal(EncounterState.InProgress, raid.Instance.GetData(slot));
        raid.AI.OnUpdate(phaseAt);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == phaseSpell);
        raid.Map.Combat.Kill(raid.Tank, raid.Boss);
        Assert.Equal(EncounterState.Done, raid.Instance.GetData(slot));
    }

    [Fact]
    public void Faerlina_WidowsEmbraceHit_RemovesAndDelaysEnrage()
    {
        // vmangos boss_faerlinaAI::SpellHit: enrage removed, enrage timer at least 30 s, no volley meanwhile.
        using var raid = new Raid(15953, 16506);
        Creature worshipper = raid.Creatures.SpawnTemporary(Template(16506), 2, 0, 0, 0);
        raid.AI.OnUpdate(50000);
        raid.AI.OnSpellHit(worshipper, SpellTestKit.Spell(28732));
        Assert.Contains(raid.Caster.RemovedAuras, r => r.Unit == raid.Boss && r.Spell == 28798);
        int volleys = raid.Caster.Casts.Count(c => c.Spell == 28796);
        raid.AI.OnUpdate(29000);
        Assert.DoesNotContain(raid.Caster.Casts, c => c.Spell == 28798);
        Assert.Equal(volleys, raid.Caster.Casts.Count(c => c.Spell == 28796));
        raid.AI.OnUpdate(1000);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 28798);
    }

    [Fact]
    public void Faerlina_PlainWorshipperDeath_IsNotWidowsEmbrace()
    {
        // 1.12: only a mind-controlled worshipper's cast embraces Faerlina; killing one does nothing.
        using var raid = new Raid(15953, 16506);
        Creature worshipper = raid.Creatures.SpawnTemporary(Template(16506), 2, 0, 0, 0);
        raid.Map.Combat.Kill(raid.Tank, worshipper);
        Assert.DoesNotContain(raid.Caster.Casts, c => c.Spell == 28732);
        raid.AI.OnUpdate(60000);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 28798);
    }

    [Fact]
    public void WidowsEmbrace_KillsTheCastingWorshipper()
    {
        // vmangos Spell::EffectScriptEffect case 28732: the caster dies.
        SpellInfo embrace = SpellTestKit.Spell(28732,
            SpellTestKit.Effect(SpellEffectName.ScriptEffect, 0));
        using var kit = new SpellTestKit(embrace);
        SpellScriptDispatcher.Install(kit.System, new SpellScriptRegistry([new WidowsEmbraceScript()]));
        Player caster = ConditionTestSupport.CreatePlayer();
        kit.World.AddPlayer(caster); kit.World.RunTick(0);
        Assert.True(caster.IsAlive);
        Assert.Equal(SpellCastResult.CastOk,
            kit.System.CastSpell(caster, 28732, SpellCastTargets.ForSelf(), triggered: true));
        Assert.False(caster.IsAlive);
    }

    [Fact]
    public void AnubRekhan_StartsWithTwoGuards_AndLocustAddsAnother()
    {
        using var raid = new Raid(15956, 16573);
        Assert.Equal(2, raid.Creatures.Creatures.Count(c => c.Entry == 16573));
        raid.AI.OnUpdate(120000);
        Assert.Equal(3, raid.Creatures.Creatures.Count(c => c.Entry == 16573));
    }

    [Fact]
    public void CryptGuard_UsesWebAcidCleaveAndHalfHealthEnrage()
    {
        using var raid = new Raid(15956, 16573);
        Creature guard = raid.Creatures.Creatures.First(c => c.Entry == 16573);
        var ai = Assert.IsType<NaxxramasCryptGuardAI>(guard.AI);
        guard.Health = guard.MaxHealth / 2;
        ai.OnUpdate(12000);
        foreach (uint spell in new uint[] { 28747, 28991, 26350, 28969 })
            Assert.Contains(raid.Caster.Casts, c => c.Spell == spell);
    }

    [Fact]
    public void AnubRekhan_PlayerDeathSpawnsFiveScarabs()
    {
        using var raid = new Raid(15956, 16573, 16698);
        Player survivor = TestWorld.CreatePlayer(2, 2, 0, new FakeSession(), 533);
        survivor.Relocate(2, 0, 0, 0, 0); raid.World.AddPlayer(survivor); raid.World.RunTick(0);
        raid.Map.Combat.Kill(raid.Boss, raid.Tank);
        raid.Instance.Update(0);
        Assert.Equal(5, raid.Creatures.Creatures.Count(c => c.Entry == 16698));
    }

    [Fact]
    public void AnubRekhan_DeadCryptGuardExplodesOnCorpseTimer()
    {
        using var raid = new Raid(15956, 16573, 16698);
        Creature guard = raid.Creatures.Creatures.First(c => c.Entry == 16573);
        raid.Map.Combat.Kill(raid.Tank, guard);
        Assert.DoesNotContain(raid.Creatures.Creatures, c => c.Entry == 16698);
        raid.AI.OnUpdate(80000);
        Assert.Equal(10, raid.Creatures.Creatures.Count(c => c.Entry == 16698));
    }

    [Fact]
    public void Noth_BalconyIsImmune_SummonsWave_ThenReturns()
    {
        using var raid = new Raid(15954);
        raid.AI.OnUpdate(90000);
        Assert.True(raid.AI.IsOnBalcony);
        Assert.Equal(UnitFlags.NotSelectable | UnitFlags.ImmuneToPlayer,
            raid.Boss.UnitFlags & (UnitFlags.NotSelectable | UnitFlags.ImmuneToPlayer));
        raid.AI.OnUpdate(7000);
        Assert.Contains(raid.Caster.Casts, c => c.Spell is 29217 or 29227);
        raid.AI.OnUpdate(63000);
        Assert.False(raid.AI.IsOnBalcony);
        Assert.Equal(0u, (uint)(raid.Boss.UnitFlags & (UnitFlags.NotSelectable | UnitFlags.ImmuneToPlayer)));
    }

    [Fact]
    public void Noth_SecondAndThirdBalconies_AddGuardiansThenConstructs()
    {
        using var raid = new Raid(15954, 16982);
        raid.AI.OnUpdate(90000); raid.AI.OnUpdate(7000); raid.AI.OnUpdate(63000);
        raid.AI.OnUpdate(110000); raid.AI.OnUpdate(7000);
        Assert.Contains(raid.Caster.Casts, c => c.Spell is 29226 or 29239);
        raid.AI.OnUpdate(88000);
        raid.AI.OnUpdate(180000); raid.AI.OnUpdate(7000);
        Assert.Equal(3, raid.Creatures.Creatures.Count(c => c.Entry == 16982));
    }

    [Fact]
    public void Heigan_DanceUsesFissureCasterAndReturnsToGround()
    {
        using var raid = new Raid(15936, 17293);
        raid.AI.OnUpdate(90000);
        Assert.True(raid.AI.IsDancing);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 29371);
        raid.AI.OnUpdate(45000);
        Assert.False(raid.AI.IsDancing);
    }

    [Fact]
    public void Heigan_EruptionActivatesOnlyUnsafeImportedTrapSections()
    {
        using var raid = new Raid(15936, 17293);
        var spells = new FakeObjectSpells();
        uint[] entries = [181517, 181510, 181534, 181545];
        var objects = new GameObjectMapSystem(raid.Map, new GameObjectContent(
            entries.Select(e => GameObjectTestKit.GoTemplate(e, GameObjectType.Trap,
                (3, 29371))).ToArray(), [], [], [], [])) { Spells = spells };
        raid.Map.AddUpdater(objects);
        foreach (uint entry in entries) objects.Summon(entry, 0, 0, 0, 0);
        raid.AI.OnUpdate(15000); // first eruption: section 0 is safe
        Assert.DoesNotContain(spells.Casts, c => c.Source.Entry == 181517);
        Assert.Equal([181510u, 181534u, 181545u],
            spells.Casts.Select(c => c.Source.Entry).Order().ToArray());
    }

    [Fact]
    public void Heigan_PortsNonTankPlayersOnceAt40s_ThenAt18And48sAfterTheDance()
    {
        // vmangos boss_heiganAI::Aggro (port at 40 s), EventDanceEnd (ports at 18 and 48 s),
        // EventPortPlayer (skip the tank, never the same player twice per dance rotation).
        using var raid = new Raid(15936, 17293);
        Player first = TestWorld.CreatePlayer(2, 2, 0, new FakeSession(), 533);
        first.Relocate(2, 0, 0, 0, 0); raid.World.AddPlayer(first);
        Player second = TestWorld.CreatePlayer(3, 3, 0, new FakeSession(), 533);
        second.Relocate(3, 0, 0, 0, 0); raid.World.AddPlayer(second); raid.World.RunTick(0);
        raid.Boss.Combat.Threat.AddThreat(raid.Tank, 1000);
        raid.Boss.Combat.Threat.AddThreat(first, 100);
        raid.Boss.Combat.Threat.AddThreat(second, 50);
        Unit?[] Ports() => raid.Caster.Casts.Where(c => c.Spell == 29273).Select(c => c.Target).ToArray();
        raid.AI.OnUpdate(39999);
        Assert.Empty(Ports());
        raid.AI.OnUpdate(1);
        Assert.Equal(2, Ports().Length);
        Assert.DoesNotContain(raid.Tank, Ports());
        raid.AI.OnUpdate(49999); // up to the dance at 90 s: no repeat port
        Assert.Equal(2, Ports().Length);
        raid.AI.OnUpdate(1);
        Assert.True(raid.AI.IsDancing);
        raid.AI.OnUpdate(45000);
        Assert.False(raid.AI.IsDancing);
        raid.AI.OnUpdate(18000);
        Assert.Equal(4, Ports().Length);
        raid.AI.OnUpdate(30000); // 48 s: both were ported this rotation, so nobody is left
        Assert.Equal(4, Ports().Length);
    }

    [Fact]
    public void LaterWingGates_AndHeiganExitDoor_KeepTheirDatabaseState()
    {
        // Part 1 drives only the Arachnid/Plague doors: an unscripted wing's start-open gate
        // (ClassicDB 181124 data0=1) must not be shut by an encounter nothing completes.
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(533);
        var raid = new NaxxramasInstance(map); raid.Initialize(); map.AddUpdater(raid);
        var objects = new GameObjectMapSystem(map, new GameObjectContent(
            [GameObjectTestKit.GoTemplate(181124, GameObjectType.Door, (0, 1)),
             GameObjectTestKit.GoTemplate(181203, GameObjectType.Door, (0, 1))], [], [], [], []));
        map.AddUpdater(objects);
        GameObject gothik = objects.Summon(181124, 0, 0, 0, 0)!;
        GameObject heiganExit = objects.Summon(181203, 0, 0, 0, 0)!;
        // The database spawn's start-open state (the test kit's Summon does not apply data0).
        gothik.State = heiganExit.State = GameObjectState.Active;
        foreach (uint state in new[] { EncounterState.InProgress, EncounterState.Fail, EncounterState.Done })
            for (uint slot = 0; slot < 15; slot++)
            {
                raid.SetData(slot, state);
                Assert.Equal(GameObjectState.Active, gothik.State);
                Assert.Equal(GameObjectState.Active, heiganExit.State);
            }
    }

    [Fact]
    public void Maexxna_ThirtyPercentFrenzyFiresOnce()
    {
        using var raid = new Raid(15952);
        raid.Boss.Health = raid.Boss.MaxHealth * 29 / 100;
        raid.AI.OnUpdate(1);
        raid.AI.OnUpdate(1);
        Assert.Single(raid.Caster.Casts, c => c.Spell == 28747);
    }

    [Fact]
    public void Maexxna_WrapsNonTank_ThenSpawnsCocoon()
    {
        using var raid = new Raid(15952, 16486);
        Player healer = TestWorld.CreatePlayer(2, 2, 0, new FakeSession(), 533);
        healer.Relocate(2, 0, 0, 0, 0); raid.World.AddPlayer(healer); raid.World.RunTick(0);
        raid.Boss.Combat.Threat.AddThreat(raid.Tank, 1000);
        raid.Boss.Combat.Threat.AddThreat(healer, 100);
        raid.AI.OnUpdate(20000);
        Assert.Contains(healer.X, new[] { 3562.40f, 3560.78f, 3554.95f, 3549.02f,
            3538.34f, 3526.43f, 3507.84f, 3493.35f });
        raid.AI.OnUpdate(1999);
        Assert.DoesNotContain(raid.Caster.Casts, c => c.Spell == 28622);
        raid.AI.OnUpdate(1);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 28622);
        raid.AI.OnUpdate(3000);
        Assert.Single(raid.Creatures.Creatures, c => c.Entry == 16486);
    }

    [Fact]
    public void Loatheb_DoomAcceleratesAfterSeventhCast()
    {
        using var raid = new Raid(16011);
        raid.AI.OnUpdate(120000);
        for (int i = 0; i < 6; i++) raid.AI.OnUpdate(30000);
        Assert.Equal(7, raid.Caster.Casts.Count(c => c.Spell == 29204));
        raid.AI.OnUpdate(15000);
        Assert.Equal(8, raid.Caster.Casts.Count(c => c.Spell == 29204));
    }

    [Fact]
    public void Loatheb_SporesUseOneReferenceSideEveryThirteenSeconds()
    {
        using var raid = new Raid(16011, 16286);
        raid.AI.OnUpdate(13000);
        Creature first = Assert.Single(raid.Creatures.Creatures, c => c.Entry == 16286);
        raid.AI.OnUpdate(13000);
        Creature[] spores = raid.Creatures.Creatures.Where(c => c.Entry == 16286).ToArray();
        Assert.Equal(2, spores.Length);
        Assert.All(spores, c => Assert.Equal(first.X, c.X));
        Assert.Contains(first.X, new[] { 2951f, 2870f });
    }

    [Fact]
    public void CorruptedMind_AppliesDruidClassRestriction()
    {
        SpellInfo parent = SpellTestKit.Spell(29201,
            SpellTestKit.Effect(SpellEffectName.ScriptEffect, 0));
        SpellInfo child = SpellTestKit.Spell(29194,
            SpellTestKit.Effect(SpellEffectName.Heal, 10));
        using var kit = new SpellTestKit(parent, child);
        SpellScriptDispatcher.Install(kit.System, new SpellScriptRegistry([new NaxxramasSpellScripts()]));
        Player druid = ConditionTestSupport.CreatePlayer(cls: Class.Druid);
        kit.World.AddPlayer(druid); kit.World.RunTick(0);
        druid.Health = 10;
        Assert.Equal(SpellCastResult.CastOk,
            kit.System.CastSpell(druid, 29201, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(20u, druid.Health);
    }
}

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
using ArcaneCore.Kernel.WorldData.Creatures;
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

        public Raid(uint entry, params uint[] extras) : this(entry, extras, [], null)
        {
        }

        /// <param name="spawns">Database spawns of map 533 (loaded with the tank's grid).</param>
        /// <param name="spells">The creature spell seam; the recording <see cref="Caster"/> when null.</param>
        public Raid(uint entry, uint[] extras, CreatureSpawn[] spawns, ICreatureSpellCaster? spells)
        {
            Map = World.GetMap(533);
            Instance = new NaxxramasInstance(Map); Instance.Initialize(); Map.AddUpdater(Instance);
            uint[] entries = [entry, .. extras];
            Creatures = new CreatureMapSystem(Map, Content(entries.Select(e => Template(e)).ToArray(), spawns),
                new CreatureOptions { AggroRate = 0, RespawnPacifyMs = 0 }, random: new Random(1),
                aiServices: new CreatureAiServices { Spells = spells ?? Caster, Hostility = new AlwaysHostile() });
            Map.AddUpdater(Creatures);
            Tank = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(), 533);
            Tank.Relocate(0, 0, 0, 0, 0); World.AddPlayer(Tank); World.RunTick(0);
            Boss = Creatures.SpawnTemporary(Template(entry), 1, 0, 0, 0);
            AI.AttackStart(Tank);
        }
        public Player AddPlayer(uint guid, float x, PowerType power = PowerType.Rage)
        {
            Player player = power == PowerType.Mana ? ManaPlayer(guid) : TestWorld.CreatePlayer(guid, x, 0, new FakeSession(), 533);
            player.Relocate(x, 0, 0, 0, 0); World.AddPlayer(player); World.RunTick(0);
            return player;
        }

        public void Dispose() => World.Dispose();
    }

    private static Player ManaPlayer(uint guid)
    {
        var character = new ArcaneCore.Kernel.Characters.CharacterRecord
        {
            Id = (int)guid, AccountId = 1, Name = $"P{guid}", Race = (byte)Race.Human, Class = (byte)Class.Mage,
            Gender = (byte)Gender.Male, Level = 60, MapId = 533, ZoneId = 3456, X = 0, Y = 0, Z = 0,
        };
        var appearance = new PlayerAppearance(
            DisplayId: 49, FactionTemplate: 1, PowerType.Mana, BaseHealth: 3000, BaseMana: 4000,
            MaxHealth: 3000, MaxPower: 4000, StartPower: 4000, NextLevelXp: 0);
        return new Player(character, appearance, new FakeSession());
    }

    // ClassicDB z2815 spell_template 29273 "Teleport" (Heigan's port): effect 1 TELEPORT_UNITS, implicit targets A UNIT_CASTER (1) and
    // B spell_target_position (17); effect 2 SANCTUARY (79) at UNIT_CASTER. spell_target_position 29273: map 533, 2905.63,-3769.96,273.62.
    private static SpellInfo HeiganPort() => SpellTestKit.Spell(29273,
        SpellTestKit.Effect(SpellEffectName.TeleportUnits, 0, SpellImplicitTarget.UnitCaster, targetB: SpellImplicitTarget.LocationDatabase),
        SpellTestKit.Effect(SpellEffectName.Sanctuary, 0, SpellImplicitTarget.UnitCaster)) with { Range = new SpellRange(0, 50000) };

    // ClassicDB z2815 spell_template 28622 "Web Wrap": duration index 3 (60 s), effect 1 APPLY_AURA 12 (stun) and effect 2 APPLY_AURA 3
    // (periodic damage 657 every 2 s), both at UNIT_CASTER (1).
    private static SpellInfo WebWrapAura() => SpellTestKit.Spell(28622,
        SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.ModStun),
        SpellTestKit.Effect(SpellEffectName.ApplyAura, 657, SpellImplicitTarget.UnitCaster, AuraType.PeriodicDamage, amplitude: 2000))
        with { Duration = new SpellDuration(60000, 0, 60000) };
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
    public void FourWingClears_EnableHubPortal_AndStopVetoingTheFrostwyrmTeleportRow()
    {
        // mangos-classic naxxramas.cpp DoHandleAreaTrigger: ClassicDB's areatrigger_teleport 4156 does the teleport, the script
        // only blocks it until the four wings are done (the World test NaxxramasFrostwyrmGateTests runs the row's evaluation).
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
        Assert.True(raid.BlocksAreaTriggerTeleport(player, NaxxramasInstance.FrostwyrmTrigger));
        foreach (uint boss in new uint[] { 2, 5, 8 })
        {
            raid.SetData(boss, EncounterState.Done);
            Assert.True(raid.BlocksAreaTriggerTeleport(player, NaxxramasInstance.FrostwyrmTrigger));
        }
        raid.SetData(12, EncounterState.Done);
        Assert.True(raid.WingsCleared);
        Assert.Equal(GameObjectState.Active, hub.State);
        Assert.False(raid.BlocksAreaTriggerTeleport(player, NaxxramasInstance.FrostwyrmTrigger));
        // The script never teleports on its own: the database row would run as well and move the player twice.
        raid.OnAreaTrigger(player, NaxxramasInstance.FrostwyrmTrigger);
        Assert.Empty(teleports.Calls);
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

    // ClassicDB z2815 map-533 Crypt Guard spawns 5331024 and 5331025 (placed next to the test tank so their grid loads).
    private static CreatureSpawn[] DatabaseGuards() =>
        [Spawn(5331024, 16573, 4, 4, 0, mapId: 533), Spawn(5331025, 16573, 4, -4, 0, mapId: 533)];

    [Fact]
    public void AnubRekhan_UsesTheTwoDatabaseGuards_AndLocustAddsAnother()
    {
        // mangos-classic boss_anubrekhan.cpp: the pre-pull guards are the database spawns, never summoned on top of them.
        using var raid = new Raid(15956, [16573], DatabaseGuards(), null);
        Creature[] guards = raid.Creatures.Creatures.Where(c => c.Entry == 16573).ToArray();
        Assert.Equal(2, guards.Length);
        Assert.All(guards, g => Assert.NotNull(g.Spawn));
        Assert.All(guards, g => Assert.True(g.Combat.IsInCombat)); // FLAG_AGGRO_ON_AGGRO
        raid.AI.OnUpdate(120000);
        Assert.Equal(3, raid.Creatures.Creatures.Count(c => c.Entry == 16573));
    }

    [Fact]
    public void AnubRekhan_EvadeDropsTheSummonedGuard_AndRespawnsTheDeadDatabaseGuard()
    {
        // mangos-classic boss_anubrekhanAI::EnterEvadeMode (SPELL_DESPAWN_GUARDS) then creature_linking FLAG_RESPAWN_ON_EVADE.
        using var raid = new Raid(15956, [16573, 16698], DatabaseGuards(), null);
        raid.AI.OnUpdate(120000); // locust swarm summons a third guard
        Creature dead = raid.Creatures.Creatures.First(c => c.Entry == 16573 && c.Spawn is not null);
        raid.Map.Combat.Kill(raid.Tank, dead);
        raid.AI.OnEvade();
        Creature[] guards = raid.Creatures.Creatures.Where(c => c.Entry == 16573).ToArray();
        Assert.Equal(2, guards.Length);
        Assert.All(guards, g => Assert.NotNull(g.Spawn));
        Assert.All(guards, g => Assert.True(g.IsAlive));
        Assert.Equal(EncounterState.Fail, raid.Instance.GetData(NaxxramasInstance.AnubRekhan));
    }

    [Fact]
    public void CryptGuard_UsesWebAcidCleaveAndHalfHealthEnrage()
    {
        using var raid = new Raid(15956, [16573], DatabaseGuards(), null);
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
        using var raid = new Raid(15956, [16573, 16698], DatabaseGuards(), null);
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
        // vmangos boss_heiganAI::EventStartDance: a plague-wave creature at each of the 20 eye stalks casts Plague Wave on itself.
        Assert.Equal(20, raid.Caster.Casts.Count(c => c.Spell == 30243));
        Assert.Contains(raid.Creatures.Creatures, c => c.Entry == 17293 && c.X == 2761.28f && c.Y == -3765.37f);
        raid.AI.OnUpdate(4000);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 29371);
        raid.AI.OnUpdate(41000);
        Assert.False(raid.AI.IsDancing);
    }

    [Fact]
    public void Heigan_ManaBurnOnlyWithAManaUserWithin28Yards_ThenEveryThreeSeconds()
    {
        // vmangos boss_heiganAI::CheckManausersAndRepeat: looked at from 15 s, again every 1 s, 3 s after a cast.
        using var raid = new Raid(15936, 17293);
        Player mage = raid.AddPlayer(2, 60, PowerType.Mana); // 59 yd from Heigan
        raid.Boss.Combat.Threat.AddThreat(raid.Tank, 1000);
        raid.Boss.Combat.Threat.AddThreat(mage, 10);
        int Burns() => raid.Caster.Casts.Count(c => c.Spell == 29310);
        raid.AI.OnUpdate(15000);
        Assert.Equal(0, Burns());
        mage.Relocate(20, 0, 0, 0, 0); // 19 yd
        raid.AI.OnUpdate(999);
        Assert.Equal(0, Burns());
        raid.AI.OnUpdate(1);
        Assert.Equal(1, Burns());
        Assert.Equal(raid.Boss, raid.Caster.Casts.Last(c => c.Spell == 29310).Target);
        raid.AI.OnUpdate(2999);
        Assert.Equal(1, Burns());
        raid.AI.OnUpdate(1);
        Assert.Equal(2, Burns());
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
        // mangos-classic boss_heigan.cpp: the ported player is the caster (target->CastSpell(target, SPELL_TELEPORT_PLAYERS)).
        Unit?[] Ports()
        {
            Assert.DoesNotContain(raid.Caster.Casts, c => c.Spell == 29273);
            var ports = raid.Caster.UnitCasts.Where(c => c.Spell == 29273).ToArray();
            Assert.All(ports, c => Assert.Same(c.Caster, c.Target));
            return ports.Select(c => (Unit?)c.Caster).ToArray();
        }
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
    public void Heigan_PortMovesThePlayer_NotHeigan_WithTheClassicDbSpell()
    {
        var spells = new SpellSystem(new SpellStore([HeiganPort()], [],
            [(29273, new SpellTargetPosition(533, 2905.63f, -3769.96f, 273.62f, 3.13f))]), () => 10000);
        using var raid = new Raid(15936, [17293], [], new SpellSystemCreatureCaster(spells));
        Player ported = raid.AddPlayer(2, 2);
        raid.Boss.Combat.Threat.AddThreat(raid.Tank, 1000);
        raid.Boss.Combat.Threat.AddThreat(ported, 100);
        (float X, float Y, float Z) heigan = (raid.Boss.X, raid.Boss.Y, raid.Boss.Z);
        raid.AI.OnUpdate(40000);
        Assert.Equal((2905.63f, -3769.96f, 273.62f), (ported.X, ported.Y, ported.Z));
        Assert.Equal(heigan, (raid.Boss.X, raid.Boss.Y, raid.Boss.Z));
        Assert.Equal((0f, 0f), (raid.Tank.X, raid.Tank.Y)); // the tank is skipped
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
        Assert.DoesNotContain(raid.Caster.UnitCasts, c => c.Spell == 28622);
        raid.AI.OnUpdate(1);
        // vmangos boss_maexxnaAI::UpdateWraps: pl->CastSpell(pl, 28622, true); Maexxna never casts it.
        Assert.Contains(raid.Caster.UnitCasts, c => c.Spell == 28622 && c.Caster == healer && c.Target == healer);
        Assert.DoesNotContain(raid.Caster.Casts, c => c.Spell == 28622);
        raid.AI.OnUpdate(3000);
        Assert.Single(raid.Creatures.Creatures, c => c.Entry == 16486);
    }

    [Fact]
    public void Maexxna_WebWrapLandsOnThePlayer_AndTheCocoonsDeathReleasesIt()
    {
        var spells = new SpellSystem(new SpellStore([WebWrapAura()], [], []), () => 10000);
        using var raid = new Raid(15952, [16486], [], new SpellSystemCreatureCaster(spells));
        Player healer = raid.AddPlayer(2, 2);
        raid.Boss.Combat.Threat.AddThreat(raid.Tank, 1000);
        raid.Boss.Combat.Threat.AddThreat(healer, 100);
        raid.AI.OnUpdate(20000);
        raid.AI.OnUpdate(2000);
        Assert.True(spells.HasAura(healer, 28622));
        Assert.False(spells.HasAura(raid.Boss, 28622));
        raid.AI.OnUpdate(3000);
        Creature cocoon = Assert.Single(raid.Creatures.Creatures, c => c.Entry == 16486);
        // vmangos mob_webwrapAI::JustDied: killing the cocoon frees the wrapped player.
        raid.Map.Combat.Kill(raid.Tank, cocoon);
        Assert.False(spells.HasAura(healer, 28622));
    }

    [Fact]
    public void Maexxna_WrapWithNobodyToWrap_StillWaitsTheFullCooldown()
    {
        // vmangos boss_maexxnaAI::UpdateAI resets the 40 s web wrap timer whether or not DoCastWebWrap found anyone.
        using var raid = new Raid(15952, 16486);
        raid.Boss.Combat.Threat.AddThreat(raid.Tank, 1000);
        raid.AI.OnUpdate(20000); // only the tank: nobody to wrap
        Player healer = raid.AddPlayer(2, 2);
        raid.Boss.Combat.Threat.AddThreat(healer, 100);
        raid.AI.OnUpdate(39999);
        Assert.Equal(2f, healer.X);
        raid.AI.OnUpdate(1);
        Assert.NotEqual(2f, healer.X);
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

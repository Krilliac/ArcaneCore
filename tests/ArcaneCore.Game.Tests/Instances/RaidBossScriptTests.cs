using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class RaidBossScriptTests
{
    private sealed class MapUnits : ISpellUnitResolver
    {
        public Unit? Find(Unit reference, ObjectGuid guid) => reference.Map?.FindObject(guid) as Unit;
    }

    private sealed class ObjectCaster(SpellSystem spells) : IGameObjectSpells
    {
        public List<SpellCastResult> Results { get; } = [];
        public bool Cast(GameObject source, uint spellId, Unit target, Unit? unitCaster)
        {
            SpellCastResult result = spells.CastForGameObject(source, spellId, target, unitCaster);
            Results.Add(result);
            return result == SpellCastResult.CastOk;
        }
        public float? MaxRange(uint spellId) => spells.Store.Get(spellId)?.Range.Max;
        public bool IsChanneling(Unit unit) => false;
        public void StartRitualAnimation(Player helper, uint animSpellId, GameObject ritual) { }
        public bool CastRitualSpell(GameObject ritual, uint spellId, Unit caster, ObjectGuid summonTarget) => false;
        public void StartCreatingSpellCooldown(Player owner, uint spellId) { }
    }

    private sealed class Raid : IDisposable
    {
        public WorldRuntime World { get; } = TestWorld.CreateRuntime();
        public FakeCaster Caster { get; } = new();
        public Map Map { get; }
        public InstanceData Data { get; }
        public CreatureMapSystem Creatures { get; }
        public Creature Boss { get; }
        public Player Tank { get; }

        public Raid(uint map, uint entry)
        {
            Map = World.GetMap(map);
            Data = Assert.IsAssignableFrom<InstanceData>(InstanceScriptRegistry.Default.Create(Map));
            Data.Initialize();
            Map.AddUpdater(Data);
            Creatures = new CreatureMapSystem(Map, Content([Template(entry)], []),
                new CreatureOptions { AggroRate = 0, RespawnPacifyMs = 0 }, random: new Random(1),
                aiServices: new CreatureAiServices { Spells = Caster, Hostility = new AlwaysHostile() });
            Map.AddUpdater(Creatures);
            Tank = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(), map);
            float z = map == 309 ? 50 : 450;
            Tank.Relocate(0, 0, z, 0, 0);
            World.AddPlayer(Tank);
            World.RunTick(0);
            Boss = Creatures.SpawnTemporary(Template(entry), 1, 0, z, 0);
            Boss.AI!.AttackStart(Tank);
        }

        public void Dispose() => World.Dispose();
    }

    [Theory]
    [InlineData(469u, 12017u, 2u, "BroodlordAI")]
    [InlineData(469u, 11983u, 3u, "FiremawAI")]
    [InlineData(469u, 11981u, 5u, "FlamegorAI")]
    [InlineData(509u, 15348u, 0u, "KurinnaxxAI")]
    public void Spawn_SelectsRaidAi_AndDeathCompletesEncounter(uint map, uint entry, uint encounter, string ai)
    {
        using var raid = new Raid(map, entry);
        Assert.Equal(ai, raid.Boss.AI!.GetType().Name);
        Assert.Equal(EncounterState.InProgress, raid.Data.GetData(encounter));
        raid.Map.Combat.Kill(raid.Tank, raid.Boss);
        Assert.Equal(EncounterState.Done, raid.Data.GetData(encounter));
        Assert.Contains("3", raid.Data.GetSaveData()!);
    }

    [Fact]
    public void Broodlord_CleaveAtEightSeconds_AndKnockAwayHalvesOnlyTheHitTargetsThreat()
    {
        using var raid = new Raid(469, 12017);
        raid.Boss.AI!.OnUpdate(7999);
        Assert.Empty(raid.Caster.Casts);
        raid.Boss.AI.OnUpdate(1);
        Assert.Equal(15284u, Assert.Single(raid.Caster.Casts).Spell);
        raid.Boss.Combat.Threat.AddThreat(raid.Tank, 100);
        float before = raid.Boss.Combat.Threat.GetThreat(raid.Tank);
        raid.Caster.RaiseHit(raid.Boss, raid.Tank, new SpellInfo { Id = 18670 });
        Assert.Equal(before / 2, raid.Boss.Combat.Threat.GetThreat(raid.Tank));
        raid.Boss.AI.OnReachedHome();
        Assert.Equal(EncounterState.Fail, raid.Data.GetData(2));
    }

    [Theory]
    [InlineData(11983u, 23341u, 5000u)]
    [InlineData(11981u, 23342u, 10000u)]
    public void Drakes_CastTheirDistinctMechanic_AndWingBuffetReducesThreat(uint entry, uint spell, uint delay)
    {
        using var raid = new Raid(469, entry);
        raid.Boss.AI!.OnUpdate(delay - 1);
        Assert.DoesNotContain(raid.Caster.Casts, c => c.Spell == spell);
        raid.Boss.AI.OnUpdate(1);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == spell);
        raid.Boss.Combat.Threat.AddThreat(raid.Tank, 100);
        float before = raid.Boss.Combat.Threat.GetThreat(raid.Tank);
        raid.Caster.RaiseHit(raid.Boss, raid.Tank, new SpellInfo { Id = 23339 });
        Assert.Equal(before / 2, raid.Boss.Combat.Threat.GetThreat(raid.Tank));
    }

    [Fact]
    public void Kurinnaxx_EnragesOnceAtThirtyPercent_AndRetriesAfterReset()
    {
        using var raid = new Raid(509, 15348);
        raid.Boss.Health = raid.Boss.MaxHealth * 30 / 100;
        raid.Boss.AI!.OnUpdate(1);
        raid.Boss.AI.OnUpdate(1);
        Assert.Single(raid.Caster.Casts, c => c.Spell == 26527);
        raid.Boss.AI.OnEvade();
        raid.Boss.AI.OnUpdate(1);
        Assert.Equal(2, raid.Caster.Casts.Count(c => c.Spell == 26527));
    }

    [Fact]
    public void Broodlord_LeashesBelowThePlatform_AndReloadKeepsCompletedDoorOpen()
    {
        using var raid = new Raid(469, 12017);
        raid.Boss.Relocate(1, 0, 448.59f, 0, 0);
        raid.Boss.AI!.OnUpdate(8000);
        Assert.True(raid.Boss.IsEvading);
        Assert.Empty(raid.Caster.Casts);

        var objects = new GameObjectMapSystem(raid.Map, new GameObjectContent(
            [GameObjectTestKit.GoTemplate(179365, GameObjectType.Door)], [], [], [], []));
        raid.Map.AddUpdater(objects);
        GameObject door = objects.Summon(179365, 1, 0, 450, 0)!;
        raid.Data.SetData(2, EncounterState.Done);
        Assert.Equal(GameObjectState.Active, door.State);
        raid.Data.SetData(2, EncounterState.Done);
        Assert.Equal(GameObjectState.Active, door.State); // duplicate notification must not toggle it closed
        raid.Data.SetData(3, EncounterState.InProgress);
        string saved = raid.Data.GetSaveData()!;
        raid.Data.Initialize();
        raid.Data.Load(saved);
        Assert.Equal(EncounterState.NotStarted, raid.Data.GetData(3));
        objects.Remove(door);
        Assert.Equal(GameObjectState.Active, objects.Summon(179365, 1, 0, 450, 0)!.State);
    }

    [Fact]
    public void SandTrap_ScriptSummonsAtThreatTarget_ActivatesAtFourSeconds_AndConsumesItsCharge()
    {
        using var raid = new Raid(509, 15348);
        var objectSpells = new FakeObjectSpells();
        // ClassicDB z2815: radius=0, spell=25656, charges=1. A proximity scan alone never triggers this object.
        var objects = new GameObjectMapSystem(raid.Map, new GameObjectContent(
            [GameObjectTestKit.GoTemplate(180647, GameObjectType.Trap, (3, 25656), (4, 1))], [], [], [], []))
        { Spells = objectSpells };
        raid.Map.AddUpdater(objects);
        var spells = new SpellSystem(new SpellStore(
            [SpellTestKit.Spell(26524, SpellTestKit.Effect(SpellEffectName.ScriptEffect, 0))], [], []), () => 10000);
        SpellScriptDispatcher.Install(spells, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(raid.Boss, 26524, SpellCastTargets.ForSelf(), triggered: true));
        GameObject trap = Assert.Single(objects.GameObjects);
        Assert.Equal(raid.Tank.X, trap.X);
        Assert.Equal(raid.Tank.Y, trap.Y);
        Assert.Equal(raid.Tank.Z, trap.Z);
        objects.Update(raid.Map, 3999);
        Assert.Empty(objectSpells.Casts);
        objects.Update(raid.Map, 1);
        Assert.Equal(25656u, Assert.Single(objectSpells.Casts).Spell);
        Assert.Empty(objects.GameObjects);
        objects.Update(raid.Map, 10000);
        Assert.Single(objectSpells.Casts);
    }

    [Fact]
    public void SandTrap_MissingTemplateDoesNotFabricateAnObject()
    {
        using var raid = new Raid(509, 15348);
        var objects = new GameObjectMapSystem(raid.Map, new GameObjectContent([], [], [], [], []));
        raid.Map.AddUpdater(objects);
        var spells = new SpellSystem(new SpellStore(
            [SpellTestKit.Spell(26524, SpellTestKit.Effect(SpellEffectName.ScriptEffect, 0))], [], []), () => 10000);
        SpellScriptDispatcher.Install(spells, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(raid.Boss, 26524, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Empty(objects.GameObjects);
    }

    [Fact]
    public void SandTrap_DestinationAreaDamagesThePlayerAtTheTrap_NotAtTheDistantBoss()
    {
        using var raid = new Raid(509, 15348);
        raid.Tank.Relocate(30, 0, 450, 0, 0);
        var relations = new FakeRelations();
        relations.Hostile.Add(raid.Boss.Guid);
        var spells = new SpellSystem(new SpellStore(
            [SpellTestKit.Spell(26524, SpellTestKit.Effect(SpellEffectName.ScriptEffect, 0)),
             // Spell.sql 25656 uses TARGET_LOCATION_CASTER_DEST + enemies at destination.
             SpellTestKit.Spell(25656, SpellTestKit.Effect(SpellEffectName.SchoolDamage, 7,
                 SpellImplicitTarget.LocationCasterDest, targetB: SpellImplicitTarget.EnumUnitsEnemyAoeAtDestLoc) with { Radius = 6 })],
            [], []), () => 10000, units: new MapUnits(), random: new Random(1)) { Relations = relations };
        SpellScriptDispatcher.Install(spells, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        var adapter = new ObjectCaster(spells);
        var objects = new GameObjectMapSystem(raid.Map, new GameObjectContent(
            [GameObjectTestKit.GoTemplate(180647, GameObjectType.Trap, (3, 25656), (4, 1))], [], [], [], []))
        { Spells = adapter };
        raid.Map.AddUpdater(objects);
        uint health = raid.Tank.Health;
        Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(raid.Boss, 26524, SpellCastTargets.ForSelf(), triggered: true));
        objects.Update(raid.Map, 4000);
        Assert.Equal(SpellCastResult.CastOk, Assert.Single(adapter.Results));
        Assert.Equal(health - 7, raid.Tank.Health);
    }

    [Fact]
    public void Hakkar_PowerDownRemovesExactlyOneStack_ThenRemovesTheLastHolder()
    {
        using var kit = new SpellTestKit(
            SpellTestKit.Spell(24692, SpellTestKit.Effect(SpellEffectName.ApplyAura, 10, aura: AuraType.Dummy)) with
            { StackAmount = 5, Duration = new SpellDuration(-1, 0, -1) },
            SpellTestKit.Spell(24693, SpellTestKit.Effect(SpellEffectName.ScriptEffect, 0)));
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        (Player player, _) = kit.AddPlayer(1);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 24692, SpellCastTargets.ForSelf(), triggered: true));
        }

        Assert.Equal(5, Assert.Single(kit.System.GetAuras(player)).StackAmount);
        for (int expected = 4; expected >= 0; expected--)
        {
            Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 24693, SpellCastTargets.ForSelf(), triggered: true));
            if (expected == 0)
            {
                Assert.Empty(kit.System.GetAuras(player));
            }
            else
            {
                SpellAuraHolder aura = Assert.Single(kit.System.GetAuras(player));
                Assert.Equal(expected, aura.StackAmount);
                Assert.Equal(expected * 10, aura.Auras[0]!.Amount);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShadowFlame_TriggersTheDamageOverTimeOnlyWithoutOnyxiaScaleCloak(bool cloak)
    {
        using var kit = new SpellTestKit(
            SpellTestKit.Spell(22539, SpellTestKit.Effect(SpellEffectName.ScriptEffect, 0)),
            SpellTestKit.Spell(22682, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
            { Duration = new SpellDuration(10000, 0, 10000) },
            SpellTestKit.Spell(22683, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
            { Duration = new SpellDuration(10000, 0, 10000) });
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        (Player player, _) = kit.AddPlayer(1);
        if (cloak)
        {
            Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 22683, SpellCastTargets.ForSelf(), triggered: true));
        }

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 22539, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(!cloak, kit.System.HasAura(player, 22682));
    }

    [Fact]
    public void Hakkar_DeadPriestsDisableTheirAspects_AndBloodSiphonStartsAtNinetySeconds()
    {
        using var raid = new Raid(309, 14834);
        Assert.Equal("HakkarAI", raid.Boss.AI!.GetType().Name);
        Assert.Equal(5, raid.Caster.Casts.Count(c => c.Spell == 24692));
        for (uint priest = 0; priest < 5; priest++)
        {
            raid.Data.SetData(priest, EncounterState.Done);
        }

        raid.Caster.Casts.Clear();
        raid.Boss.AI.OnUpdate(89999);
        Assert.DoesNotContain(raid.Caster.Casts, c => c.Spell is >= 24686 and <= 24690);
        Assert.DoesNotContain(raid.Caster.Casts, c => c.Spell == 24324);
        raid.Boss.AI.OnUpdate(1);
        Assert.Single(raid.Caster.Casts, c => c.Spell == 24324);
        Assert.Equal("3 3 3 3 3 0 0 0", raid.Data.GetSaveData());
    }

    [Fact]
    public void Hakkar_InsanityRestoresCapturedThreatAfterTheAuraEnds()
    {
        using var raid = new Raid(309, 14834);
        raid.Boss.Combat.Threat.AddThreat(raid.Tank, 100);
        float before = raid.Boss.Combat.Threat.GetThreat(raid.Tank);
        raid.Boss.AI!.OnUpdate(17000);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 24327 && c.Target == raid.Tank);
        raid.Caster.Auras.Add((raid.Tank, 24327));
        raid.Boss.Combat.Threat.ModifyThreatPercent(raid.Tank, -100);
        raid.Boss.AI.OnUpdate(4000);
        Assert.Equal(0, raid.Boss.Combat.Threat.GetThreat(raid.Tank));
        raid.Caster.Auras.Remove((raid.Tank, 24327));
        raid.Boss.AI.OnUpdate(1);
        Assert.Equal(before, raid.Boss.Combat.Threat.GetThreat(raid.Tank));
    }

    [Theory]
    [InlineData(false, 24322u)]
    [InlineData(true, 24323u)]
    public void Hakkar_BloodSiphonSelectsPoisonDamageOrHealing(bool poisoned, uint expected)
    {
        using var kit = new SpellTestKit(
            SpellTestKit.Spell(24324, SpellTestKit.Effect(SpellEffectName.ScriptEffect, 0)),
            SpellTestKit.Spell(24322, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
            { Duration = new SpellDuration(10000, 0, 10000) },
            SpellTestKit.Spell(24323, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
            { Duration = new SpellDuration(10000, 0, 10000) },
            SpellTestKit.Spell(24321, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
            { Duration = new SpellDuration(10000, 0, 10000) });
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        (Player player, _) = kit.AddPlayer(1);
        if (poisoned)
        {
            Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 24321, SpellCastTargets.ForSelf(), triggered: true));
        }

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 24324, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(kit.System.HasAura(player, expected));
        Assert.False(kit.System.HasAura(player, poisoned ? 24322u : 24323u));
    }
}

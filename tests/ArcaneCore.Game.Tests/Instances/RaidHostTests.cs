using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.MoltenCore;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class RaidHostTests
{
    [Fact]
    public void Rune_OrdinaryClickAndWrongLockCannotDouse_ValidatedOpenLockCan()
    {
        using WorldRuntime world = TestWorld.CreateRuntime(); Map map = world.GetMap(0);
        var raid = new MoltenCoreInstance(map); map.AddUpdater(raid);
        var template = GoTemplate(176956, GameObjectType.Button, (1, 1459));
        var locks = new LockEntry(1459, [2, 0, 0, 0, 0, 0, 0, 0], [(uint)LockType.Open, 0, 0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0, 0, 0]);
        var objects = new GameObjectMapSystem(map, new GameObjectContent([template], [], [locks], [], [])); map.AddUpdater(objects);
        (Player player, _) = CreatureAiTestSupport.AddPlayer(world, 1, 0, 0);
        GameObject rune = objects.Summon(176956, 1, 0, 83.5f, 0)!;
        raid.SetData(1, EncounterState.Done);
        objects.Use(player, rune.Guid); Assert.Equal(EncounterState.Done, raid.GetData(1));
        Assert.Equal(GameObjectUseResult.Locked, objects.OpenLock(player, rune.Guid, LockType.Mining));
        Assert.Equal(EncounterState.Done, raid.GetData(1));
        Assert.Equal(GameObjectUseResult.Ok, objects.OpenLock(player, rune.Guid, LockType.Open));
        Assert.Equal(EncounterState.Special, raid.GetData(1)); Assert.Equal(GameObjectState.Active, rune.State);
    }
    /// <summary>The production resolver (ArcaneCore.World WorldSpellUnitResolver): any unit of the caster's map by GUID.</summary>
    private sealed class MapObjectResolver : ISpellUnitResolver
    {
        public Unit? Find(Unit reference, ObjectGuid guid)
            => guid.IsEmpty ? null : reference.Guid == guid ? reference : reference.Map?.FindObject(guid) as Unit;
    }
    private static SpellInfo Dummy(uint id) => SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.Dummy, 0));
    private static SpellInfo Heal(uint id) => SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.Heal, 10));
    [Theory]
    [InlineData(19411u, 20494u)]
    [InlineData(20474u, 20495u)]
    public void Magmadar_LavaBombExecutesItsFollowup(uint parent, uint child)
    {
        using var kit = new SpellTestKit(Dummy(parent), Heal(child));
        SpellScriptDispatcher.Install(kit.System, new SpellScriptRegistry([new MoltenCoreSpellScripts()]));
        (Player player, _) = kit.AddPlayer(1); player.Health = 10;
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, parent, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(20u, player.Health);
    }
    [Fact]
    public void Ragnaros_SonsDummyExecutesAllEightSummoningSpells()
    {
        using var kit = new SpellTestKit([Dummy(21108), .. Enumerable.Range(21110, 8).Select(id => Heal((uint)id))]);
        SpellScriptDispatcher.Install(kit.System, new SpellScriptRegistry([new MoltenCoreSpellScripts()]));
        (Player player, _) = kit.AddPlayer(1); player.Health = 10; player.MaxHealth = 1000;
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 21108, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(90u, player.Health);
    }
    [Fact]
    public void Ragnaros_LavaRandomizerExecutesExactlyOneChild()
    {
        uint[] children = [21886, 21900, 21901, 21902, 21903, 21904, 21905, 21906, 21907];
        using var kit = new SpellTestKit([Dummy(21908), .. children.Select(Heal)]);
        SpellScriptDispatcher.Install(kit.System, new SpellScriptRegistry([new MoltenCoreSpellScripts()]));
        (Player player, _) = kit.AddPlayer(1); player.Health = 10;
        kit.System.CastSpell(player, 21908, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(20u, player.Health);
    }
    [Theory]
    [InlineData(21094u, 21095u)]
    [InlineData(23487u, 23492u)]
    public void SeparationAnxiety_TicksAtFiveSeconds_StopsWhenAuraIsRemoved(uint aura, uint trigger)
    {
        SpellInfo anxiety = SpellTestKit.Spell(aura, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0,
            SpellImplicitTarget.Unit, aura: AuraType.Dummy) with
        { Radius = 20 }) with
        { RangeIndex = 3, Range = new SpellRange(0, 100), Duration = new SpellDuration(-1, 0, -1) };
        using var kit = new SpellTestKit(anxiety, Heal(trigger));
        Map map = kit.World.GetMap(0); var raid = new MoltenCoreInstance(map); map.AddUpdater(raid);
        (Player caster, _) = kit.AddPlayer(1); (Player target, _) = kit.AddPlayer(2, 50, 0);
        target.Health = 10;
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, aura, SpellCastTargets.ForUnit(target.Guid), triggered: true));
        raid.Update(4999); Assert.Equal(10u, target.Health);
        raid.Update(1); Assert.Equal(20u, target.Health);
        kit.System.RemoveAuras(target, aura); raid.Update(5000); Assert.Equal(20u, target.Health);
    }
    [Theory]
    [InlineData(20619u, 7u, 11663u, 11664u)]
    [InlineData(21087u, 7u, 11663u, 11663u)]
    [InlineData(19515u, 38u, 12057u, 12057u)]
    public void RaidScriptTargets_SelectOnlyTheReferencedEntries(uint spell, uint selector, uint first, uint second)
    {
        SpellInfo heal = SpellTestKit.Spell(spell, SpellTestKit.Effect(SpellEffectName.Heal, 10, (SpellImplicitTarget)selector) with { Radius = 100 })
            with
        { RangeIndex = 3, Range = new SpellRange(0, 100) };
        using var kit = new SpellTestKit(heal);
        Map map = kit.World.GetMap(0); map.AddUpdater(new MoltenCoreInstance(map));
        uint[] entries = [12018, 11663, 11664, 12057, 12099];
        var content = new CreatureContent(entries.Select(e => CreatureTestSupport.Template(e)), [], [], [], []);
        var creatures = new CreatureMapSystem(map, content); map.AddUpdater(creatures);
        var targets = entries.ToDictionary(e => e, e => creatures.SpawnTemporary(content.FindTemplate(e)!, 5, 0, 83.5f, 0));
        foreach (Creature creature in targets.Values) creature.Health = 10;
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(targets[12018], spell, SpellCastTargets.ForSelf(), triggered: true));
        foreach (var pair in targets)
            Assert.Equal(pair.Key == first || pair.Key == second ? 20u : 10u, pair.Value.Health);
    }
    [Fact]
    public void ElementalFire_KillsMajordomoThroughTheSpellSystem_AndStartsRagnarosIntroduction()
    {
        // Spell.dbc 5875 row 19773: effect 1 INSTAKILL, implicit target A 38, range index 6 (0-100 yd); ClassicDB (19773,1,12018).
        SpellInfo fire = SpellTestKit.Spell(19773, SpellTestKit.Effect(SpellEffectName.Instakill, 0, (SpellImplicitTarget)38))
            with { RangeIndex = 6, Range = new SpellRange(0, 100) };
        using var kit = new SpellTestKit(fire);
        kit.System.Units = new MapObjectResolver(); // ArcaneCore.World's WorldSpellUnitResolver: creatures resolve as explicit targets
        Map map = kit.World.GetMap(0); var raid = new MoltenCoreInstance(map); map.AddUpdater(raid);
        raid.SetData(MoltenCoreInstance.Majordomo, EncounterState.Done);
        var content = new CreatureContent([CreatureTestSupport.Template(12018), CreatureTestSupport.Template(11502)], [], [], [], []);
        var creatures = new CreatureMapSystem(map, content, random: new Random(1),
            aiServices: new CreatureAiServices { Spells = new SpellSystemCreatureCaster(kit.System) });
        map.AddUpdater(creatures);
        Creature domo = creatures.SpawnTemporary(content.FindTemplate(12018)!, 5, 0, 83.5f, 0);
        Creature rag = creatures.SpawnTemporary(content.FindTemplate(11502)!, 15, 0, 83.5f, 0);
        Assert.Equal(1080u, domo.FactionTemplate); // the defeated, friendly Majordomo of the summon event
        var ragAi = Assert.IsType<RagnarosAI>(rag.AI); ragAi.BeginIntroduction();
        domo.InvincibilityHpThreshold = 0; domo.UnitFlags &= ~UnitFlags.NonAttackable2; // MajordomoAI speech stage 17
        (Player bystander, _) = kit.AddPlayer(1, 10, 0);
        // The same call MajordomoAI's stage 17 makes: Ragnaros casts Elemental Fire at Majordomo through the real SpellSystem.
        Assert.Equal(CreatureCastResult.Ok, creatures.CastSpell(rag, 19773, domo, false));
        kit.Advance(1000);
        Assert.False(domo.IsAlive); Assert.True(bystander.IsAlive);
        Assert.NotEqual(0u, (uint)(rag.UnitFlags & UnitFlags.NonAttackable2));
        // RagnarosAI.OnSpellHitTarget(19773) armed the 10 s + 3 s introduction; nothing here calls it by hand.
        ragAi.OnUpdate(9999); Assert.NotEqual(0u, (uint)(rag.UnitFlags & UnitFlags.NonAttackable2));
        ragAi.OnUpdate(1); ragAi.OnUpdate(3000);
        Assert.Equal(0u, (uint)(rag.UnitFlags & UnitFlags.NonAttackable2));
        Assert.True(rag.AI!.AggroesOnSight);
    }
    [Fact]
    public void ScriptTargetNearCaster_PrefersTheExplicitListedCreature_OverANearerOne()
    {
        SpellInfo heal = SpellTestKit.Spell(19515, SpellTestKit.Effect(SpellEffectName.Heal, 10, (SpellImplicitTarget)38))
            with { RangeIndex = 3, Range = new SpellRange(0, 100) };
        using var kit = new SpellTestKit(heal);
        kit.System.Units = new MapObjectResolver();
        Map map = kit.World.GetMap(0); map.AddUpdater(new MoltenCoreInstance(map));
        var content = new CreatureContent([CreatureTestSupport.Template(12099), CreatureTestSupport.Template(12057)], [], [], [], []);
        var creatures = new CreatureMapSystem(map, content); map.AddUpdater(creatures);
        Creature caster = creatures.SpawnTemporary(content.FindTemplate(12099)!, 0, 0, 83.5f, 0);
        Creature near = creatures.SpawnTemporary(content.FindTemplate(12057)!, 5, 0, 83.5f, 0);
        Creature far = creatures.SpawnTemporary(content.FindTemplate(12057)!, 50, 0, 83.5f, 0);
        near.Health = far.Health = 10;
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, 19515, SpellCastTargets.ForUnit(far.Guid), triggered: true));
        Assert.Equal((20u, 10u), (far.Health, near.Health));
    }
    [Fact]
    public void RaidDatabaseDestination_UsesStoredPosition_AndRefusesMissingRows()
    {
        using WorldRuntime world = TestWorld.CreateRuntime(); Map map = world.GetMap(0);
        var content = new CreatureContent([CreatureTestSupport.Template(11502)], [], [], [], []);
        var creatures = new CreatureMapSystem(map, content); map.AddUpdater(creatures);
        Creature caster = creatures.SpawnTemporary(content.FindTemplate(11502)!, 5, 0, 83.5f, 0);
        SpellInfo spell = SpellTestKit.Spell(21110, SpellTestKit.Effect(SpellEffectName.SummonWild, 1, SpellImplicitTarget.LocationDatabase));
        var spells = new SpellSystem(new SpellStore([spell], [], [(21110, new SpellTargetPosition(0, 40, 50, 60, 0))]), () => 0);
        var targets = SpellCastTargets.ForSelf(); var check = new RaidDatabaseDestination();
        Assert.Equal(SpellCastResult.CastOk, check.Check(new(spells, caster, spell, targets, caster, true, true)));
        Assert.True(targets.HasDest); Assert.Equal((40f, 50f, 60f), targets.Dest);
        SpellInfo missing = spell with { Id = 21111 };
        Assert.Equal(SpellCastResult.NotHere, check.Check(new(spells, caster, missing, SpellCastTargets.ForSelf(), caster, true, true)));
    }
}

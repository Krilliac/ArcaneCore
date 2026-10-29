using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Naxxramas;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// The Naxxramas encounter spells the boss AIs drive, cast through a real <see cref="SpellSystem"/> with the encounter's spell scripts installed:
/// Gothik's anchor chain (vmangos boss_gothik.cpp EffectDummyCreature_spell_anchor), the evade clean-up casts (mangos-classic DespawnCryptGuards,
/// DespawnBossAdds, DespawnSummonsNoth) and the Tesla overload Shock on Thaddius (mangos-classic ShockThaddius, ThaddiusLightningVisual).
/// The spell rows are the 1.12.1 Spell.dbc shapes (effect, implicit targets) with the ClassicDB spell_script_target rows.
/// </summary>
public sealed class NaxxramasEncounterSpellTests
{
    private sealed class Raid : IDisposable
    {
        public WorldRuntime World { get; } = TestWorld.CreateRuntime();
        public Map Map { get; }
        public NaxxramasInstance Instance { get; }
        public CreatureMapSystem Creatures { get; }
        public SpellSystem Spells { get; }
        public Player Tank { get; }

        public Raid(SpellInfo[] spells, SpellStore.ScriptTarget[] scriptTargets, uint[] entries, Func<CreatureTemplate, CreatureTemplate>? shape = null)
        {
            // Map 533 is a raid (map_template map_type 2): SetInCombatWithZone only works in a dungeon.
            WorldMaps.Of(World).Load(new MapContent(
                [new MapTemplate(533, 0, MapType.Raid, 3456, 40, 7, 0, 3005.68f, -3447.77f, "Naxxramas", "instance_naxxramas")], [], [], [], []));
            Map = World.GetMap(533);
            Instance = Assert.IsType<NaxxramasInstance>(InstanceScriptRegistry.Default.Create(Map));
            Instance.Initialize();
            Map.AddUpdater(Instance);
            Spells = new SpellSystem(new SpellStore(spells, [], [], scriptTargets), () => 10000) { Units = new MapObjectResolver() };
            SpellScriptDispatcher.Install(Spells, new SpellScriptRegistry(
            [
                new GothikAnchorChainScript(), new NaxxramasDespawnSummonsScript(), new ThaddiusShockScript(), new ThaddiusLightningVisualScript(),
                new ThaddiusMagneticPullScript(),
            ]));
            Creatures = new CreatureMapSystem(Map, Content([.. entries.Distinct().Select(e => (shape ?? (t => t))(Template(e)))], []),
                new CreatureOptions { AggroRate = 0, RespawnPacifyMs = 0 }, random: new Random(1),
                aiServices: new CreatureAiServices { Spells = new SpellSystemCreatureCaster(Spells), Hostility = new AlwaysHostile() });
            Map.AddUpdater(Creatures);
            Tank = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(), 533);
            Tank.Relocate(0, 0, 270, 0, 0);
            World.AddPlayer(Tank);
            World.RunTick(0);
        }

        public Creature Spawn(uint entry, float x, float y, float z) => Creatures.SpawnTemporary(Creatures.Content.FindTemplate(entry)!, x, y, z, 0);

        public void Dispose() => World.Dispose();
    }

    private static SpellInfo Dummy(uint id, SpellImplicitTarget target)
        => SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.Dummy, 1, target)) with { RangeIndex = 6, Range = new SpellRange(0, 100) };

    // Spell.dbc 5875: the To Anchor spells are DUMMY at TARGET_UNIT_SCRIPT_NEAR_CASTER (38), Anchor to Skulls DUMMY at TARGET_UNIT (25), all
    // range index 6. ClassicDB spell_script_target: 27892 and 27893 -> creature 16137.
    private static SpellInfo[] GothikSpells() =>
    [
        Dummy(27892, SpellImplicitTarget.UnitScriptNearCaster), Dummy(27893, SpellImplicitTarget.UnitScriptNearCaster),
        Dummy(27915, SpellImplicitTarget.Unit),
    ];

    private static SpellStore.ScriptTarget[] GothikTargets() => [new(27892, 1, 16137, 0), new(27893, 1, 16137, 0)];

    private static Raid GothikFight(bool spectralSummonPoint)
    {
        var raid = new Raid(GothikSpells(), GothikTargets(), [16060, 16137, 16124, 16127]);
        var objects = new GameObjectMapSystem(raid.Map, new GameObjectContent(
            [GameObjectTestKit.GoTemplate(181170, GameObjectType.Door)], [], [], [], []));
        raid.Map.AddUpdater(objects);
        objects.Summon(181170, 0, 0, 270, 0); // the combat gate: Y <= 0 is the living side
        Creature gothik = raid.Spawn(16060, 1, 0, 270);
        // High anchors stand no more than 5 yd below Gothik; summon points lower.
        raid.Spawn(16137, 0, -20, 270); // living-side high anchor
        raid.Spawn(16137, 0, -30, 250); // living-side summon point (never used by the chain)
        raid.Spawn(16137, 0, 20, 270);  // spectral-side high anchor
        if (spectralSummonPoint) raid.Spawn(16137, 0, 30, 250); // spectral-side summon point
        gothik.AI!.AttackStart(raid.Tank);
        return raid;
    }

    [Fact]
    public void Gothik_LivingSideDeath_RaisesTheSpectralAddAtASpectralSummonPoint_ThroughTheAnchorChain()
    {
        using Raid raid = GothikFight(spectralSummonPoint: true);
        Creature trainee = raid.Spawn(16124, 0, -10, 270);
        raid.Map.Combat.Kill(raid.Tank, trainee);
        for (int i = 0; i < 5; i++) raid.World.RunTick(100);

        // The spectral trainee stands on the low spectral point (vmangos Anchor to Skulls), not on the nearest spectral anchor.
        Creature spectral = Assert.Single(raid.Creatures.Creatures, c => c.Entry == 16127);
        Assert.Equal((0f, 30f, 250f), (spectral.X, spectral.Y, spectral.Z));
        // The 15-second corpse trigger is gone after its lifetime.
        Assert.Contains(raid.Creatures.Creatures, c => c.Entry == 16137 && c.Y == -10);
        for (int i = 0; i < 16; i++) raid.World.RunTick(1000);
        Assert.DoesNotContain(raid.Creatures.Creatures, c => c.Entry == 16137 && c.Y == -10 && c.IsAlive);
    }

    [Fact]
    public void Gothik_AnchorChainStops_WhenTheSpectralSideHasNoSummonPoint()
    {
        using Raid raid = GothikFight(spectralSummonPoint: false);
        Creature trainee = raid.Spawn(16124, 0, -10, 270);
        raid.Map.Combat.Kill(raid.Tank, trainee);
        for (int i = 0; i < 5; i++) raid.World.RunTick(100);
        Assert.DoesNotContain(raid.Creatures.Creatures, c => c.Entry == 16127);
    }

    [Fact]
    public void Grobbulus_Evade_DespawnsItsFalloutSlimes_ButNotOtherCreatures()
    {
        // Spell.dbc 30134 Despawn Boss Adds: SCRIPT_EFFECT at 22/7; ClassicDB spell_script_target -> 16363, 16290.
        SpellInfo despawn = SpellTestKit.Spell(30134, SpellTestKit.Effect(SpellEffectName.ScriptEffect, 1, SpellImplicitTarget.LocationCasterSrc,
            targetB: SpellImplicitTarget.EnumUnitsScriptAoeAtSrcLoc) with { Radius = 100 }) with { RangeIndex = 6, Range = new SpellRange(0, 100) };
        using var raid = new Raid([despawn], [new(30134, 1, 16363, 0), new(30134, 1, 16290, 0)], [15931, 16290, 16363, 16124]);
        Creature grobbulus = raid.Spawn(15931, 1, 0, 270);
        Creature slime = raid.Spawn(16290, 5, 0, 270);
        Creature cloud = raid.Spawn(16363, 6, 0, 270);
        Creature bystander = raid.Spawn(16124, 4, 0, 270);
        grobbulus.AI!.AttackStart(raid.Tank);
        grobbulus.AI.OnEvade();
        raid.World.RunTick(0);
        // A temporary creature's forced despawn removes it from the map (CreatureMapSystem.ForcedDespawnNow).
        Assert.DoesNotContain(slime, raid.Creatures.Creatures);
        Assert.DoesNotContain(cloud, raid.Creatures.Creatures);
        Assert.Contains(bystander, raid.Creatures.Creatures);
    }

    [Fact]
    public void ThaddiusOverload_CoilShockRemovesTheSpawnStun_AndThaddiusEngages()
    {
        // Spell.dbc 28160 Thadius Spawn: APPLY_AURA MOD_STUN at the caster, permanent. 28159 Shock: DUMMY at 38 (ClassicDB script target 15928),
        // range anywhere. 28136 Thadius Lightning Visual: DUMMY at the caster.
        SpellInfo stun = SpellTestKit.Spell(28160, SpellTestKit.Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitCaster, AuraType.ModStun))
            with { Duration = new SpellDuration(-1, 0, -1) };
        SpellInfo shock = Dummy(28159, SpellImplicitTarget.UnitScriptNearCaster) with { RangeIndex = 13, Range = new SpellRange(0, 50000) };
        SpellInfo visual = SpellTestKit.Spell(28136, SpellTestKit.Effect(SpellEffectName.Dummy, 1));
        using var raid = new Raid([stun, shock, visual], [new(28159, 1, 15928, 0)], [15928, 15929, 15930, 16218]);
        Creature thaddius = raid.Spawn(15928, 1, 0, 270);
        raid.Spawn(16218, 10, 10, 275);
        Creature stalagg = raid.Spawn(15929, 2, 0, 270);
        Creature feugen = raid.Spawn(15930, 3, 0, 270);
        Assert.True(raid.Spells.HasAura(thaddius, 28160)); // Reset: Thadius Spawn while the adds stand

        raid.Map.Combat.Kill(raid.Tank, stalagg);
        raid.Map.Combat.Kill(raid.Tank, feugen);
        raid.Instance.Update(14000);
        raid.World.RunTick(0);

        Assert.False(raid.Spells.HasAura(thaddius, 28160));
        Assert.True(thaddius.Combat.IsInCombat);
    }
}

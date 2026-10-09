using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.BlackwingLair;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// The Blackwing Lair spells cast through the real <see cref="SpellSystem"/> (no recording caster), with the Spell.dbc 5875 implicit
/// targets of each spell: these are the casts whose targets only <see cref="BlackwingLairTargetModule"/> can resolve.
/// </summary>
public sealed class BlackwingLairTargetingTests
{
    private const float Z = 450;

    private sealed class MapUnits : ISpellUnitResolver
    {
        public Unit? Find(Unit reference, ObjectGuid guid)
            => guid.IsEmpty ? null : reference.Guid == guid ? reference : reference.Map?.FindObject(guid) as Unit;
    }

    private sealed class Bwl : IDisposable
    {
        public Bwl(SpellInfo[] spells, uint[] creatures, uint[] objects)
        {
            Kit = new SpellTestKit(spells);
            Kit.System.Units = new MapUnits();
            SpellScriptDispatcher.Install(Kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
            Map = Kit.World.GetMap(469);
            Raid = Assert.IsType<BlackwingLairInstance>(InstanceScriptRegistry.Default.Create(Map));
            Raid.Initialize();
            Map.AddUpdater(Raid);
            Creatures = new CreatureMapSystem(Map, Content([.. creatures.Select(e => Template(e, b => b.Faction = 14))], []),
                new CreatureOptions { AggroRate = 0, RespawnPacifyMs = 0 }, random: new Random(1),
                aiServices: new CreatureAiServices { Spells = new SpellSystemCreatureCaster(Kit.System) });
            Map.AddUpdater(Creatures);
            Objects = new GameObjectMapSystem(Map, new GameObjectContent(
                [.. objects.Select(e => GameObjectTestKit.GoTemplate(e, GameObjectType.Goober))], [], [], [], []));
            Map.AddUpdater(Objects);
        }

        public SpellTestKit Kit { get; }
        public Map Map { get; }
        public BlackwingLairInstance Raid { get; }
        public CreatureMapSystem Creatures { get; }
        public GameObjectMapSystem Objects { get; }

        public Creature Spawn(uint entry, float x, float y = 0) => Creatures.SpawnTemporary(Creatures.Content.FindTemplate(entry)!, x, y, Z, 0);

        public Player AddPlayer(uint guid, float x, Class cls = Class.Warrior)
        {
            Player player = TestWorld.CreatePlayer(guid, x, 0, new FakeSession(), 469);
            player.Relocate(x, 0, Z, 0, 0);
            player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)cls);
            Kit.World.AddPlayer(player);
            Kit.World.RunTick(0);
            return player;
        }

        public void Dispose() => Kit.Dispose();
    }

    // Spell.dbc 5875 19832 Possess: effect 0 APPLY_AURA MOD_POSSESS target A 38, range index 6 (100 yd).
    private static SpellInfo Possess() => SpellTestKit.Spell(19832,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 100, (SpellImplicitTarget)38, AuraType.ModPossess))
        with { RangeIndex = 6, Range = new SpellRange(0, 100), Duration = new SpellDuration(-1, 0, -1) };

    // Spell.dbc 5875 19873 Destroy Egg: effect 0 ACTIVATE_OBJECT target A 40, effect 1 DUMMY target A 46, range index 7 (10 yd).
    private static SpellInfo DestroyEgg() => SpellTestKit.Spell(19873,
            SpellTestKit.Effect(SpellEffectName.ActivateObject, 0, (SpellImplicitTarget)40),
            SpellTestKit.Effect(SpellEffectName.Dummy, 0, (SpellImplicitTarget)46))
        with { RangeIndex = 7, Range = new SpellRange(0, 10) };

    [Fact]
    public void Orb_PossessLandsOnRazorgore_ThroughTheProductionPlayerCast()
    {
        using var bwl = new Bwl([Possess()], [12435, 12557], [177808]);
        Creature razorgore = bwl.Spawn(12435, 20);
        GameObject orb = bwl.Objects.Summon(177808, 1, 0, Z, 0)!;
        Player player = bwl.AddPlayer(1, 0);
        // InstanceFeature.ScriptCastPlayerTargetSpell: the player casts the spell at the GUID, triggered.
        bwl.Raid.CastPlayerTargetSpell = (p, spell, target) =>
            bwl.Kit.System.CastSpell(p, spell, SpellCastTargets.ForUnit(target), triggered: true);
        bwl.Raid.SetData(0, EncounterState.InProgress);
        Creature grethok = bwl.Spawn(12557, 5);
        bwl.Map.Combat.Kill(player, grethok);
        Assert.True((orb.Flags & GameObjectFlags.NoInteract) == 0);

        Assert.True(bwl.Raid.OnGameObjectUse(player, orb));
        Assert.True(bwl.Kit.System.HasAura(razorgore, 19832));
        Assert.False(bwl.Kit.System.HasAura(player, 19832));
    }

    [Fact]
    public void DestroyEgg_PossessedRazorgoreBreaksTheNearestEggInRange_WithoutAnObjectTarget()
    {
        using var bwl = new Bwl([DestroyEgg()], [12435], [177807]);
        Creature razorgore = bwl.Spawn(12435, 0);
        GameObject near = bwl.Objects.Summon(177807, 4, 0, Z, 0)!;
        GameObject nearer = bwl.Objects.Summon(177807, 2, 0, Z, 0)!;
        GameObject far = bwl.Objects.Summon(177807, 40, 0, Z, 0)!;
        bwl.Raid.SetData(0, EncounterState.InProgress);

        // The pet-bar cast of a possessed unit names no object (target type 40 needs none).
        Assert.Equal(SpellCastResult.CastOk, bwl.Kit.System.CastSpell(razorgore, 19873, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(1, bwl.Raid.BrokenEggCount);
        Assert.Equal(GameObjectLootState.JustDeactivated, nearer.LootState);
        Assert.Equal(GameObjectLootState.Ready, near.LootState);

        Assert.Equal(SpellCastResult.CastOk, bwl.Kit.System.CastSpell(razorgore, 19873, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(2, bwl.Raid.BrokenEggCount);
        Assert.Equal(GameObjectLootState.JustDeactivated, near.LootState);

        // Only the egg 40 yd away is left: out of the 10 yd range, so the cast is refused and nothing is counted.
        Assert.Equal(SpellCastResult.BadTargets, bwl.Kit.System.CastSpell(razorgore, 19873, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(2, bwl.Raid.BrokenEggCount);
        Assert.Equal(GameObjectLootState.Ready, far.LootState);
        Assert.Equal(EncounterState.InProgress, bwl.Raid.GetData(0));
    }

    [Fact]
    public void NefariusCorruption_HitsVaelastraszOnly()
    {
        // Spell.dbc 5875 23642: APPLY_AURA (DUMMY) target A 22 / B 7, radius index 12 (100 yd), range index 13.
        SpellInfo corruption = SpellTestKit.Spell(23642,
                SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.LocationCasterSrc, AuraType.Dummy,
                    targetB: (SpellImplicitTarget)7) with { Radius = 100 })
            with { RangeIndex = 13, Range = new SpellRange(0, 50000), Duration = new SpellDuration(-1, 0, -1) };
        using var bwl = new Bwl([corruption], [10162, 13020], []);
        Creature nefarius = bwl.Spawn(10162, 0);
        Creature vael = bwl.Spawn(13020, 10);
        Player player = bwl.AddPlayer(1, 5);
        Assert.Equal(CreatureCastResult.Ok, bwl.Creatures.CastSpell(nefarius, 23642, vael, triggered: true));
        Assert.True(bwl.Kit.System.HasAura(vael, 23642));
        Assert.False(bwl.Kit.System.HasAura(player, 23642));
        Assert.False(bwl.Kit.System.HasAura(nefarius, 23642));
    }

    [Fact]
    public void ClassCall_AffectsOnlyTheCalledClass_FromOneCast()
    {
        // Spell.dbc 5875 23410 Wild Magic: APPLY_AURA target A 22 / B 15 (every enemy around the caster), radius index 12 (100 yd).
        SpellInfo wildMagic = SpellTestKit.Spell(23410,
                SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.LocationCasterSrc, AuraType.Dummy,
                    targetB: SpellImplicitTarget.EnumUnitsEnemyAoeAtSrcLoc) with { Radius = 100 })
            with { Duration = new SpellDuration(-1, 0, -1) };
        using var bwl = new Bwl([wildMagic], [11583], []);
        Creature nefarian = bwl.Spawn(11583, 0);
        Player mage = bwl.AddPlayer(1, 5, Class.Mage);
        Player otherMage = bwl.AddPlayer(2, 8, Class.Mage);
        Player warrior = bwl.AddPlayer(3, 6, Class.Warrior);
        Assert.Equal(CreatureCastResult.Ok, bwl.Creatures.CastSpell(nefarian, 23410, null, triggered: true));
        Assert.True(bwl.Kit.System.HasAura(mage, 23410));
        Assert.True(bwl.Kit.System.HasAura(otherMage, 23410));
        Assert.False(bwl.Kit.System.HasAura(warrior, 23410));
    }

    [Fact]
    public void RaiseDrakonids_HasAResolvedTarget_AndTheInstanceRaisesTheBones()
    {
        // Spell.dbc 5875 23362: ACTIVATE_OBJECT target A 22 / B 51 (script game objects 179804 around the caster), radius index 12.
        SpellInfo raise = SpellTestKit.Spell(23362,
            SpellTestKit.Effect(SpellEffectName.ActivateObject, 0, SpellImplicitTarget.LocationCasterSrc,
                targetB: (SpellImplicitTarget)51) with { Radius = 100 });
        var log = new CapturingLogger();
        using var bwl = new Bwl([raise], [11583, 14265, 14605], [179804]);
        var spells = new SpellSystem(bwl.Kit.Store, () => 0, units: new MapUnits(), logger: log);
        Creature nefarian = bwl.Spawn(11583, 0);
        Player player = bwl.AddPlayer(1, 3);

        // Drakonids leave Drakonid Bones where they die (ClassicDB EventAI: 23363 Summon Drakonid Corpse Trigger).
        Creature drakonid = bwl.Spawn(14265, 12, 4);
        bwl.Map.Combat.Kill(player, drakonid);
        GameObject bones = Assert.Single(bwl.Objects.GameObjects, go => go.Entry == 179804);
        Assert.Equal((12f, 4f), (bones.X, bones.Y));

        Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(nefarian, 23362, SpellCastTargets.ForSelf(), triggered: true));
        Assert.DoesNotContain(log.Lines, line => line.Contains("implicit target", StringComparison.Ordinal));
        Assert.Equal(1, bwl.Raid.RaiseBones(nefarian));
        Assert.Single(bwl.Creatures.Creatures, c => c.Entry == 14605);
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }
}

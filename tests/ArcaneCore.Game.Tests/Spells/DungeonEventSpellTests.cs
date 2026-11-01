using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Generic;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.TestSupport;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// The dungeon and dungeon-area quest-event spells of <c>Spells/Generic/DungeonEventSpellScripts.cs</c>, <c>DeliberatelyInertSpells.Dungeons.cs</c> and
/// <c>PlayerbotQaSpellConsumers.Dungeons.cs</c>: the Banish Exile charms, Unsummon Mechanical Yeti, and the architecture rule that every id of the lane
/// is a script, a deliberately inert spell or a named consumer, and never two of them.
/// </summary>
public sealed class DungeonEventSpellTests
{
    private const uint BanishBurning = 4130, BanishCresting = 4131, BanishThundering = 4132, UnsummonYeti = 17163;

    /// <summary>Every id of the lane except 6277 (Bind Sight (PT)), which only TrinityCore and AzerothCore evidence supports: an owner decision.</summary>
    private static readonly uint[] s_laneIds =
    [
        10254, 10347, 4130, 4131, 4132, 14928, 15064, 16051, 12639, 12948, 8894, 8603, 8655, 9144, 9096, 9173, 9156, 9157, 9200, 9457, 8995, 8997,
        11365, 11893, 12198, 12938, 13236, 13540, 13619, 13280, 14247, 14291, 18476, 18113, 18362, 18793, 19032, 19033, 19473, 20528, 21052, 24173,
        24593, 25813, 3730, 794, 6924, 8345, 18969, 15713, 15782, 17163,
    ];

    private static SpellInfo Instant(SpellInfo spell) => spell with { StartRecoveryCategory = 0, StartRecoveryTime = 0, SpellVisual = 1 };

    private static SpellInfo Charm(uint id) => Instant(Spell(id, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitEnemy)));

    private static SpellInfo Marker(uint id) => Instant(Spell(id, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
    {
        Duration = new SpellDuration(30_000, 0, 30_000),
    });

    private sealed class MapUnits : ISpellUnitResolver
    {
        public Unit? Find(Unit reference, ObjectGuid guid)
            => reference.Guid == guid ? reference : reference.Map?.FindObject(guid) as Unit;
    }

    private sealed class Scene : IDisposable
    {
        public Scene(params SpellInfo[] spells)
        {
            Kit = new SpellTestKit(spells);
            Kit.System.Units = new MapUnits();
            Kit.System.CombatRules = SpellCombatRules.Neutral;
            SpellScriptDispatcher.Install(Kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
            (Caster, _) = Kit.AddPlayer(1);
        }

        public SpellTestKit Kit { get; }

        public Player Caster { get; }

        public Creature AddCreature(uint entry, float x)
        {
            var template = new CreatureTemplate
            {
                Entry = entry, Name = $"Creature {entry}", MinLevel = 20, MaxLevel = 20,
                MinLevelHealth = 100, MaxLevelHealth = 100, DisplayIds = [1], Faction = 14,
            };
            var creature = new Creature(entry, template, null, new CreatureContent([template], [], [], [], []), new Random(1));
            Map map = Kit.World.GetMap(0);
            creature.Relocate(x, 0, Caster.Z, 0, 0);
            creature.MapId = 0;
            map.AddObject(creature);
            map.Combat.Track(creature);
            Kit.World.RunTick(0);
            creature.Level = 20;
            creature.MaxHealth = creature.Health = 100;
            return creature;
        }

        /// <summary>
        /// Installs a real <see cref="CreatureMapSystem"/> on map 0 that spawns one creature per (entry, x) pair, so the creatures get their
        /// <c>Creature.System</c> (StopMoving and forced despawn act only through it) and the map clock runs their delayed despawns.
        /// </summary>
        public CreatureMapSystem AddCreatureSystem(params (uint Entry, float X)[] spawns)
        {
            Map map = Kit.World.GetMap(0);
            CreatureTemplate[] templates = [.. spawns.Select(s => s.Entry).Distinct().Select(entry => CreatureTestKit.Template(entry, b =>
            {
                b.Faction = 14;
                b.MinLevelHealth = 100;
                b.MaxLevelHealth = 100;
            }))];
            CreatureSpawn[] rows = [.. spawns.Select((s, i) => CreatureTestKit.Spawn((uint)(i + 1), s.Entry, s.X, 0, Caster.Z))];
            var system = new CreatureMapSystem(map, CreatureTestKit.Content(templates, rows), random: new Random(1));
            map.AddUpdater(system);
            Kit.World.RunTick(0);
            return system;
        }

        public void Tick(int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                Kit.World.RunTick(100);
            }
        }

        public SpellCastResult Cast(uint spell, Unit target)
            => Kit.System.CastSpell(Caster, spell, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        public void Dispose() => Kit.Dispose();
    }

    [Theory]
    [InlineData(BanishBurning, 2760u)]
    [InlineData(BanishCresting, 2761u)]
    [InlineData(BanishThundering, 2762u)]
    [InlineData(UnsummonYeti, 0u)]
    public void BanishExile_NamesTheExileOfEachCharm(uint spell, uint expected)
        => Assert.Equal(expected, BanishExileScript.ExileFor(spell));

    [Fact]
    public void BanishExile_OnTheCharmsOwnExile_MakesTheExileCastQuietSuicideOnItself()
    {
        using var s = new Scene(Charm(BanishCresting), Marker(BanishExileScript.QuietSuicide));
        Creature exile = s.AddCreature(2761, 3);

        Assert.Equal(SpellCastResult.CastOk, s.Cast(BanishCresting, exile));
        Assert.True(s.Kit.System.HasAura(exile, BanishExileScript.QuietSuicide));
    }

    [Fact]
    public void BanishExile_OnAnotherExile_FailsBadTargets_AndNothingIsCast()
    {
        using var s = new Scene(Charm(BanishBurning), Marker(BanishExileScript.QuietSuicide));
        Creature thundering = s.AddCreature(2762, 3);

        Assert.Equal(SpellCastResult.BadTargets, s.Cast(BanishBurning, thundering));
        Assert.False(s.Kit.System.HasAura(thundering, BanishExileScript.QuietSuicide));
    }

    private static SpellInfo AnyUnitDummy(uint id) => Instant(Spell(id, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.Unit)));

    [Fact]
    public void UnsummonMechanicalYeti_StopsUmisYeti_AndDespawnsItAfterOneSecond()
    {
        using var s = new Scene(AnyUnitDummy(UnsummonYeti));
        CreatureMapSystem creatures = s.AddCreatureSystem((UnsummonMechanicalYetiScript.UmisMechanicalYeti, 3f));
        Creature yeti = Assert.Single(creatures.Creatures);
        Assert.Same(creatures, yeti.System);
        creatures.MoveTo(yeti, 40f, 0f, yeti.Z, false, null);
        Assert.True(yeti.IsMoving);

        Assert.Equal(SpellCastResult.CastOk, s.Cast(UnsummonYeti, yeti));

        Assert.False(yeti.IsMoving);
        s.Tick(9); // 900 ms: the despawn is delayed by one second
        Assert.True(yeti.IsAlive);
        s.Tick(2);
        Assert.False(yeti.IsAlive);
    }

    [Fact]
    public void UnsummonMechanicalYeti_LeavesAnotherCreatureAlone_AndMoving()
    {
        using var s = new Scene(AnyUnitDummy(UnsummonYeti));
        CreatureMapSystem creatures = s.AddCreatureSystem((UnsummonMechanicalYetiScript.UmisMechanicalYeti, 3f), (12257u, 4f));
        Creature other = Assert.Single(creatures.Creatures, c => c.Entry == 12257);
        creatures.MoveTo(other, 40f, 0f, other.Z, false, null);
        Assert.True(other.IsMoving);

        Assert.Equal(SpellCastResult.CastOk, s.Cast(UnsummonYeti, other));

        Assert.True(other.IsMoving);
        s.Tick(11);
        Assert.True(other.IsAlive);
        Assert.Same(other, creatures.FindCreature(other.Guid));
    }

    [Fact]
    public void EveryLaneSpell_IsAScriptOrInertOrAConsumer_NeverTwoOfThose()
    {
        SpellScriptRegistry registry = SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly);
        string consumers = File.ReadAllText(Path.Combine(RepositorySource.RequireRoot(), "src", "ArcaneCore.Playerbots", "Qa", "PlayerbotQaSpellConsumers.Dungeons.cs"));

        foreach (uint id in s_laneIds)
        {
            bool scripted = registry.Find(id) is not null;
            bool inert = DeliberatelyInertSpells.TryGet(id, out InertSpell? entry);
            bool consumed = consumers.Contains($"new({id},", StringComparison.Ordinal) || consumers.Contains($"({id}, \"", StringComparison.Ordinal);
            int kinds = (scripted ? 1 : 0) + (inert ? 1 : 0) + (consumed ? 1 : 0);
            Assert.True(kinds == 1, $"spell {id}: scripted={scripted} inert={inert} consumed={consumed}; exactly one expected");
            if (entry is not null)
            {
                Assert.False(string.IsNullOrWhiteSpace(entry.Citation), $"spell {id}: no citation");
            }
        }

        foreach (uint id in new uint[] { BanishBurning, BanishCresting, BanishThundering })
        {
            Assert.IsType<BanishExileScript>(registry.Find(id));
        }
    }

    [Fact]
    public void BindSightPt_StaysUnresolved_ForTheOwnerDecision()
    {
        Assert.Null(SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly).Find(6277));
        Assert.False(DeliberatelyInertSpells.TryGet(6277, out _));
    }
}

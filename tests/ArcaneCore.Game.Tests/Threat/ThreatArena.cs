using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Combat.Threat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Threat;

/// <summary>
/// A real spell system, map combat (with the production damage sink and threat binding) and creatures in one map, for the threat tests:
/// three players (a tank, a damage dealer and a healer) and creatures spawned on demand.
/// </summary>
internal sealed class ThreatArena : IDisposable
{
    private readonly List<Creature> _creatures = [];

    public ThreatArena(params SpellInfo[] spells)
    {
        Kit = new SpellTestKit(spells);
        (Tank, TankSession) = Kit.AddPlayer(1, 0, 0);
        (Dps, _) = Kit.AddPlayer(2, 0, 1);
        (Healer, _) = Kit.AddPlayer(3, 0, 2);
        Map.Combat.ThreatModifiers = new SpellThreatModifiers(Kit.System);
        Map.Combat.SpellThreatCatalog = Catalog;
        Kit.System.Damage = new MapCombatDamageSink();
        Kit.System.Units = new CreatureResolver(_creatures);
        foreach (Player player in new[] { Tank, Dps, Healer })
        {
            player.MaxHealth = 1000;
            player.Health = 1000;
        }

        Wolf = SpawnCreature(77, 3);
    }

    public SpellTestKit Kit { get; }

    public Player Tank { get; }

    public FakeSession TankSession { get; }

    public Player Dps { get; }

    public Player Healer { get; }

    public Creature Wolf { get; }

    public TestThreatCatalog Catalog { get; } = new();

    public Map Map => Kit.World.GetMap(0);

    public IReadOnlyList<Creature> Creatures => _creatures;

    /// <summary>
    /// Spawn another creature (a very healthy wolf that stays where it is); <paramref name="tweak"/> adjusts its template and
    /// <paramref name="options"/> the creature system. Its AI casts through the spell system, so it can be evaded and reset.
    /// </summary>
    public Creature SpawnCreature(uint guid, float x, Func<CreatureTemplate, CreatureTemplate>? tweak = null, CreatureOptions? options = null)
    {
        CreatureTemplate template = CreatureTestSupport.Template(CreatureTestSupport.WolfEntry, t =>
        {
            t.MinLevelHealth = 100000;
            t.MaxLevelHealth = 100000;
        });
        if (tweak is not null)
        {
            template = tweak(template);
        }

        var system = new CreatureMapSystem(Map, CreatureTestSupport.Content([template], [CreatureTestSupport.Spawn(guid, CreatureTestSupport.WolfEntry, x, 0)]),
            options, random: new Random(1), aiServices: new CreatureAiServices { Hostility = new AlwaysHostile(), Spells = new SpellSystemCreatureCaster(Kit.System) });
        Map.AddUpdater(system);
        Kit.World.RunTick(50);
        Creature creature = Assert.Single(system.Creatures);
        creature.AI!.CombatMovement = false;
        _creatures.Add(creature);
        Systems.Add(system);
        return creature;
    }

    /// <summary>The creature system of every spawned creature, in spawn order.</summary>
    public List<CreatureMapSystem> Systems { get; } = [];

    /// <summary>Cast <paramref name="spell"/> from <paramref name="caster"/> at <paramref name="target"/>.</summary>
    public SpellCastResult Cast(Unit caster, Unit target, uint spell, bool triggered = true)
    {
        if (caster is Player player)
        {
            Kit.Spellbook.Teach(player, spell);
        }

        return Kit.System.CastSpell(caster, spell, SpellCastTargets.ForUnit(target.Guid), triggered);
    }

    public SpellInfo Info(uint id) => Kit.Store.Get(id)!;

    public void Dispose() => Kit.Dispose();

    private sealed class CreatureResolver(List<Creature> creatures) : ISpellUnitResolver
    {
        private readonly MapPlayerResolver _players = new();

        public Unit? Find(Unit reference, ObjectGuid guid)
            => creatures.FirstOrDefault(c => c.Guid == guid) ?? _players.Find(reference, guid);
    }
}

/// <summary>An in-memory spell_threat table.</summary>
internal sealed class TestThreatCatalog : ISpellThreatCatalog
{
    private readonly Dictionary<uint, SpellThreatEntry> _entries = [];

    public SpellThreatEntry? Find(uint spellId) => _entries.GetValueOrDefault(spellId);

    public void Set(SpellThreatEntry entry) => _entries[entry.SpellId] = entry;
}

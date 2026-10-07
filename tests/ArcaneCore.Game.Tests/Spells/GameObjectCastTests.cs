using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// Spells cast by game objects through the real <see cref="SpellSystem"/> (<see cref="SpellSystem.CastForGameObject"/>): an environmental trap
/// hurting a player (vmangos GameObject::Update, GameObject.cpp:532-537, the object as caster) and a cast made for the object's owner.
/// </summary>
public sealed class GameObjectCastTests : IDisposable
{
    private const uint FireSpell = 951001;
    private const uint FireNova = 951002;
    private const uint NovaTrap = 951012;
    private const uint WildTrap = 951010;
    private const uint FactionTrap = 951011;

    private readonly WorldRuntime _world;
    private readonly Map _map;
    private readonly GameObjectMapSystem _objects;
    private readonly SpellSystem _spells;
    private readonly List<(Unit Caster, Unit Target)> _hits = [];
    private uint _now = 10_000;

    public GameObjectCastTests()
    {
        _world = TestWorld.CreateRuntime();
        _map = _world.GetMap(0);
        var content = new GameObjectContent(
        [
            Trap(WildTrap, faction: 0),
            Trap(FactionTrap, faction: 35),
            Trap(NovaTrap, faction: 0, spell: FireNova),
        ], [GoSpawn(1, WildTrap, 3, 0), GoSpawn(2, FactionTrap, 60, 0), GoSpawn(3, NovaTrap, 3, 120)], [], [], []);
        _objects = new GameObjectMapSystem(_map, content);
        _map.AddUpdater(_objects);
        var store = new SpellStore(
        [
            Spell(FireSpell, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with
            {
                RangeIndex = SpellConstants.RangeIndexSelfOnly,
            },
            // An area around the casting object: TARGET_LOCATION_CASTER_SRC + enemies at the source (Spell.cpp:2549-2555, 7968-7971).
            Spell(FireNova, Effect(SpellEffectName.SchoolDamage, 7, SpellImplicitTarget.LocationCasterSrc,
                targetB: SpellImplicitTarget.EnumUnitsEnemyAoeAtSrcLoc) with { Radius = 6 }) with
            {
                RangeIndex = SpellConstants.RangeIndexSelfOnly,
            },
        ], [], []);
        _spells = new SpellSystem(store, () => _now, random: new Random(1)) { MapUpdateIntervalMs = 0, Relations = new FakeRelations() };
        _spells.SpellHit += (caster, target, spell) =>
        {
            if (spell.Id is FireSpell or FireNova)
            {
                _hits.Add((caster, target));
            }
        };
        _objects.Spells = new Adapter(_spells);
    }

    public void Dispose() => _world.Dispose();

    private sealed class Adapter(SpellSystem spells) : IGameObjectSpells
    {
        public bool Cast(GameObject source, uint spellId, Unit target, Unit? unitCaster)
            => spells.CastForGameObject(source, spellId, target, unitCaster) == SpellCastResult.CastOk;

        public float? MaxRange(uint spellId) => spells.Store.Get(spellId)?.Range.Max;

        public bool IsChanneling(Unit unit) => spells.IsChanneling(unit);

        public void StartRitualAnimation(Player helper, uint animSpellId, GameObject ritual)
        {
        }

        public bool CastRitualSpell(GameObject ritual, uint spellId, Unit caster, ObjectGuid summonTarget) => false;

        public void StartCreatingSpellCooldown(Player owner, uint spellId) => spells.StartCreatingSpellCooldown(owner, spellId);
    }

    private static GameObjectTemplate Trap(uint entry, uint faction, uint spell = FireSpell)
    {
        var data = new uint[GameObjectTemplate.DataCount];
        data[2] = 4;          // radius
        data[3] = spell;      // spell
        data[5] = 5;          // cooldown
        return new GameObjectTemplate { Entry = entry, Type = (uint)GameObjectType.Trap, DisplayId = 1, Name = $"Trap {entry}", Faction = faction, Data = data };
    }

    private Player Join(uint guid, float x, float y)
    {
        Player player = TestWorld.CreatePlayer(guid, x, y, new FakeSession((int)guid));
        _world.AddPlayer(player);
        return player;
    }

    private void Tick(uint ms)
    {
        _now += ms;
        _spells.Update(ms);
        _world.RunTick(ms);
    }

    [Fact]
    public void AWildEnvironmentalTrap_HurtsThePlayerWhoStepsIn_AsTheCaster()
    {
        Player player = Join(1, 3, 0);
        uint before = player.Health;

        Tick(50);
        Tick(50);

        (Unit caster, Unit target) = Assert.Single(_hits);
        Assert.Same(player, target);
        Assert.Same(player, caster); // the stand-in: the target casts for the object
        Assert.Equal(before - 10, player.Health);
        Assert.False(player.Combat.IsInCombat); // a trap does not put a player in combat (Spell.cpp:1650)

        Tick(1000);
        Assert.Single(_hits); // the 5 s cooldown
    }

    [Fact]
    public void AnEnvironmentalTrap_PicksPlayersWithoutAskingItsFaction()
    {
        // AnyPlayerInObjectRangeCheck (GridNotifiers.h:1228-1241) has no hostility test: a trap with a faction fires at the player too.
        Player player = Join(1, 60, 0);
        Tick(50);
        Tick(50);
        Assert.Single(_hits);
        Assert.Equal(player.MaxHealth - 10, player.Health);
    }

    [Fact]
    public void AnAreaAroundTheObject_TakesEveryoneTheObjectIsHostileTo_NotWhomTheStandInWouldBe()
    {
        // Players are not hostile to each other here (FakeRelations): only the object's hostility (faction 0: everyone,
        // GameObject::IsHostileTo) puts the second player in the area.
        Player first = Join(1, 3, 119);
        Player second = Join(2, 4, 121);
        Tick(50);
        Tick(50);

        Assert.Equal([first, second], _hits.Select(h => h.Target).OrderBy(u => u.Guid.Value).ToArray());
        Assert.Equal(first.MaxHealth - 7, first.Health);
        Assert.Equal(second.MaxHealth - 7, second.Health);
    }

    [Fact]
    public void TheRelationsAreRestoredAfterTheObjectCast()
    {
        ISpellTargetRelations relations = _spells.Relations;
        Join(1, 3, 0);
        Tick(50);
        Tick(50);
        Assert.Single(_hits);
        Assert.Same(relations, _spells.Relations);
    }

    [Fact]
    public void AnOwnedObject_CastsAsItsOwner()
    {
        Player owner = Join(1, 30, 30);
        Player victim = Join(2, 33, 30);
        ((FakeRelations)_spells.Relations).Hostile.Add(victim.Guid);
        GameObject portal = _objects.Summon(WildTrap, 33, 30, 83.5f, 0)!;

        Assert.Equal(SpellCastResult.CastOk, _spells.CastForGameObject(portal, FireSpell, victim, owner));
        (Unit caster, Unit target) = Assert.Single(_hits);
        Assert.Same(owner, caster);
        Assert.Same(victim, target);
    }
}

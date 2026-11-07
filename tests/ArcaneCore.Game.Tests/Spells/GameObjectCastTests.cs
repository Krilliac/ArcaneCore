using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
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
    private const uint QuietSpell = 951003;
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
            // A spell with no visual, speed or channel: the client hears of it only when it is not triggered (Spell::IsNeedSendToClient).
            Spell(QuietSpell, Effect(SpellEffectName.SchoolDamage, 5, SpellImplicitTarget.UnitEnemy)) with
            {
                SpellVisual = 0,
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

        public bool CastUse(GameObject source, uint spellId, Unit target)
            => spells.CastForGameObject(source, spellId, target, unitCaster: null, objectUse: true) == SpellCastResult.CastOk;

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

    private static int QuietSpellGoCount(FakeSession session) => session.Sent
        .Where(p => p.Opcode == WorldOpcode.SmsgSpellGo)
        .Count(p =>
        {
            var reader = new PacketReader(p.Payload);
            reader.ReadPackedGuid(); // the cast item or the caster
            reader.ReadPackedGuid(); // the caster
            return reader.ReadUInt32() == QuietSpell;
        });

    [Fact]
    public void AGooberOrSpellCasterUse_IsShownToTheClient_EvenWithoutASpellVisual()
    {
        // vmangos GameObject::Use starts with triggered = false (GameObject.cpp:1403); a goober's and a spell caster's spell reach
        // new Spell(..., triggered, ...) as such (:2001-2005), so Spell::IsNeedSendToClient is true and SMSG_SPELL_GO is sent.
        var session = new FakeSession(1);
        Player player = TestWorld.CreatePlayer(1, 30, 30, session);
        _world.AddPlayer(player);
        _world.RunTick(0);
        GameObject goober = _objects.Summon(WildTrap, 33, 30, 83.5f, 0)!;
        session.Clear();

        Assert.True(new Adapter(_spells).CastUse(goober, QuietSpell, player));

        Assert.Equal(1, QuietSpellGoCount(session));
        Assert.Equal(player.MaxHealth - 5, player.Health); // the cast itself ran as an object cast
    }

    [Fact]
    public void ATrapSpellWithoutAVisual_StaysHidden_ItIsTriggered()
    {
        // An object's trap spell is CastSpell(..., true) (GameObject.cpp:535-537, 1495-1497): triggered, so with no visual no SMSG_SPELL_GO.
        var session = new FakeSession(1);
        Player player = TestWorld.CreatePlayer(1, 30, 30, session);
        _world.AddPlayer(player);
        _world.RunTick(0);
        GameObject trap = _objects.Summon(WildTrap, 33, 30, 83.5f, 0)!;
        session.Clear();

        Assert.True(new Adapter(_spells).Cast(trap, QuietSpell, player, null));

        Assert.Equal(0, QuietSpellGoCount(session));
        Assert.Equal(player.MaxHealth - 5, player.Health);
    }
}

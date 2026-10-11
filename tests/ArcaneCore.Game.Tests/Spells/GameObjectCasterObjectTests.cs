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
/// <see cref="SpellCast.CasterGameObject"/> (vmangos <c>Spell::m_casterGo</c>): the object of a cast <see cref="SpellSystem.CastForGameObject"/>
/// makes for an object casting by itself. Only that one cast carries it: a cast its effects start, a unit acting for the object (a trap's owner)
/// and an ordinary unit cast carry none.
/// </summary>
public sealed class GameObjectCasterObjectTests : IDisposable
{
    private const uint ObjectSpell = 952001;
    private const uint InnerSpell = 952002;
    private const uint Goober = 952010;

    private readonly WorldRuntime _world;
    private readonly GameObjectMapSystem _objects;
    private readonly SpellSystem _spells;
    private readonly List<(uint Spell, GameObject? Object)> _seen = [];
    private bool _castInner;

    public GameObjectCasterObjectTests()
    {
        _world = TestWorld.CreateRuntime();
        Map map = _world.GetMap(0);
        _objects = new GameObjectMapSystem(map, new GameObjectContent([GoTemplate(Goober, GameObjectType.Goober)], [GoSpawn(1, Goober, 2, 0)], [], [], []));
        map.AddUpdater(_objects);
        var store = new SpellStore(
        [
            Spell(ObjectSpell, Effect(SpellEffectName.ScriptEffect, 0, SpellImplicitTarget.Unit)),
            Spell(InnerSpell, Effect(SpellEffectName.ScriptEffect, 0, SpellImplicitTarget.UnitCaster)),
        ], [], []);
        _spells = new SpellSystem(store, () => 10_000, random: new Random(1)) { MapUpdateIntervalMs = 0, Relations = new FakeRelations() };
        ScriptEffectRegistry registry = ScriptEffectRegistry.For(_spells);
        registry.Add(ObjectSpell, context =>
        {
            _seen.Add((ObjectSpell, context.CasterGameObject));
            if (_castInner)
            {
                // A cast the object's spell starts from its effect, by the stand-in (the unit the object cast at).
                context.System.CastSpell(context.Caster, InnerSpell, SpellCastTargets.ForSelf(), triggered: true);
            }
        });
        registry.Add(InnerSpell, context => _seen.Add((InnerSpell, context.CasterGameObject)));
    }

    public void Dispose() => _world.Dispose();

    private Player Join(uint guid)
    {
        Player player = TestWorld.CreatePlayer(guid, 2, 0, new FakeSession((int)guid));
        _world.AddPlayer(player);
        return player;
    }

    private GameObject Object => _objects.GameObjects.Single();

    [Fact]
    public void TheObjectsOwnCast_CarriesTheObject()
    {
        Player player = Join(1);

        Assert.Equal(SpellCastResult.CastOk, _spells.CastForGameObject(Object, ObjectSpell, player, unitCaster: null));

        (uint spell, GameObject? caster) = Assert.Single(_seen);
        Assert.Equal(ObjectSpell, spell);
        Assert.Same(Object, caster);
    }

    [Fact]
    public void ACastStartedInsideTheObjectsEffect_CarriesNoObject()
    {
        Player player = Join(1);
        _castInner = true;

        Assert.Equal(SpellCastResult.CastOk, _spells.CastForGameObject(Object, ObjectSpell, player, unitCaster: null));

        Assert.Equal(2, _seen.Count);
        Assert.Equal((ObjectSpell, (GameObject?)Object), _seen[0]);
        Assert.Equal((InnerSpell, (GameObject?)null), _seen[1]);
    }

    [Fact]
    public void AUnitActingForTheObject_CarriesNoObject()
    {
        // A trap's owner casts for its trap (vmangos owner->CastSpell(..., goGuid)): the caster is the unit, m_casterGo stays null.
        Player owner = Join(1);
        Player target = Join(2);

        Assert.Equal(SpellCastResult.CastOk, _spells.CastForGameObject(Object, ObjectSpell, target, unitCaster: owner));

        Assert.Equal((ObjectSpell, (GameObject?)null), Assert.Single(_seen));
    }

    [Fact]
    public void AnOrdinaryUnitCast_AfterAnObjectCast_CarriesNoObject()
    {
        Player player = Join(1);
        Assert.Equal(SpellCastResult.CastOk, _spells.CastForGameObject(Object, ObjectSpell, player, unitCaster: null));

        Assert.Equal(SpellCastResult.CastOk, _spells.CastSpell(player, ObjectSpell, SpellCastTargets.ForUnit(player.Guid), triggered: true));

        Assert.Equal(2, _seen.Count);
        Assert.Equal((ObjectSpell, (GameObject?)null), _seen[1]);
    }
}

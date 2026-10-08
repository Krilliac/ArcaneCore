using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Casters;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Tests.Reputation;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// A spell a game object casts by itself (no owner) is stood in for by its target (<see cref="SpellSystem.CastForGameObject"/>). vmangos casts it
/// from the object, a SpellCaster without auras, spell mods or crit (SpellCaster::IsSpellCrit returns false, SpellCaster.h:320; the done bonus
/// reads only unit auras, SpellCaster.cpp:1457-1700), at the object's level (GameObject::GetLevel, GameObject.cpp:2492-2510), and judges
/// others with GameObject::IsHostileTo / IsFriendlyTo (GameObject.cpp:2076-2166). The stand-in's own caster side must not leak into the cast.
/// </summary>
public sealed class GameObjectStandInTests : IDisposable
{
    private const uint Bolt = 952001;
    private const uint LevelBolt = 952002;
    private const uint Mend = 952003;
    private const uint Nova = 952004;
    private const uint Blessing = 952005;
    private const uint DamageUp = 952010;
    private const uint HealingUp = 952011;

    private const uint WildTrap = 952100;         // faction 0, trap level 30
    private const uint HostileObject = 952101;    // faction template 3: hostile to the player template
    private const uint NeutralObject = 952102;    // faction template 2: neither hostile nor friendly to the player template
    private const uint StormwindObject = 952103;  // faction template 4: Stormwind (a reputation faction), friendly by template
    private const uint WildGoober = 952104;       // faction 0, no level column

    private readonly WorldRuntime _world;
    private readonly Map _map;
    private readonly GameObjectMapSystem _objects;
    private readonly SpellSystem _spells;
    private readonly ReputationService _reputation = new(ReputationFixtures.Factions, roll: () => 0.0);
    private uint _now = 10_000;

    public GameObjectStandInTests()
    {
        _world = TestWorld.CreateRuntime();
        _map = _world.GetMap(0);
        var content = new GameObjectContent(
        [
            Object(WildTrap, GameObjectType.Trap, faction: 0, (1, 30)),
            Object(HostileObject, GameObjectType.Trap, faction: 3),
            Object(NeutralObject, GameObjectType.Trap, faction: 2),
            Object(StormwindObject, GameObjectType.Trap, faction: 4),
            Object(WildGoober, GameObjectType.Goober, faction: 0),
        ], [], [], [], []);
        _objects = new GameObjectMapSystem(_map, content);
        _map.AddUpdater(_objects);
        var store = new SpellStore(
        [
            Damage(Bolt, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)),
            Damage(LevelBolt, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy) with { RealPointsPerLevel = 1 }),
            Damage(Mend, Effect(SpellEffectName.Heal, 20, SpellImplicitTarget.Unit)) with { School = SpellSchool.Holy },
            Damage(Nova, Effect(SpellEffectName.SchoolDamage, 7, SpellImplicitTarget.LocationCasterSrc, targetB: SpellImplicitTarget.EnumUnitsEnemyAoeAtSrcLoc) with { Radius = 6 }),
            Damage(Blessing, Effect(SpellEffectName.Heal, 5, SpellImplicitTarget.LocationCasterSrc, targetB: SpellImplicitTarget.EnumUnitsFriendAoeAtSrcLoc) with { Radius = 6 }) with { School = SpellSchool.Holy },
            Aura(DamageUp, AuraType.ModDamageDone, 100),
            Aura(HealingUp, AuraType.ModHealingDone, 100),
        ], [], []);
        _spells = new SpellSystem(store, () => _now, random: new Random(1)) { MapUpdateIntervalMs = 0 };
        CasterSpellModules.Register(_spells);
        var resolver = new ReputationReactionResolver(ReputationFixtures.Templates, ReputationFixtures.Factions, _reputation.For);
        _map.Combat.Hooks = new ReputationCombatHooks(new FactionCombatHooks(ReputationFixtures.Templates), resolver);
    }

    public void Dispose() => _world.Dispose();

    private static SpellInfo Damage(uint id, SpellEffectInfo effect) => Spell(id, effect) with
    {
        School = SpellSchool.Fire,
        DamageClass = SpellDamageClass.Magic,
        RangeIndex = SpellConstants.RangeIndexSelfOnly,
        StartRecoveryCategory = 0,
    };

    private static SpellInfo Aura(uint id, AuraType type, int amount) => Spell(id, Effect(SpellEffectName.ApplyAura, amount, aura: type, misc: 0x7F)) with
    {
        Duration = new SpellDuration(600000, 0, 600000),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
    };

    private static GameObjectTemplate Object(uint entry, GameObjectType type, uint faction, params (int Index, uint Value)[] data)
    {
        var values = new uint[GameObjectTemplate.DataCount];
        foreach ((int index, uint value) in data)
        {
            values[index] = value;
        }

        return new GameObjectTemplate { Entry = entry, Type = (uint)type, DisplayId = 1, Name = $"Object {entry}", Faction = faction, Data = values };
    }

    private Player Join(uint guid, float x, float y)
    {
        Player player = TestWorld.CreatePlayer(guid, x, y, new FakeSession((int)guid));
        _world.AddPlayer(player);
        _reputation.Track(player, _reputation.Create(player, CharacterReputationData.Empty));
        _world.RunTick(0);
        return player;
    }

    private GameObject Place(uint entry, float x, float y) => _objects.Summon(entry, x, y, 83.5f, 0)!;

    private void Buff(Player player, uint aura)
        => Assert.Equal(SpellCastResult.CastOk, _spells.CastSpell(player, aura, SpellCastTargets.ForSelf(), triggered: true));

    [Fact]
    public void TheStandInsSpellPower_DoesNotRaiseTheObjectsDamage()
    {
        Player player = Join(1, 3, 0);
        Buff(player, DamageUp);
        GameObject trap = Place(WildTrap, 3, 0);

        Assert.Equal(SpellCastResult.CastOk, _spells.CastForGameObject(trap, Bolt, player, null));
        Assert.Equal(player.MaxHealth - 10, player.Health);
    }

    [Fact]
    public void TheStandInsHealingPower_DoesNotRaiseTheObjectsHeal()
    {
        Player player = Join(1, 3, 0);
        Buff(player, HealingUp);
        player.Health = 5;
        GameObject well = Place(WildGoober, 3, 0);

        Assert.Equal(SpellCastResult.CastOk, _spells.CastForGameObject(well, Mend, player, null));
        Assert.Equal(25u, player.Health);
    }

    [Fact]
    public void AnObjectNeverCrits()
    {
        _spells.CombatRules = new FixedRules { Crit = true };
        Player player = Join(1, 3, 0);
        GameObject trap = Place(WildTrap, 3, 0);

        Assert.Equal(SpellCastResult.CastOk, _spells.CastForGameObject(trap, Bolt, player, null));
        Assert.Equal(player.MaxHealth - 10, player.Health);

        player.Health = 5;
        GameObject well = Place(WildGoober, 3, 0);
        Assert.Equal(SpellCastResult.CastOk, _spells.CastForGameObject(well, Mend, player, null));
        Assert.Equal(25u, player.Health);
    }

    [Fact]
    public void TheStandInsSpellMods_DoNotApply()
    {
        _spells.SpellModifiers = new DoublingModifiers();
        Player player = Join(1, 3, 0);
        GameObject trap = Place(WildTrap, 3, 0);

        Assert.Equal(SpellCastResult.CastOk, _spells.CastForGameObject(trap, Bolt, player, null));
        Assert.Equal(player.MaxHealth - 10, player.Health);
    }

    [Theory]
    [InlineData(WildTrap, 30u)]   // trap.level (data1)
    [InlineData(WildGoober, 60u)] // no level column and no GAMEOBJECT_LEVEL: PLAYER_MAX_LEVEL
    public void TheEffectScalesWithTheObjectsLevel_NotTheStandIns(uint entry, uint level)
    {
        Player player = Join(1, 3, 0);
        player.MaxHealth = 500;
        player.Health = 500;
        GameObject source = Place(entry, 3, 0);

        Assert.Equal(SpellCastResult.CastOk, _spells.CastForGameObject(source, LevelBolt, player, null));
        Assert.Equal(500 - (10 + level), player.Health);
    }

    [Fact]
    public void AFactionObject_HitsThePlayersItsFactionIsHostileTo_WithTheReputationHooksOfTheWorld()
    {
        Player first = Join(1, 3, 0);
        Player second = Join(2, 4, 1);
        GameObject source = Place(HostileObject, 3, 0);

        Assert.Equal(SpellCastResult.CastOk, _spells.CastForGameObject(source, Nova, first, null));
        Assert.Equal(first.MaxHealth - 7, first.Health);
        Assert.Equal(second.MaxHealth - 7, second.Health);
    }

    [Fact]
    public void ANeutralObject_IsNotFriendly_SoItsFriendlyAreaLeavesPlayersOut()
    {
        // GameObject::IsFriendlyTo (GameObject.cpp:2122-2166) is its own test, not "not hostile": a neutral object helps nobody.
        Player first = Join(1, 3, 0);
        Player second = Join(2, 4, 1);
        first.Health = 10;
        second.Health = 10;
        GameObject source = Place(NeutralObject, 3, 0);

        _spells.CastForGameObject(source, Blessing, first, null);
        Assert.Equal(10u, first.Health);
        Assert.Equal(10u, second.Health);
    }

    [Fact]
    public void AnObjectJudgesAPetByItsOwner()
    {
        // GameObject::IsHostileTo (GameObject.cpp:2086-2090): a unit with a charmer or owner is judged as that charmer or owner.
        Player owner = Join(1, 20, 20);
        var pet = new CombatTestUnit { FactionTemplate = 2, MaxHealth = 100, Health = 100 }; // template 3 is not hostile to template 2
        pet.SetOwnerGuid(owner.Guid);
        pet.Relocate(3, 0, 83.5f, 0, 0);
        _map.AddObject(pet);
        _map.Combat.Track(pet);
        Player bystander = Join(2, 4, 1);
        _world.RunTick(0);
        GameObject source = Place(HostileObject, 3, 0);

        Assert.Equal(SpellCastResult.CastOk, _spells.CastForGameObject(source, Nova, bystander, null));
        Assert.Equal(93u, pet.Health);
    }

    [Fact]
    public void AReputationFactionObject_FollowsThePlayersStanding()
    {
        // GameObject::IsHostileTo (GameObject.cpp:2101-2115): for a player, the forced reaction or the standing with the object's reputation
        // faction decides, before the template relations (the Stormwind template is friendly to the player template).
        Player player = Join(1, 3, 0);
        GameObject source = Place(StormwindObject, 3, 0);

        _spells.CastForGameObject(source, Nova, player, null);
        Assert.Equal(player.MaxHealth, player.Health);

        _reputation.SetReputation(player, ReputationFixtures.Stormwind, ReputationMath.Bottom); // Hated
        Assert.Equal(SpellCastResult.CastOk, _spells.CastForGameObject(source, Nova, player, null));
        Assert.Equal(player.MaxHealth - 7, player.Health);
    }

    [Fact]
    public void AnObjectIsNeverHostileToAGameMaster()
    {
        Player gm = Join(1, 3, 0);
        gm.SetGameMaster(true);
        Player other = Join(2, 4, 1);
        GameObject source = Place(WildTrap, 3, 0);

        _spells.CastForGameObject(source, Nova, other, null);
        Assert.Equal(gm.MaxHealth, gm.Health);
        Assert.Equal(other.MaxHealth - 7, other.Health);
    }

    /// <summary>Spell mods that double every value of every caster (a talent the stand-in has).</summary>
    private sealed class DoublingModifiers : ISpellModifiers
    {
        public float Apply(Unit caster, SpellInfo spell, SpellModOp op, float value) => op is SpellModOp.AllEffects or SpellModOp.Damage ? value * 2 : value;
    }
}

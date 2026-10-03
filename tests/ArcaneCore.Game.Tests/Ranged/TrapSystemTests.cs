using System.Buffers.Binary;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Ranged;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Ranged;

/// <summary>
/// Hunter traps (vmangos GameObject::Update trap branch, GameObject.cpp:340-360 and 455-600, and
/// HunterTrapTargetSelectorCheck, 274-308). Synthetic templates and spells; the real trap data
/// (classic-db gameobject_template: radius 5, charges 1) is noted in docs/areas/hunter.md.
/// </summary>
public sealed class TrapSystemTests : IDisposable
{
    private const uint TrapSpell = 950002;
    private const uint FreezingEntry = 2561;   // one of the twelve templates vmangos forces to 2.5 yd
    private const uint CustomEntry = 950010;   // not in the override list
    private const uint DelayedEntry = 950011;
    private const uint ThreeChargeEntry = 950012;

    private readonly WorldRuntime _world;
    private readonly Map _map;
    private readonly CreatureMapSystem _creatures;
    private readonly GameObjectMapSystem _objects;
    private readonly SpellSystem _spells;
    private readonly FakeRelations _relations = new();
    private readonly Player _hunter;
    private readonly FakeSession _session = new(1);
    private readonly List<Unit> _hit = [];
    private uint _now = 10_000;

    public TrapSystemTests()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 40, 0), Spawn(2, WolfEntry, 50, 0)]);
        (WorldRuntime world, Map map, CreatureMapSystem creatures) = CreateSystem(content);
        _world = world;
        _map = map;
        _creatures = creatures;
        var goContent = new GameObjectContent(
        [
            Trap(FreezingEntry, radius: 5, charges: 1),
            Trap(CustomEntry, radius: 4, charges: 3),
            Trap(DelayedEntry, radius: 4, charges: 3, startDelay: 3),
            Trap(ThreeChargeEntry, radius: 4, charges: 3, cooldown: 10),
        ], [], [], [], []);
        _objects = new GameObjectMapSystem(map, goContent);
        map.AddUpdater(_objects);
        var store = new SpellStore(
        [
            .. new[] { FreezingEntry, CustomEntry, DelayedEntry, ThreeChargeEntry }.Select(entry =>
                Spell(LaySpellFor(entry), Effect(SpellEffectName.SummonObjectSlot1, 0, SpellImplicitTarget.None, misc: (int)entry)) with
                {
                    Duration = new SpellDuration(60_000, 0, 60_000),
                    StartRecoveryCategory = 0,
                    StartRecoveryTime = 0,
                }),
            Spell(TrapSpell, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with
            {
                RangeIndex = SpellConstants.RangeIndexSelfOnly,
                Duration = new SpellDuration(60_000, 0, 60_000),
            },
        ], [], []);
        _spells = new SpellSystem(store, () => _now, random: new Random(1)) { MapUpdateIntervalMs = 0, Relations = _relations, Units = new MapUnitResolver() };
        _spells.SpellHit += (_, target, spell) =>
        {
            if (spell.Id == TrapSpell)
            {
                _hit.Add(target);
            }
        };
        RangedHandlers.Register(_spells);
        map.AddUpdater(new SpellObjectSystem(_spells));
        map.AddUpdater(new TrapSystem(_spells));

        _hunter = TestWorld.CreatePlayer(1, 0, 0, _session);
        _hunter.Level = 40;
        _world.AddPlayer(_hunter);
        _world.RunTick(50);
        Wolf(0).Level = 40;
        Wolf(1).Level = 40;
        _relations.Hostile.Add(Wolf(0).Guid);
        _relations.Hostile.Add(Wolf(1).Guid);
        _session.Clear();
    }

    public void Dispose() => _world.Dispose();

    private sealed class MapUnitResolver : ISpellUnitResolver
    {
        public Unit? Find(Unit reference, ObjectGuid guid) => reference.Map?.Combat.FindUnit(guid) ?? reference.Map?.FindPlayer(guid);
    }

    private static GameObjectTemplate Trap(uint entry, uint radius, uint charges, uint cooldown = 0, uint startDelay = 0)
    {
        var data = new uint[GameObjectTemplate.DataCount];
        data[TrapRules.RadiusData] = radius;
        data[TrapRules.SpellData] = TrapSpell;
        data[TrapRules.ChargesData] = charges;
        data[TrapRules.CooldownData] = cooldown;
        data[TrapRules.StartDelayData] = startDelay;
        return new GameObjectTemplate { Entry = entry, Type = (uint)GameObjectType.Trap, DisplayId = entry == FreezingEntry ? 3071u : 1000 + entry, Name = $"Trap {entry}", Size = 1.0f, Data = data };
    }

    private Creature Wolf(int index) => _creatures.Creatures.OrderBy(c => c.Guid.Low).ElementAt(index);

    private GameObject LayTrap(uint entry, float x = 20, float y = 0)
    {
        // The summon goes where the spell puts it: a destination in the targets.
        var targets = new SpellCastTargets { Mask = SpellCastTargetFlags.DestLocation, Dest = (x, y, 83.5f) };
        Assert.Equal(SpellCastResult.CastOk, _spells.CastSpell(_hunter, LaySpellFor(entry), targets, triggered: true));
        return Assert.Single(_objects.GameObjects, g => g.Entry == entry);
    }

    private static uint LaySpellFor(uint entry) => 950100 + (entry % 100);

    private void Tick(uint ms = 50)
    {
        _now += ms;
        _spells.Update(ms);
        _world.RunTick(ms);
    }

    private static float Edge(GameObject trap, Unit unit, float radius) => radius + trap.BoundingRadius + unit.BoundingRadius;

    private void Place(Unit unit, GameObject trap, float distanceFromTrapCenter)
        => unit.Relocate(trap.X + distanceFromTrapCenter, trap.Y, trap.Z, 0, 0);

    [Fact]
    public void ARangeOfTwoPointFive_AppliesToTheHunterTrapTemplates_UnderTheVmangosSource()
    {
        GameObject trap = LayTrap(FreezingEntry);
        Creature wolf = Wolf(0);
        Place(wolf, trap, Edge(trap, wolf, 2.5f) + 0.2f); // inside the template's 5 yd, outside 2.5

        Tick();
        Assert.Empty(_hit);

        Place(wolf, trap, Edge(trap, wolf, 2.5f) - 0.2f);
        Tick();
        Assert.Equal([wolf], _hit);
    }

    [Fact]
    public void TheTemplateRadiusSource_UsesData2()
    {
        _spells.RangedOptions.Traps.RadiusSource = TrapRadiusSource.Template;
        GameObject trap = LayTrap(FreezingEntry);
        Creature wolf = Wolf(0);
        Place(wolf, trap, Edge(trap, wolf, 5f) - 0.2f);

        Tick();

        Assert.Equal([wolf], _hit);
    }

    [Fact]
    public void ATemplateOutsideTheOverrideList_KeepsItsOwnRadius()
    {
        GameObject trap = LayTrap(CustomEntry);
        Creature wolf = Wolf(0);
        Place(wolf, trap, Edge(trap, wolf, 4f) - 0.2f);

        Tick();

        Assert.Equal([wolf], _hit);
    }

    [Fact]
    public void OnlyHostileLivingUnits_Trigger()
    {
        GameObject trap = LayTrap(CustomEntry);
        Creature wolf = Wolf(0);
        Place(wolf, trap, 1.0f);

        _relations.Hostile.Remove(wolf.Guid); // friendly
        Tick();
        Assert.Empty(_hit);

        _relations.Hostile.Add(wolf.Guid);
        wolf.Health = 0;                      // dead
        Tick();
        Assert.Empty(_hit);
    }

    [Fact]
    public void TheNearestOfTwoCandidatesIsHit()
    {
        GameObject trap = LayTrap(CustomEntry);
        Creature near = Wolf(0);
        Creature far = Wolf(1);
        Place(far, trap, 2.0f);
        Place(near, trap, 1.0f);

        Tick();

        Assert.Equal([near], _hit);
    }

    [Fact]
    public void ATrapIgnoresPlayersUnlessItsOwnerIsFlaggedForPvp()
    {
        GameObject trap = LayTrap(CustomEntry);
        Player enemy = TestWorld.CreatePlayer(2, trap.X + 1, trap.Y, new FakeSession(2));
        _world.AddPlayer(enemy);
        _relations.Hostile.Add(enemy.Guid);

        Tick();
        Assert.Empty(_hit);

        _hunter.UnitFlags |= UnitFlags.Pvp;
        _hunter.Combat.PvpFlagTimer = CombatConstants.PvpFlagTimerMs; // the combat system clears a PvP flag whose timer ran out
        Tick(5000); // wait out nothing: the first scan above never fired, so the cooldown is clear
        Assert.Equal([enemy], _hit);
    }

    [Fact]
    public void ATriggerCastsTheTrapSpellAsTheOwner_StartsTheFourSecondCooldown_AndCountsTheCharge()
    {
        GameObject trap = LayTrap(CustomEntry); // charges 3, cooldown 0 in the template
        Creature wolf = Wolf(0);
        Place(wolf, trap, 1.0f);

        Tick();
        Assert.Single(_hit);
        Assert.Equal(1u, trap.UseCount);

        Tick(3000); // 3 s of 4: still cooling down
        Assert.Single(_hit);

        Tick(1100);
        Assert.Equal(2, _hit.Count);
        Assert.Equal(2u, trap.UseCount);
    }

    [Fact]
    public void TheTemplateCooldownReplacesTheDefault()
    {
        GameObject trap = LayTrap(ThreeChargeEntry); // cooldown 10 s
        Place(Wolf(0), trap, 1.0f);

        Tick();
        Assert.Single(_hit);
        Tick(9000);
        Assert.Single(_hit);
        Tick(1100);
        Assert.Equal(2, _hit.Count);
    }

    [Fact]
    public void WhenItsChargesAreUsedTheTrapGoes_AndFreesTheOwnersSlot()
    {
        GameObject trap = LayTrap(FreezingEntry); // charges 1
        Place(Wolf(0), trap, 1.0f);

        Tick();
        Assert.Single(_hit);
        Tick();
        Tick();

        Assert.DoesNotContain(trap, _objects.GameObjects);
        Assert.Equal(0, _spells.SpellObjects.Count);
    }

    [Fact]
    public void TheArmingDelayHoldsTheTrapBack()
    {
        GameObject trap = LayTrap(DelayedEntry); // startDelay 3 s
        Place(Wolf(0), trap, 1.0f);

        Tick();
        Tick(2000);
        Assert.Empty(_hit);

        Tick(1100);
        Assert.Single(_hit);
    }

    [Fact]
    public void TheOwnerNeedsToBeNeitherNearNorAlive()
    {
        GameObject trap = LayTrap(CustomEntry, x: 60); // 60 yd from the hunter at the origin
        Place(Wolf(0), trap, 1.0f);
        _hunter.Health = 0;

        Tick();

        Assert.Single(_hit);
    }

    [Fact]
    public void ATrapWhoseOwnerLeftTheMapDoesNotFire_AndIsRemoved()
    {
        GameObject trap = LayTrap(CustomEntry);
        Place(Wolf(0), trap, 1.0f);

        _world.RemovePlayer(_hunter);
        Tick();

        Assert.Empty(_hit);
        Assert.DoesNotContain(trap, _objects.GameObjects);
    }

    [Fact]
    public void ATrapWithoutAnOwner_NeverFires_ItIsEnvironmentalAndNotModelled()
    {
        GameObject runtime = _objects.Summon(CustomEntry, 20, 0, 83.5f, 0)!;
        Place(Wolf(0), runtime, 1.0f);

        Tick();
        Tick();

        Assert.Empty(_hit);
    }

    [Fact]
    public void TheFreezingTrapModelPlaysItsCustomAnimation_WhenItFires()
    {
        GameObject trap = LayTrap(FreezingEntry);
        Tick(); // the create block reaches the owner in the tick after the summon
        Tick();
        _session.Clear();
        Place(Wolf(0), trap, 1.0f);

        Tick();

        Assert.Contains(trap.Guid.Value, _session.Sent.Where(p => p.Opcode == WorldOpcode.SmsgGameobjectCustomAnim)
            .Select(p => BinaryPrimitives.ReadUInt64LittleEndian(p.Payload)));
    }
}

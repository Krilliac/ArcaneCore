using ArcaneCore.Game.Conditions;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells.Targeting;

/// <summary>
/// spell_script_target.conditionId (vmangos SpellMgr.cpp:2068, GridNotifiers.h:672 and :1210): a row whose conditionId is set selects a unit or
/// game object only when <c>IsConditionSatisfied(conditionId, candidate, map, caster)</c> holds. The condition is a CREATURE_IN_RANGE row
/// (Conditions.cpp:484-489, decided from the map): a beacon creature within 10 yards of the candidate. Beside the unconditioned control, the
/// nearer candidate has no beacon and the farther one has, so only the condition tells them apart. Written for the integration gate; not run in the lane.
/// </summary>
public sealed class ScriptTargetConditionTests
{
    private const uint NearestSpell = 990801;
    private const uint AreaSpell = 990802;
    private const uint LocationSpell = 990803;
    private const uint Listed = 990811;
    private const uint Beacon = 990812;
    private const uint Firecracker = 990821;
    private const uint BeaconCondition = 100;
    private const uint MissingCondition = 999;
    private const int Target46 = 46;

    private sealed class Forwarding(ConditionEvaluator inner) : IConditionEvaluator, IConditionTableEvaluator
    {
        public ConditionEvaluator Current => inner;

        public bool IsSatisfied(uint conditionId, Player player, NpcInfo? source) => inner.IsSatisfied(conditionId, player, source);
    }

    private static SpellInfo Aura(uint id, SpellImplicitTarget targetA, SpellImplicitTarget targetB = SpellImplicitTarget.None, float radius = 0)
        => Spell(id, Effect(SpellEffectName.ApplyAura, 1, targetA, AuraType.Dummy, targetB: targetB) with { Radius = radius }) with
        {
            Range = new SpellRange(0, 30),
            Duration = new SpellDuration(10_000, 0, 10_000),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    private static SpellInfo Location() => Spell(LocationSpell, Effect(SpellEffectName.Dummy, 0, (SpellImplicitTarget)Target46))
        with { RangeIndex = 4, Range = new SpellRange(0, 30), StartRecoveryCategory = 0, StartRecoveryTime = 0 };

    private sealed class Rig : IDisposable
    {
        public Rig(params SpellStore.ScriptTarget[] rows)
        {
            SpellInfo[] spells = [Aura(NearestSpell, SpellImplicitTarget.UnitScriptNearCaster),
                Aura(AreaSpell, SpellImplicitTarget.LocationCasterSrc, SpellImplicitTarget.EnumUnitsScriptAoeAtSrcLoc, radius: 20), Location()];
            Kit = new SpellTestKit(spells);
            var map = Kit.World.GetMap(0);
            Objects = new GameObjectMapSystem(map, new GameObjectContent([GameObjectTestKit.GoTemplate(Firecracker, GameObjectType.Goober)], [], [], [], []));
            map.AddUpdater(Objects);
            var conditions = new Forwarding(new ConditionEvaluator(
                ConditionTable.Build([new ConditionRecord(BeaconCondition, 37, Beacon, 10, 0, 0, 0)]), new ConditionContext()));
            Templates = [CreatureTestKit.Template(Listed, b => b.Faction = 35), CreatureTestKit.Template(Beacon, b => b.Faction = 35)];
            Creatures = new CreatureMapSystem(map, CreatureTestKit.Content(Templates, []), random: new Random(1),
                aiServices: new CreatureAiServices { Conditions = conditions });
            map.AddUpdater(Creatures);
            (Caster, Session) = Kit.AddPlayer(1);
            Kit.System.Units = new MapObjectResolver();
            Kit.System.Store = new SpellStore(spells, [], [], rows);
        }

        public SpellTestKit Kit { get; }
        public GameObjectMapSystem Objects { get; }
        public CreatureMapSystem Creatures { get; }
        public ArcaneCore.Kernel.WorldData.Creatures.CreatureTemplate[] Templates { get; }
        public Player Caster { get; }
        public FakeSession Session { get; }

        public Creature Spawn(uint entry, float x) => Creatures.SpawnTemporary(Templates.Single(t => t.Entry == entry), x, 0, Caster.Z, 0);

        public GameObject SpawnObject(float x) => Objects.Summon(Firecracker, x, 0, Caster.Z, 0)!;

        public SpellCastResult Cast(uint spell) => Kit.System.CastSpell(Caster, spell, SpellCastTargets.ForSelf(), triggered: true);

        public bool Has(Creature creature, uint spell) => Kit.System.HasAura(creature, spell);

        public SpellCastTargets SpellGoTargets()
        {
            var reader = new PacketReader(Assert.Single(Session.Sent, p => p.Opcode == WorldOpcode.SmsgSpellGo).Payload);
            reader.ReadPackedGuid();
            reader.ReadPackedGuid();
            reader.ReadUInt32();
            reader.ReadUInt16();
            int hits = reader.ReadByte();
            for (int i = 0; i < hits; i++) reader.ReadUInt64();
            Assert.Equal(0, (int)reader.ReadByte());
            return SpellCastTargets.Read(ref reader);
        }

        public void Dispose() => Kit.Dispose();
    }

    [Fact]
    public void Target38_WithNoCondition_TakesTheNearestListedUnit()
    {
        using var rig = new Rig(new SpellStore.ScriptTarget(NearestSpell, 1, Listed, 0));
        Creature near = rig.Spawn(Listed, 5);
        Creature far = rig.Spawn(Listed, 25);
        rig.Spawn(Beacon, 30);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(NearestSpell));

        Assert.True(rig.Has(near, NearestSpell));
        Assert.False(rig.Has(far, NearestSpell));
    }

    [Fact]
    public void Target38_WithACondition_SkipsTheNearerUnitThatFailsIt()
    {
        using var rig = new Rig(new SpellStore.ScriptTarget(NearestSpell, 1, Listed, 0, BeaconCondition));
        Creature near = rig.Spawn(Listed, 5);   // the beacon is 25 yards away: the condition fails
        Creature far = rig.Spawn(Listed, 25);   // the beacon is 5 yards away: the condition holds
        rig.Spawn(Beacon, 30);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(NearestSpell));

        Assert.False(rig.Has(near, NearestSpell));
        Assert.True(rig.Has(far, NearestSpell));
    }

    [Fact]
    public void Target38_WhenNoListedUnitMeetsTheCondition_FailsTheCast()
    {
        using var rig = new Rig(new SpellStore.ScriptTarget(NearestSpell, 1, Listed, 0, BeaconCondition));
        Creature near = rig.Spawn(Listed, 5);
        Creature far = rig.Spawn(Listed, 25);

        Assert.Equal(SpellCastResult.DontReport, rig.Cast(NearestSpell));

        Assert.False(rig.Has(near, NearestSpell));
        Assert.False(rig.Has(far, NearestSpell));
    }

    [Fact]
    public void Target38_WithAConditionThatIsNotLoaded_SelectsNobody()
    {
        // vmangos IsConditionSatisfied: a condition id that is not loaded is not satisfied (Conditions.cpp:1023-1028).
        using var rig = new Rig(new SpellStore.ScriptTarget(NearestSpell, 1, Listed, 0, MissingCondition));
        Creature near = rig.Spawn(Listed, 5);
        rig.Spawn(Beacon, 8);

        Assert.Equal(SpellCastResult.DontReport, rig.Cast(NearestSpell));

        Assert.False(rig.Has(near, NearestSpell));
    }

    [Fact]
    public void TheAreaTarget_KeepsOnlyTheListedUnitsThatMeetTheCondition()
    {
        using var rig = new Rig(new SpellStore.ScriptTarget(AreaSpell, 1, Listed, 0, BeaconCondition));
        Creature without = rig.Spawn(Listed, 2);
        Creature with = rig.Spawn(Listed, 18);
        rig.Spawn(Beacon, 24);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(AreaSpell));

        Assert.False(rig.Has(without, AreaSpell));
        Assert.True(rig.Has(with, AreaSpell));
    }

    [Fact]
    public void Target46_WithACondition_SetsTheDestinationToTheNearestObjectThatMeetsIt()
    {
        using var rig = new Rig(new SpellStore.ScriptTarget(LocationSpell, 0, Firecracker, 0, BeaconCondition));
        rig.SpawnObject(5);                     // no beacon within 10 yards
        GameObject meeting = rig.SpawnObject(20);
        rig.Spawn(Beacon, 24);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(LocationSpell));

        SpellCastTargets targets = rig.SpellGoTargets();
        Assert.True(targets.HasDest);
        Assert.Equal(meeting.X, targets.Dest.X, 0.01f);
    }

    [Fact]
    public void Target46_WhenNoObjectMeetsTheCondition_FailsTheCast()
    {
        using var rig = new Rig(new SpellStore.ScriptTarget(LocationSpell, 0, Firecracker, 0, BeaconCondition));
        rig.SpawnObject(5);
        rig.SpawnObject(20);

        Assert.Equal(SpellCastResult.DontReport, rig.Cast(LocationSpell));

        Assert.DoesNotContain(rig.Session.Sent, p => p.Opcode == WorldOpcode.SmsgSpellGo);
    }
}

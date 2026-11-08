using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Targets;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells.Targeting;

/// <summary>
/// spell_script_target.conditionId in the shapes of the real vmangos rows (vmangos world database conditions: 6492 HEALTH_PERCENT at most 5,
/// 122 OBJECT_IS_SPAWNED, 124 AND(122, 123 OBJECT_LOOT_STATE 1), 15958 AND(122, 192 OBJECT_GO_STATE 2), 1111 SOURCE_ENTRY 15882 reversed;
/// Conditions.h:101, 195, 217, 219, 248), translated at import into <see cref="ScriptTargetCondition"/> text. vmangos reads the row's condition only in
/// the nearest-unit and nearest-object searches of Spell::CheckScriptTargeting (targets 38, 46, 40; GridNotifiers.h:672 and :1210, Spell.cpp:480,
/// :539): a valid explicit target (Spell.cpp:511-533), the area targets 7 and 8 and the cone (:2356-2442, :2731-2738) and the object area targets
/// 51 and 52 (GameObjectEntryInPosRangeCheck, :2498-2503) ignore it. Written for the integration gate; not run in the lane.
/// </summary>
public sealed class ScriptTargetConditionTests
{
    private const uint NearestSpell = 990801;
    private const uint AreaSpell = 990802;
    private const uint LocationSpell = 990803;
    private const uint ObjectNearestSpell = 990804;
    private const uint ObjectSourceAreaSpell = 990805;
    private const uint DestAreaSpell = 990806;
    private const uint ObjectDestAreaSpell = 990807;
    private const uint ConeSpell = 990808;
    private const uint Listed = 990811;
    private const uint Firecracker = 990821;
    private const uint Door = 990822;
    private const int Target46 = 46;
    private const string AtMost5Percent = "health:5:2";
    private const string GoState2 = "gostate:2";

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

    private static SpellInfo ObjectSpell(uint id, int action, SpellImplicitTarget targetA, SpellImplicitTarget targetB = SpellImplicitTarget.None, float radius = 0)
        => Spell(id, Effect(SpellEffectName.ActivateObject, 0, targetA, misc: action, targetB: targetB) with { Radius = radius })
            with { RangeIndex = 4, Range = new SpellRange(0, 30), StartRecoveryCategory = 0, StartRecoveryTime = 0 };

    private sealed class Rig : IDisposable
    {
        public Rig(params SpellStore.ScriptTarget[] rows)
        {
            SpellInfo[] spells = [Aura(NearestSpell, SpellImplicitTarget.UnitScriptNearCaster),
                Aura(AreaSpell, SpellImplicitTarget.LocationCasterSrc, SpellImplicitTarget.EnumUnitsScriptAoeAtSrcLoc, radius: 20), Location(),
                ObjectSpell(ObjectNearestSpell, 6, (SpellImplicitTarget)40),
                ObjectSpell(ObjectSourceAreaSpell, 7, SpellImplicitTarget.LocationCasterSrc, (SpellImplicitTarget)51, 10),
                Aura(DestAreaSpell, SpellImplicitTarget.LocationCasterDest, SpellImplicitTarget.EnumUnitsScriptAoeAtDestLoc, radius: 20),
                ObjectSpell(ObjectDestAreaSpell, 7, SpellImplicitTarget.LocationCasterDest, (SpellImplicitTarget)52, 10),
                Aura(ConeSpell, (SpellImplicitTarget)SpellTargetSelectors.UnitsScriptInCone, radius: 20)];
            Kit = new SpellTestKit(spells);
            var map = Kit.World.GetMap(0);
            Objects = new GameObjectMapSystem(map, new GameObjectContent(
                [GameObjectTestKit.GoTemplate(Firecracker, GameObjectType.Goober), GameObjectTestKit.GoTemplate(Door, GameObjectType.Door)], [], [], [], []));
            map.AddUpdater(Objects);
            Templates = [CreatureTestKit.Template(Listed, b => b.Faction = 35)];
            Creatures = new CreatureMapSystem(map, CreatureTestKit.Content(Templates, []), random: new Random(1));
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

        /// <summary>A listed creature at <paramref name="x"/> with the given health percent.</summary>
        public Creature Spawn(float x, uint healthPercent = 100)
        {
            Creature creature = Creatures.SpawnTemporary(Templates.Single(t => t.Entry == Listed), x, 0, Caster.Z, 0);
            creature.MaxHealth = 1000;
            creature.Health = healthPercent * 10;
            return creature;
        }

        public GameObject SpawnObject(uint entry, float x, GameObjectState state = GameObjectState.Ready)
        {
            GameObject go = Objects.Summon(entry, x, 0, Caster.Z, 0)!;
            go.State = state;
            return go;
        }

        public SpellCastResult Cast(uint spell, SpellCastTargets? targets = null)
            => Kit.System.CastSpell(Caster, spell, targets ?? SpellCastTargets.ForSelf(), triggered: true);

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
        Creature near = rig.Spawn(5);
        Creature far = rig.Spawn(25);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(NearestSpell));

        Assert.True(rig.Has(near, NearestSpell));
        Assert.False(rig.Has(far, NearestSpell));
    }

    [Fact]
    public void Target38_WithAHealthCondition_SkipsTheNearerUnitThatFailsIt()
    {
        // Spells 9082 and 9012: the unit must be at 5 percent health or less (condition 6492).
        using var rig = new Rig(new SpellStore.ScriptTarget(NearestSpell, 1, Listed, 0, AtMost5Percent));
        Creature near = rig.Spawn(5);        // full health: the condition fails
        Creature far = rig.Spawn(25, 1);     // 1 percent: the condition holds

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(NearestSpell));

        Assert.False(rig.Has(near, NearestSpell));
        Assert.True(rig.Has(far, NearestSpell));
    }

    [Fact]
    public void Target38_WhenNoListedUnitMeetsTheCondition_FailsTheCast()
    {
        using var rig = new Rig(new SpellStore.ScriptTarget(NearestSpell, 1, Listed, 0, AtMost5Percent));
        Creature near = rig.Spawn(5);
        Creature far = rig.Spawn(25, 50);

        Assert.Equal(SpellCastResult.DontReport, rig.Cast(NearestSpell));

        Assert.False(rig.Has(near, NearestSpell));
        Assert.False(rig.Has(far, NearestSpell));
    }

    [Fact]
    public void Target38_WithAConditionTextThatCannotBeRead_SelectsNobody()
    {
        using var rig = new Rig(new SpellStore.ScriptTarget(NearestSpell, 1, Listed, 0, "not-a-condition:1"));
        Creature near = rig.Spawn(5, 1);

        Assert.Equal(SpellCastResult.DontReport, rig.Cast(NearestSpell));

        Assert.False(rig.Has(near, NearestSpell));
    }

    [Fact]
    public void Target38_KeepsAValidExplicitTarget_EvenWhenItFailsTheCondition()
    {
        // vmangos Spell.cpp:511-533 keeps a listed explicit target without reading conditionId; only the nearest search (:539) does.
        using var rig = new Rig(new SpellStore.ScriptTarget(NearestSpell, 1, Listed, 0, AtMost5Percent));
        Creature explicitTarget = rig.Spawn(10);   // full health
        Creature meeting = rig.Spawn(3, 1);        // nearer and meets the condition, but the explicit target wins

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(NearestSpell, SpellCastTargets.ForUnit(explicitTarget.Guid)));

        Assert.True(rig.Has(explicitTarget, NearestSpell));
        Assert.False(rig.Has(meeting, NearestSpell));
    }

    [Fact]
    public void TheAreaTarget_IgnoresTheCondition()
    {
        // vmangos target 7 (Spell.cpp:2356-2398) has no condition test: both units are hit though neither is at 5 percent.
        using var rig = new Rig(new SpellStore.ScriptTarget(AreaSpell, 1, Listed, 0, AtMost5Percent));
        Creature first = rig.Spawn(2);
        Creature second = rig.Spawn(18);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(AreaSpell));

        Assert.True(rig.Has(first, AreaSpell));
        Assert.True(rig.Has(second, AreaSpell));
    }

    [Fact]
    public void Target46_WithAGoStateCondition_SetsTheDestinationToTheNearestObjectThatMeetsIt()
    {
        // Spell 15958: the object must be spawned and in go state 2 (condition 15958, AND of 122 and 192).
        using var rig = new Rig(new SpellStore.ScriptTarget(LocationSpell, 0, Firecracker, 0, "spawned;" + GoState2));
        rig.SpawnObject(Firecracker, 5);
        GameObject meeting = rig.SpawnObject(Firecracker, 20, GameObjectState.ActiveAlternative);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(LocationSpell));

        SpellCastTargets targets = rig.SpellGoTargets();
        Assert.True(targets.HasDest);
        Assert.Equal(meeting.X, targets.Dest.X, 0.01f);
    }

    [Fact]
    public void Target46_WhenNoObjectMeetsTheCondition_FailsTheCast()
    {
        using var rig = new Rig(new SpellStore.ScriptTarget(LocationSpell, 0, Firecracker, 0, GoState2));
        rig.SpawnObject(Firecracker, 5);
        rig.SpawnObject(Firecracker, 20);

        Assert.Equal(SpellCastResult.DontReport, rig.Cast(LocationSpell));

        Assert.DoesNotContain(rig.Session.Sent, p => p.Opcode == WorldOpcode.SmsgSpellGo);
    }

    [Fact]
    public void Target46_WithALootStateCondition_ChoosesTheObjectInThatState()
    {
        // Spell 10731 uses condition 124, AND(spawned, loot state 1); the state is 2 here so the nearer object in state 1 is passed over.
        using var rig = new Rig(new SpellStore.ScriptTarget(LocationSpell, 0, Firecracker, 0, "spawned;loot:2"));
        rig.SpawnObject(Firecracker, 5);
        GameObject activated = rig.SpawnObject(Firecracker, 20);
        activated.LootState = GameObjectLootState.Activated;

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(LocationSpell));

        Assert.Equal(activated.X, rig.SpellGoTargets().Dest.X, 0.01f);
    }

    [Fact]
    public void Target46_WithAReversedSourceEntryCondition_HoldsForACasterWithAnotherEntry()
    {
        // Spell 26347: the launcher is found only when the caster's entry is not 15882 (condition 1111). The caster here is a player (entry 0).
        using var rig = new Rig(new SpellStore.ScriptTarget(LocationSpell, 0, Firecracker, 0, "!entry:15882"));
        GameObject launcher = rig.SpawnObject(Firecracker, 7);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(LocationSpell));

        Assert.Equal(launcher.X, rig.SpellGoTargets().Dest.X, 0.01f);
    }

    [Fact]
    public void Target46_WithASourceEntryCondition_FailsForACasterWithAnotherEntry()
    {
        using var rig = new Rig(new SpellStore.ScriptTarget(LocationSpell, 0, Firecracker, 0, "entry:15882"));
        rig.SpawnObject(Firecracker, 7);

        Assert.Equal(SpellCastResult.DontReport, rig.Cast(LocationSpell));
    }

    [Fact]
    public void Target40_ChoosesTheNearestObjectThatMeetsTheCondition()
    {
        using var rig = new Rig(new SpellStore.ScriptTarget(ObjectNearestSpell, 0, Door, 0, GoState2));
        GameObject near = rig.SpawnObject(Door, 3);
        GameObject far = rig.SpawnObject(Door, 12, GameObjectState.ActiveAlternative);
        near.Flags |= GameObjectFlags.Locked;
        far.Flags |= GameObjectFlags.Locked;

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(ObjectNearestSpell));

        Assert.True(near.Flags.HasFlag(GameObjectFlags.Locked));
        Assert.False(far.Flags.HasFlag(GameObjectFlags.Locked));
    }

    [Fact]
    public void Target40_WhenNoObjectMeetsTheCondition_ChoosesNone()
    {
        using var rig = new Rig(new SpellStore.ScriptTarget(ObjectNearestSpell, 0, Door, 0, GoState2));
        GameObject near = rig.SpawnObject(Door, 3);
        GameObject far = rig.SpawnObject(Door, 12);
        near.Flags |= GameObjectFlags.Locked;
        far.Flags |= GameObjectFlags.Locked;

        rig.Cast(ObjectNearestSpell);

        Assert.True(near.Flags.HasFlag(GameObjectFlags.Locked));
        Assert.True(far.Flags.HasFlag(GameObjectFlags.Locked));
    }

    [Fact]
    public void TheDestinationAreaTarget8_IgnoresTheCondition()
    {
        // vmangos target 8 (Spell.cpp:2405-2470) has no condition test either: both units are hit though neither is at 5 percent.
        using var rig = new Rig(new SpellStore.ScriptTarget(DestAreaSpell, 1, Listed, 0, AtMost5Percent));
        Creature first = rig.Spawn(2);
        Creature second = rig.Spawn(18);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(DestAreaSpell));

        Assert.True(rig.Has(first, DestAreaSpell));
        Assert.True(rig.Has(second, DestAreaSpell));
    }

    [Fact]
    public void TheCone_IgnoresTheCondition_ButKeepsToTheArcInFrontOfTheCaster()
    {
        // vmangos target 60 (Spell.cpp:2702-2740) fills from the rows' entries inside the caster's frontal cone and reads no conditionId:
        // both units in front are hit though neither is at 5 percent, and the one behind the caster is not.
        using var rig = new Rig(new SpellStore.ScriptTarget(ConeSpell, 1, Listed, 0, AtMost5Percent));
        rig.Caster.Orientation = 0f;
        Creature first = rig.Spawn(2);
        Creature second = rig.Spawn(18);
        Creature behind = rig.Spawn(-6);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(ConeSpell));

        Assert.True(rig.Has(first, ConeSpell));
        Assert.True(rig.Has(second, ConeSpell));
        Assert.False(rig.Has(behind, ConeSpell));
    }

    [Fact]
    public void Target52_IgnoresTheCondition()
    {
        using var rig = new Rig(new SpellStore.ScriptTarget(ObjectDestAreaSpell, 0, Firecracker, 0, GoState2));
        GameObject first = rig.SpawnObject(Firecracker, 3);
        GameObject second = rig.SpawnObject(Firecracker, 7);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(ObjectDestAreaSpell));

        Assert.True(first.Flags.HasFlag(GameObjectFlags.Locked));
        Assert.True(second.Flags.HasFlag(GameObjectFlags.Locked));
    }

    [Fact]
    public void Target51_IgnoresTheCondition()
    {
        // vmangos GameObjectEntryInPosRangeCheck (Spell.cpp:2498-2503) has no condition: both objects are locked though neither is in state 2.
        using var rig = new Rig(new SpellStore.ScriptTarget(ObjectSourceAreaSpell, 0, Firecracker, 0, GoState2));
        GameObject first = rig.SpawnObject(Firecracker, 3);
        GameObject second = rig.SpawnObject(Firecracker, 7);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(ObjectSourceAreaSpell));

        Assert.True(first.Flags.HasFlag(GameObjectFlags.Locked));
        Assert.True(second.Flags.HasFlag(GameObjectFlags.Locked));
    }

    [Fact]
    public void TheConditionTerms_FollowVmangos()
    {
        using var rig = new Rig();
        Creature unit = rig.Spawn(5, 4);
        GameObject go = rig.SpawnObject(Firecracker, 6);

        Assert.True(ScriptTargetCondition.Parse("health:5:2")!.Holds(unit, rig.Caster));
        Assert.False(ScriptTargetCondition.Parse("health:3:2")!.Holds(unit, rig.Caster));
        Assert.True(ScriptTargetCondition.Parse("health:4:0")!.Holds(unit, rig.Caster));
        Assert.True(ScriptTargetCondition.Parse("health:4:1")!.Holds(unit, rig.Caster));
        Assert.True(ScriptTargetCondition.Parse("spawned")!.Holds(go, rig.Caster));
        Assert.False(ScriptTargetCondition.Parse("!spawned")!.Holds(go, rig.Caster));
        Assert.True(ScriptTargetCondition.Parse("loot:1")!.Holds(go, rig.Caster));
        Assert.True(ScriptTargetCondition.Parse("!loot:2")!.Holds(go, rig.Caster));
        // A term whose candidate has the wrong kind is false, and the reverse flag does not make it true (Conditions.cpp:119-140).
        Assert.False(ScriptTargetCondition.Parse("spawned")!.Holds(unit, rig.Caster));
        Assert.False(ScriptTargetCondition.Parse("!spawned")!.Holds(unit, rig.Caster));
        Assert.False(ScriptTargetCondition.Parse("health:5:2")!.Holds(go, rig.Caster));
        // The caster is the source: its entry is the one compared.
        Assert.True(ScriptTargetCondition.Parse($"entry:1:{Listed}")!.Holds(go, unit));
        Assert.False(ScriptTargetCondition.Parse($"entry:{Listed}")!.Holds(go, rig.Caster));
        Assert.Null(ScriptTargetCondition.Parse(""));
        Assert.False(ScriptTargetCondition.Parse("health:5")!.Holds(unit, rig.Caster));
    }
}

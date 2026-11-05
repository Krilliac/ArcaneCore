using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Casters;
using ArcaneCore.Game.Tests.SpellMods;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells.Targeting;

/// <summary>
/// TARGET_LOCATION_SCRIPT_NEAR_CASTER (46; vmangos Spell::CheckScriptTargeting, Spell.cpp:446-600): the nearest spell_script_target object within range
/// is the destination and, with target A 46, the effect's target.
/// </summary>
public sealed class ScriptLocationTargetTests
{
    private const uint Firework = 990301;
    private const uint Firecracker = 990501;
    private const uint Fountain = 990502;
    private const uint Marker = 990601;
    private const int Target46 = 46;

    private static SpellInfo FireworkSpell(uint id = Firework) => Spell(id, Effect(SpellEffectName.Dummy, 0, (SpellImplicitTarget)Target46))
        with { RangeIndex = 4, Range = new SpellRange(0, 30), StartRecoveryCategory = 0, StartRecoveryTime = 0 };

    private sealed class Rig : IDisposable
    {
        public Rig(params SpellStore.ScriptTarget[] rows) : this([FireworkSpell()], rows)
        {
        }

        public Rig(SpellInfo[] spells, SpellStore.ScriptTarget[] rows)
        {
            Kit = new SpellTestKit(spells);
            var map = Kit.World.GetMap(0);
            Objects = new GameObjectMapSystem(map, new GameObjectContent(
                [GameObjectTestKit.GoTemplate(Firecracker, GameObjectType.Goober), GameObjectTestKit.GoTemplate(Fountain, GameObjectType.Goober)],
                [], [], [], []));
            map.AddUpdater(Objects);
            (Caster, Session) = Kit.AddPlayer(1);
            Kit.System.Store = new SpellStore(spells, [], [], rows);
        }

        public SpellTestKit Kit { get; }
        public GameObjectMapSystem Objects { get; }
        public Player Caster { get; }
        public FakeSession Session { get; }

        public GameObject Spawn(uint entry, float x) => Objects.Summon(entry, x, 0, Caster.Z, 0)!;

        public SpellCastResult Cast(uint spell = Firework) => Kit.System.CastSpell(Caster, spell, SpellCastTargets.ForSelf(), triggered: true);

        public ulong[] SpellGoHits()
        {
            var reader = new PacketReader(Assert.Single(Session.Sent, p => p.Opcode == WorldOpcode.SmsgSpellGo).Payload);
            reader.ReadPackedGuid();
            reader.ReadPackedGuid();
            reader.ReadUInt32();
            reader.ReadUInt16();
            int count = reader.ReadByte();
            var hits = new ulong[count];
            for (int i = 0; i < count; i++) hits[i] = reader.ReadUInt64();
            return hits;
        }

        /// <summary>The destination block of the only SMSG_SPELL_GO (the target block after the hit and miss lists).</summary>
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

    private const uint BeyondTheUnit = 990701;
    private const uint RadiusMod = 990702;
    private const uint DamageVsGuards = 990703;
    private const uint Guard = 16398;

    /// <summary>Target A names the caster, target B is 46: the shape of 12699 and 22393 (A unit / B 46), range 30.</summary>
    private static SpellInfo UnitWithLocation() => Spell(BeyondTheUnit, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitCaster, targetB: (SpellImplicitTarget)Target46))
        with { RangeIndex = 4, Range = new SpellRange(0, 30), StartRecoveryCategory = 0, StartRecoveryTime = 0 };

    [Fact]
    public void TargetB46BesideAUnit_SetsTheDestination_AndStillHitsTheUnitOfTargetA()
    {
        using var rig = new Rig([UnitWithLocation()], [new SpellStore.ScriptTarget(BeyondTheUnit, 0, Firecracker, 0)]);
        GameObject listed = rig.Spawn(Firecracker, 4);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(BeyondTheUnit));

        Assert.Contains(rig.Caster.Guid.Value, rig.SpellGoHits());
        SpellCastTargets targets = rig.SpellGoTargets();
        Assert.True(targets.HasDest);
        Assert.Equal(listed.X, targets.Dest.X, 0.01f);
        Assert.Equal(listed.Y, targets.Dest.Y, 0.01f);
    }

    [Fact]
    public void TargetB46BesideAUnit_WithNoListedObjectInRange_FailsTheCast()
    {
        // vmangos SetTargetMap runs CheckScriptTargeting for target B too (Spell.cpp:2848-2860): nothing listed in range fails the cast.
        using var rig = new Rig([UnitWithLocation()], [new SpellStore.ScriptTarget(BeyondTheUnit, 0, Firecracker, 0)]);
        rig.Spawn(Fountain, 3);

        Assert.Equal(SpellCastResult.DontReport, rig.Cast(BeyondTheUnit));

        Assert.DoesNotContain(rig.Session.Sent, p => p.Opcode == WorldOpcode.SmsgSpellGo);
    }

    [Fact]
    public void ANonTriggeredCast_WithNoListedObjectInRange_FailsWithBadTargets()
    {
        using var rig = new Rig(new SpellStore.ScriptTarget(Firework, 0, Firecracker, 0));
        rig.Spawn(Firecracker, 60);
        rig.Kit.LearnSpell(rig.Caster, Firework);

        Assert.Equal(SpellCastResult.BadTargets, rig.Kit.System.CastSpell(rig.Caster, Firework, SpellCastTargets.ForSelf(), triggered: false));

        Assert.DoesNotContain(rig.Session.Sent, p => p.Opcode == WorldOpcode.SmsgSpellGo);
    }

    [Fact]
    public void ARadiusMod_ExtendsTheSearchOfTarget46()
    {
        // SPELLMOD_RADIUS applies to the search radius (vmangos Spell.cpp:2059-2062): range 10 * 1.25 reaches an object 12 yards away.
        SpellInfo spell = ModTestKit.InFamily(FireworkSpell() with { Range = new SpellRange(0, 10) });
        using var rig = new Rig([spell, ModTestKit.Pct(RadiusMod, SpellModOp.Radius, 25)], [new SpellStore.ScriptTarget(Firework, 0, Firecracker, 0)]);
        CasterSpellModules.Register(rig.Kit.System, null);
        GameObject listed = rig.Spawn(Firecracker, 12);

        Assert.Equal(SpellCastResult.DontReport, rig.Cast());

        rig.Kit.System.LearnSpell(rig.Caster, RadiusMod);
        rig.Kit.Advance(100);
        rig.Session.Clear();

        Assert.Equal(SpellCastResult.CastOk, rig.Cast());
        Assert.Contains(listed.Guid.Value, rig.SpellGoHits());
    }

    [Fact]
    public void ARadiusZeroSpell_Like28364_FindsAListedCreatureInMeleeReach_AndFailsWhenNoneIsClose()
    {
        // Damage vs. Guards: range index 1 (self only, no radius), effect A6 / B46, rows for the guard entries. vmangos searches with
        // NearestUnitFitConditionInCombatRangeCheck (combat distance, Spell.cpp:539), so a guard in melee reach is found at radius 0.
        SpellInfo spell = Spell(DamageVsGuards, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitEnemy, targetB: (SpellImplicitTarget)Target46))
            with { StartRecoveryCategory = 0, StartRecoveryTime = 0 };
        using var kit = new PetTestKit([spell], extraTemplates: [CreatureTestKit.Template(Guard, b => b.Faction = 14)]);
        kit.Spells.System.Store = new SpellStore(kit.Spells.Store.All, [], [], [new SpellStore.ScriptTarget(DamageVsGuards, 1, Guard, 0)]);
        (Player caster, _) = kit.AddPlayer(1);
        Creature touching = kit.Creatures.SummonForInstance(Guard, 1f, 0f, caster.Z, 0f)!;
        Creature distant = kit.Creatures.SummonForInstance(Guard, 15f, 0f, caster.Z, 0f)!;

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(caster, DamageVsGuards, SpellCastTargets.ForUnit(touching.Guid)));
        Assert.Equal(SpellCastResult.DontReport, kit.Cast(caster, DamageVsGuards, SpellCastTargets.ForUnit(distant.Guid)));
    }

    [Fact]
    public void ARowOfTypeGameObject_LandsOnTheNearestListedObject_NotAnUnlistedOrFartherOne()
    {
        using var rig = new Rig(new SpellStore.ScriptTarget(Firework, 0, Firecracker, 0));
        GameObject far = rig.Spawn(Firecracker, 20);
        GameObject near = rig.Spawn(Firecracker, 4);
        GameObject unlisted = rig.Spawn(Fountain, 2);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast());

        ulong[] hits = rig.SpellGoHits();
        Assert.Contains(near.Guid.Value, hits);
        Assert.DoesNotContain(far.Guid.Value, hits);
        Assert.DoesNotContain(unlisted.Guid.Value, hits);
    }

    [Fact]
    public void AListedObjectBeyondTheSpellRange_FailsTheCastWithoutReporting_AndNothingIsSent()
    {
        // vmangos Spell::CheckScriptTargeting (Spell.cpp:603-616): rows but nothing in range fails the cast (DONT_REPORT when triggered).
        using var rig = new Rig(new SpellStore.ScriptTarget(Firework, 0, Firecracker, 0));
        rig.Spawn(Firecracker, 60);

        Assert.Equal(SpellCastResult.DontReport, rig.Cast());

        Assert.DoesNotContain(rig.Session.Sent, p => p.Opcode == WorldOpcode.SmsgSpellGo);
    }

    [Fact]
    public void ARowOfTypeCreature_LandsOnTheNearestListedCreature()
    {
        const uint proxy = 16398;
        using var kit = new PetTestKit([FireworkSpell()], extraTemplates: [CreatureTestKit.Template(proxy, b => b.Faction = 35)]);
        kit.Spells.System.Store = new SpellStore(kit.Spells.Store.All, [], [], [new SpellStore.ScriptTarget(Firework, 1, proxy, 0)]);
        List<Unit> hit = [];
        kit.Spells.System.SpellHit += (_, target, spell) =>
        {
            if (spell.Id == Firework) hit.Add(target);
        };
        (Player caster, _) = kit.AddPlayer(1);
        Creature near = kit.Creatures.SummonForInstance(proxy, 5f, 0f, caster.Z, 0f)!;
        Creature far = kit.Creatures.SummonForInstance(proxy, 20f, 0f, caster.Z, 0f)!;
        Creature unlisted = kit.Creatures.SummonForInstance(PetTestKit.WildEntry, 2f, 0f, caster.Z, 0f)!;

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(caster, Firework));

        Assert.Same(near, Assert.Single(hit));
        Assert.DoesNotContain(far, hit);
        Assert.DoesNotContain(unlisted, hit);
    }

    [Fact]
    public void ASpellWithNoRows_FailsWithBadTargets()
    {
        using var rig = new Rig();
        rig.Spawn(Firecracker, 4);

        Assert.Equal(SpellCastResult.BadTargets, rig.Cast());
    }

    [Fact]
    public void ASpellSpecificSelector_KeepsPrecedence_AndNeedsNoRows()
    {
        // Destroy Egg (BlackwingLairTargetModule) owns its target 46; the generic selector never runs for it, rows or not.
        using var rig = new Rig();
        GameObject near = rig.Spawn(Firecracker, 3);
        bool ran = false;
        rig.Kit.System.RegisterSpellTargetSelector(Firework, (SpellImplicitTarget)Target46, (_, cast, _, _) =>
        {
            ran = true;
            return [(cast.Caster, 1f)];
        });

        Assert.Equal(SpellCastResult.CastOk, rig.Cast());

        Assert.True(ran);
        Assert.DoesNotContain(near.Guid.Value, rig.SpellGoHits());
    }

    [Theory]
    [InlineData(11)]
    [InlineData(46)]
    [InlineData(58)]
    [InlineData(62)]
    public void TheTargetsOfThisArea_AreSelectable(int target)
    {
        using var rig = new Rig();
        SpellInfo spell = Spell(Marker, Effect(SpellEffectName.Dummy, 0, (SpellImplicitTarget)target));

        Assert.True(rig.Kit.System.CanSelectImplicitTarget(spell, spell.Effects[0]));
    }
}

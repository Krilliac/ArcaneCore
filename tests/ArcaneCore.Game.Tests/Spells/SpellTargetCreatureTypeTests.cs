using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// Spell.dbc TargetCreatureType (a mask of CreatureType.dbc ids, bit = id - 1) and MaxTargetLevel, in vmangos terms:
/// Spell::CheckTargetCreatureType (Spell.cpp:7577-7605) reached from CheckCast (:5578-5585), the area filter (:7641) and the hit
/// table (:1003); the level cap is CheckCast :5463-5464.
/// </summary>
public sealed class SpellTargetCreatureTypeTests
{
    private const uint Polymorph = 970101;
    private const uint ShackleUndead = 970102;
    private const uint Banish = 970103;
    private const uint AreaBolt = 970104;
    private const uint DismissPet = 2641;
    private const uint Doom = 603;
    private const uint Capped = 970105;
    private const uint Uncapped = 970106;
    private const uint Grounding = 8179;
    private const uint Protection = 970107;
    private const uint SelfHeal = 970120;
    private const uint UndeadOnly = 970121;

    private const uint Beast = 1u << 0;
    private const uint Demon = 1u << 2;
    private const uint Elemental = 1u << 3;
    private const uint Undead = 1u << 5;
    private const uint Humanoid = 1u << 6;
    private const uint Critter = 1u << 7;

    private const uint BeastType = 1;
    private const uint DemonType = 3;
    private const uint UndeadType = 6;
    private const uint HumanoidType = 7;

    private static SpellInfo Bolt(uint id, uint mask, uint maxTargetLevel = 0) => Spell(id,
        Effect(SpellEffectName.SchoolDamage, 15, SpellImplicitTarget.UnitEnemy)) with
    {
        School = SpellSchool.Shadow,
        DamageClass = SpellDamageClass.Magic,
        TargetCreatureType = mask,
        MaxTargetLevel = maxTargetLevel,
        RangeIndex = 4,
        Range = new SpellRange(0, 30),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellInfo AreaSpell(uint id, uint mask) => Spell(id,
        Effect(SpellEffectName.SchoolDamage, 15, SpellImplicitTarget.LocationCasterSrc, targetB: SpellImplicitTarget.EnumUnitsEnemyAoeAtSrcLoc) with { Radius = 20 }) with
    {
        School = SpellSchool.Shadow,
        DamageClass = SpellDamageClass.Magic,
        TargetCreatureType = mask,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellInfo[] Spells() =>
    [
        Bolt(Polymorph, Beast | Humanoid | Critter),
        Bolt(ShackleUndead, Undead),
        Bolt(Banish, Demon | Elemental),
        Bolt(DismissPet, Undead),
        Bolt(Doom, 0),
        Bolt(Capped, 0, maxTargetLevel: 30),
        Bolt(Uncapped, 0),
        Bolt(UndeadOnly, Undead),
        Bolt(SelfHeal, Undead) with { Effects = [Effect(SpellEffectName.Heal, 5, SpellImplicitTarget.UnitCaster)] },
        AreaSpell(AreaBolt, Undead),
        Spell(Grounding, Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
        {
            Duration = new SpellDuration(60_000, 0, 60_000),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        },
        Spell(Protection, Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitFriend, AuraType.SpellMagnet)) with
        {
            ProcCharges = 1,
            Duration = new SpellDuration(60_000, 0, 60_000),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        },
    ];

    private sealed class MapUnits : ISpellUnitResolver
    {
        public Unit? Find(Unit reference, ObjectGuid guid)
            => reference.Guid == guid ? reference : reference.Map?.FindObject(guid) as Unit;
    }

    private sealed class Outcomes : ISpellCastObserver
    {
        public List<SpellTargetOutcome> Seen { get; } = [];

        public void OnTargetOutcome(SpellCast cast, SpellTargetOutcome outcome) => Seen.Add(outcome);
    }

    private sealed class Scene : IDisposable
    {
        private uint _next = 990_000;

        public Scene(bool installChecks)
        {
            Kit = new SpellTestKit(Spells());
            Kit.System.Units = new MapUnits();
            Kit.System.CombatRules = SpellCombatRules.Neutral;
            Relations = new FakeRelations();
            Kit.System.Relations = Relations;
            if (installChecks)
            {
                GeneralCastChecks.Install(Kit.System);
            }

            Kit.System.RegisterObserver(Seen);
            (Caster, _) = Kit.AddPlayer(1);
            Caster.Level = 60;
            Caster.MaxHealth = Caster.Health = 1000;
            Relations.Hostile.Add(Caster.Guid);
        }

        public SpellTestKit Kit { get; }

        public FakeRelations Relations { get; }

        public Outcomes Seen { get; } = new();

        public Player Caster { get; }

        /// <summary>A hostile creature of <paramref name="creatureType"/> (0 = no type) at the given x.</summary>
        public Creature AddCreature(uint creatureType, float x, byte level = 20)
        {
            uint entry = ++_next;
            var template = new CreatureTemplate
            {
                Entry = entry, Name = $"Typed {entry}", MinLevel = level, MaxLevel = level,
                MinLevelHealth = 100, MaxLevelHealth = 100, DisplayIds = [1], Faction = 14, CreatureType = creatureType,
            };
            var creature = new Creature(entry, template, null, new CreatureContent([template], [], [], [], []), new Random(1));
            Map map = Kit.World.GetMap(0);
            creature.Relocate(x, 0, Caster.Z, 0, 0);
            creature.MapId = 0;
            map.AddObject(creature);
            map.Combat.Track(creature);
            Kit.World.RunTick(0);
            creature.Level = level;
            creature.MaxHealth = creature.Health = 100;
            return creature;
        }

        public SpellCastResult Cast(uint spell, Unit target, Unit? caster = null)
            => Kit.System.CastSpell(caster ?? Caster, spell, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        public void Dispose() => Kit.Dispose();
    }

    [Fact]
    public void Polymorph_OnAnUndead_FailsBadTargets_AndOnABeastOrHumanoidSucceeds()
    {
        using var s = new Scene(installChecks: true);
        Creature undead = s.AddCreature(UndeadType, 3);
        Creature beast = s.AddCreature(BeastType, 4);
        Creature humanoid = s.AddCreature(HumanoidType, 5);

        Assert.Equal(SpellCastResult.BadTargets, s.Cast(Polymorph, undead));
        Assert.Equal(SpellCastResult.CastOk, s.Cast(Polymorph, beast));
        Assert.Equal(SpellCastResult.CastOk, s.Cast(Polymorph, humanoid));
    }

    [Fact]
    public void ShackleUndead_OnAHumanoid_FailsBadTargets_OnAPlayerTargetIsPlayer_OnAnUndeadSucceeds()
    {
        using var s = new Scene(installChecks: true);
        Creature humanoid = s.AddCreature(HumanoidType, 3);
        Creature undead = s.AddCreature(UndeadType, 4);
        Player other = s.Kit.AddPlayer(2, 5).Player;
        other.MaxHealth = other.Health = 100;

        Assert.Equal(SpellCastResult.BadTargets, s.Cast(ShackleUndead, humanoid));
        Assert.Equal(SpellCastResult.TargetIsPlayer, s.Cast(ShackleUndead, other));
        Assert.Equal(SpellCastResult.CastOk, s.Cast(ShackleUndead, undead));
    }

    [Fact]
    public void Banish_OnADemon_Succeeds_OnABeastFails()
    {
        using var s = new Scene(installChecks: true);
        Creature demon = s.AddCreature(DemonType, 3);
        Creature beast = s.AddCreature(BeastType, 4);

        Assert.Equal(SpellCastResult.CastOk, s.Cast(Banish, demon));
        Assert.Equal(SpellCastResult.BadTargets, s.Cast(Banish, beast));
    }

    [Fact]
    public void ACreatureWithNoType_PassesAnyTypeMask()
    {
        using var s = new Scene(installChecks: true);
        Creature untyped = s.AddCreature(0, 3);

        Assert.Equal(SpellCastResult.CastOk, s.Cast(ShackleUndead, untyped));
    }

    [Fact]
    public void AnAuraOfGroundingTotem8179_OnTheTarget_BypassesTheTypeCheck()
    {
        using var s = new Scene(installChecks: true);
        Creature humanoid = s.AddCreature(HumanoidType, 3);
        Assert.Equal(SpellCastResult.BadTargets, s.Cast(ShackleUndead, humanoid));

        Assert.Equal(SpellCastResult.CastOk, s.Kit.System.CastSpell(humanoid, Grounding, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(s.Kit.System.HasAura(humanoid, Grounding));

        Assert.Equal(SpellCastResult.CastOk, s.Cast(ShackleUndead, humanoid));
    }

    [Fact]
    public void DismissPet_IgnoresItsTypeMask()
    {
        using var s = new Scene(installChecks: true);
        Creature humanoid = s.AddCreature(HumanoidType, 3);

        Assert.Equal(SpellCastResult.CastOk, s.Cast(DismissPet, humanoid));
    }

    [Fact]
    public void CurseOfDoom_AcceptsEveryCreatureType_ButNeverAPlayer()
    {
        using var s = new Scene(installChecks: true);
        Creature beast = s.AddCreature(BeastType, 3);
        Player other = s.Kit.AddPlayer(2, 5).Player;
        other.MaxHealth = other.Health = 100;

        Assert.Equal(SpellCastResult.CastOk, s.Cast(Doom, beast));
        Assert.Equal(SpellCastResult.TargetIsPlayer, s.Cast(Doom, other));
    }

    [Fact]
    public void ASelfCast_SkipsTheTypeCheck()
    {
        using var s = new Scene(installChecks: true);

        Assert.Equal(SpellCastResult.CastOk, s.Kit.System.CastSpell(s.Caster, SelfHeal, SpellCastTargets.ForSelf(), triggered: true));
    }

    [Fact]
    public void AnAreaSpellWithATypeMask_SkipsNonMatchingUnits()
    {
        using var s = new Scene(installChecks: true);
        Creature undead = s.AddCreature(UndeadType, 3);
        Creature humanoid = s.AddCreature(HumanoidType, 4);
        Creature beast = s.AddCreature(BeastType, 5);

        Assert.Equal(SpellCastResult.CastOk, s.Kit.System.CastSpell(s.Caster, AreaBolt, SpellCastTargets.ForSelf(), triggered: true));

        Assert.Contains(s.Seen.Seen, o => ReferenceEquals(o.Target, undead));
        Assert.DoesNotContain(s.Seen.Seen, o => ReferenceEquals(o.Target, humanoid));
        Assert.DoesNotContain(s.Seen.Seen, o => ReferenceEquals(o.Target, beast));
        Assert.True(undead.Health < 100);
        Assert.Equal(100u, humanoid.Health);
        Assert.Equal(100u, beast.Health);
    }

    [Fact]
    public void AMismatchedTarget_ReachingTheHitTable_IsImmuneAndTakesNothing()
    {
        using var s = new Scene(installChecks: false);
        Creature humanoid = s.AddCreature(HumanoidType, 3);
        Creature undead = s.AddCreature(UndeadType, 4);

        Assert.Equal(SpellCastResult.CastOk, s.Cast(ShackleUndead, humanoid));
        SpellTargetOutcome miss = Assert.Single(s.Seen.Seen, o => ReferenceEquals(o.Target, humanoid));
        Assert.Equal(SpellMissInfo.Immune, miss.Miss);
        Assert.Equal(100u, humanoid.Health);

        Assert.Equal(SpellCastResult.CastOk, s.Cast(ShackleUndead, undead));
        SpellTargetOutcome hit = Assert.Single(s.Seen.Seen, o => ReferenceEquals(o.Target, undead));
        Assert.Equal(SpellMissInfo.None, hit.Miss);
        Assert.True(undead.Health < 100);
    }

    [Theory]
    [InlineData(false, 100u, 100u)]
    [InlineData(true, 100u, 85u)]
    public void TheGroundingMagnet_StillFollowsTheTypeRules(bool magnetHasGrounding, uint victimHealth, uint magnetHealth)
    {
        using var s = new Scene(installChecks: false);
        Player victim = s.Kit.AddPlayer(2, 5).Player;
        Player magnet = s.Kit.AddPlayer(3, 6).Player;
        victim.MaxHealth = victim.Health = magnet.MaxHealth = magnet.Health = 100;
        Assert.Equal(SpellCastResult.CastOk, s.Kit.System.CastSpell(magnet, Protection, SpellCastTargets.ForUnit(victim.Guid), triggered: true));
        if (magnetHasGrounding)
        {
            Assert.Equal(SpellCastResult.CastOk, s.Kit.System.CastSpell(magnet, Grounding, SpellCastTargets.ForSelf(), triggered: true));
        }

        Assert.Equal(SpellCastResult.CastOk, s.Cast(UndeadOnly, victim));

        // A humanoid magnet is a legal redirect target of an undead-only spell only while it carries Grounding's 8179 holder.
        Assert.Equal(!magnetHasGrounding, s.Kit.System.HasAura(victim, Protection));
        Assert.Equal(victimHealth, victim.Health);
        Assert.Equal(magnetHealth, magnet.Health);
    }

    [Fact]
    public void ALevelCappedSpell_OnATargetAboveTheCap_FailsHighlevel_AtTheCapSucceeds()
    {
        using var s = new Scene(installChecks: true);
        Creature above = s.AddCreature(HumanoidType, 3, level: 31);
        Creature at = s.AddCreature(HumanoidType, 4, level: 30);
        Creature below = s.AddCreature(HumanoidType, 5, level: 12);

        Assert.Equal(SpellCastResult.Highlevel, s.Cast(Capped, above));
        Assert.Equal(SpellCastResult.CastOk, s.Cast(Capped, at));
        Assert.Equal(SpellCastResult.CastOk, s.Cast(Capped, below));
    }

    [Fact]
    public void AZeroMaxTargetLevel_MeansNoCap()
    {
        using var s = new Scene(installChecks: true);
        Creature boss = s.AddCreature(HumanoidType, 3, level: 63);

        Assert.Equal(SpellCastResult.CastOk, s.Cast(Uncapped, boss));
    }

    [Fact]
    public void ALevelCappedSpell_OnAPlayerAboveTheCap_FailsHighlevel()
    {
        using var s = new Scene(installChecks: true);
        Player target = s.Kit.AddPlayer(2, 5).Player;
        target.MaxHealth = target.Health = 100;
        target.Level = 45;

        Assert.Equal(SpellCastResult.Highlevel, s.Cast(Capped, target));
        target.Level = 30;
        Assert.Equal(SpellCastResult.CastOk, s.Cast(Capped, target));
    }
}

using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules.Immunity;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Threat;

/// <summary>
/// SPELL_EFFECT_ATTACK_ME, SPELL_AURA_MOD_TAUNT, SPELL_AURA_MOD_TOTAL_THREAT and SPELL_EFFECT_MODIFY_THREAT_PERCENT against a real spell
/// system and a real creature map (vmangos Spell::EffectTaunt SpellEffects.cpp:3356-3395, Aura::HandleModTaunt SpellAuras.cpp:3939-3967,
/// Aura::HandleAuraModTotalThreat :3920-3937, Spell::EffectModifyThreatPercent :5638-5646).
/// </summary>
public sealed class TauntTests
{
    private const uint TauntSpell = 920001;
    private const uint ImmuneToTaunt = 920002;
    private const uint FadeSpell = 920003;
    private const uint HalfThreat = 920004;

    private sealed class Arena : IDisposable
    {
        public SpellTestKit Kit { get; } = new(
            Spell(TauntSpell, Effect(SpellEffectName.AttackMe, 0, SpellImplicitTarget.UnitEnemy),
                Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.ModTaunt)) with
            {
                Duration = new SpellDuration(3000, 0, 3000),
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
                SpellVisual = 1,
            },
            // vmangos Creature.cpp:444-448: CREATURE_IMMUNITY_TAUNT is an effect immunity (ATTACK_ME) plus a state immunity (MOD_TAUNT)
            Spell(ImmuneToTaunt,
                Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.EffectImmunity, misc: (int)SpellEffectName.AttackMe),
                Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.StateImmunity, misc: (int)AuraType.ModTaunt)) with
            {
                Duration = new SpellDuration(-1, 0, -1),
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
                SpellVisual = 1,
            },
            Spell(FadeSpell, Effect(SpellEffectName.ApplyAura, -500, SpellImplicitTarget.UnitCaster, AuraType.ModTotalThreat)) with
            {
                Duration = new SpellDuration(10000, 0, 10000),
                SpellVisual = 1,
            },
            Spell(HalfThreat, Effect(SpellEffectName.ModifyThreatPercent, -50, SpellImplicitTarget.UnitEnemy)) with
            {
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
            });

        public Player Tank { get; }

        public Player Dps { get; }

        public Player Healer { get; }

        public Creature Wolf { get; }

        public CreatureMapSystem System { get; }

        public Map Map => Kit.World.GetMap(0);

        public Arena()
        {
            (Tank, _) = Kit.AddPlayer(1, 0, 0);
            (Dps, _) = Kit.AddPlayer(2, 0, 1);
            (Healer, _) = Kit.AddPlayer(3, 0, 2);
            var template = CreatureTestSupport.Template(CreatureTestSupport.WolfEntry, t =>
            {
                t.MinLevelHealth = 100000;
                t.MaxLevelHealth = 100000;
            });
            System = new CreatureMapSystem(Map, CreatureTestSupport.Content([template], [CreatureTestSupport.Spawn(77, CreatureTestSupport.WolfEntry, 3, 0)]),
                null, random: new Random(1), aiServices: new CreatureAiServices { Hostility = new AlwaysHostile() });
            Map.AddUpdater(System);
            Kit.World.RunTick(50);
            Wolf = Assert.Single(System.Creatures);
            Wolf.AI!.CombatMovement = false;
            Kit.System.Units = new FixedResolver(Wolf);
            Kit.System.ApplicationRules.Add(new ImmunityApplicationRule()); // the world feature installs it first (SpellRulesFeature)
        }

        /// <summary>The dps pulls with threat 100 and the tank has 20: the wolf attacks the dps.</summary>
        public void Pull()
        {
            Map.Combat.DealDamage(Dps, Wolf, 100, direct: false);
            Map.Combat.DealDamage(Tank, Wolf, 20, direct: false);
            Assert.True(System.SelectHostileTarget(Wolf));
            Assert.Same(Dps, Wolf.Combat.Victim);
            Assert.Equal(100f, Wolf.Combat.Threat.GetThreat(Dps));
            Assert.Equal(20f, Wolf.Combat.Threat.GetThreat(Tank));
        }

        public void Cast(Unit caster, uint spell) => Kit.System.CastSpell(caster, spell, SpellCastTargets.ForUnit(Wolf.Guid), triggered: true);

        public void Dispose() => Kit.Dispose();
    }

    private sealed class FixedResolver(Unit unit) : ISpellUnitResolver
    {
        private readonly MapPlayerResolver _players = new();

        public Unit? Find(Unit reference, ObjectGuid guid) => guid == unit.Guid ? unit : _players.Find(reference, guid);
    }

    [Fact]
    public void TheModuleHandlesTheThreatEffectsAndAuras()
    {
        using var arena = new Arena();

        Assert.Contains(typeof(ThreatEffects), arena.Kit.System.Modules);
        Assert.Contains(typeof(ThreatAuras), arena.Kit.System.Modules);
        Assert.True(arena.Kit.System.HasEffectHandler(SpellEffectName.AttackMe));
        Assert.True(arena.Kit.System.HasEffectHandler(SpellEffectName.ModifyThreatPercent));
        Assert.True(arena.Kit.System.HasAuraHandler(AuraType.ModTaunt));
        Assert.True(arena.Kit.System.HasAuraHandler(AuraType.ModTotalThreat));
    }

    [Fact]
    public void ATaunt_TakesTheCreatureAtOnce_AndGivesTheTaunterTheVictimsThreat()
    {
        using var arena = new Arena();
        arena.Pull();

        arena.Cast(arena.Tank, TauntSpell);

        Assert.Equal(100f, arena.Wolf.Combat.Threat.GetThreat(arena.Tank)); // SpellEffects.cpp:3376
        Assert.Same(arena.Tank, arena.Wolf.Combat.Victim);
        Assert.Same(arena.Tank, arena.Wolf.Combat.Threat.CurrentVictim);
        Assert.True(arena.Wolf.Combat.Threat.HasTauntCasters);
    }

    [Fact]
    public void TheTauntThreatIsPermanent_AndTheTaunterKeepsTheCreatureWhenTheAuraExpires()
    {
        using var arena = new Arena();
        arena.Pull();
        arena.Cast(arena.Tank, TauntSpell);

        arena.Kit.Advance(3500);

        Assert.False(arena.Wolf.Combat.Threat.HasTauntCasters);
        Assert.Equal(100f, arena.Wolf.Combat.Threat.GetThreat(arena.Tank));
        Assert.True(arena.System.SelectHostileTarget(arena.Wolf));
        Assert.Same(arena.Tank, arena.Wolf.Combat.Victim); // 100 is not above 110 % of 100
    }

    [Fact]
    public void ATauntOnACreatureAlreadyAttackingTheCaster_ChangesNothing()
    {
        using var arena = new Arena();
        arena.Pull();
        arena.Cast(arena.Tank, TauntSpell);
        arena.Wolf.Combat.Threat.AddThreat(arena.Tank, 50); // 150 now

        arena.Cast(arena.Tank, TauntSpell);

        Assert.Equal(150f, arena.Wolf.Combat.Threat.GetThreat(arena.Tank));
    }

    [Fact]
    public void TheLatestTaunterWins_AndTheEarlierOneTakesOverWhenTheLaterAuraEnds()
    {
        using var arena = new Arena();
        arena.Pull();
        arena.Cast(arena.Tank, TauntSpell);
        arena.Kit.Advance(1000);
        arena.Cast(arena.Healer, TauntSpell);
        Assert.Same(arena.Healer, arena.Wolf.Combat.Victim);

        arena.Kit.System.RemoveAurasByCaster(arena.Wolf, TauntSpell, arena.Healer.Guid);
        Assert.True(arena.System.SelectHostileTarget(arena.Wolf));
        Assert.Same(arena.Tank, arena.Wolf.Combat.Victim);

        arena.Kit.System.RemoveAurasByCaster(arena.Wolf, TauntSpell, arena.Tank.Guid);
        Assert.False(arena.Wolf.Combat.Threat.HasTauntCasters);
        Assert.True(arena.System.SelectHostileTarget(arena.Wolf));
        Assert.NotNull(arena.Wolf.Combat.Victim);
    }

    [Fact]
    public void ATauntedFearedCreature_GetsTheThreat_ButIsNotRetargeted()
    {
        using var arena = new Arena();
        arena.Pull();
        arena.Wolf.UnitFlags |= UnitFlags.Fleeing;

        arena.Cast(arena.Tank, TauntSpell);

        Assert.Equal(100f, arena.Wolf.Combat.Threat.GetThreat(arena.Tank));
        Assert.Same(arena.Dps, arena.Wolf.Combat.Victim);
    }

    [Fact]
    public void ACreatureImmuneToTheTauntEffect_IgnoresEffectAndAura()
    {
        using var arena = new Arena();
        arena.Pull();
        arena.Kit.System.CastSpell(arena.Wolf, ImmuneToTaunt, SpellCastTargets.ForUnit(arena.Wolf.Guid), triggered: true); // the unit carries its own immunity aura (vmangos ApplySpellImmune)

        arena.Cast(arena.Tank, TauntSpell);

        Assert.Equal(20f, arena.Wolf.Combat.Threat.GetThreat(arena.Tank));
        Assert.Same(arena.Dps, arena.Wolf.Combat.Victim);
        Assert.False(arena.Wolf.Combat.Threat.HasTauntCasters);
    }

    [Fact]
    public void ATaunterWhoDies_IsSkipped_AndTheThreatListTakesOver()
    {
        using var arena = new Arena();
        arena.Pull();
        arena.Cast(arena.Tank, TauntSpell);
        arena.Map.Combat.Kill(arena.Healer, arena.Tank); // death drops the dead unit from every threat list (MapCombat.Death)

        Assert.True(arena.System.SelectHostileTarget(arena.Wolf));
        Assert.Same(arena.Dps, arena.Wolf.Combat.Victim);
    }

    [Fact]
    public void ATauntOnAPlayer_IsIgnoredByTheThreatSide()
    {
        using var arena = new Arena();
        arena.Pull();

        arena.Kit.System.CastSpell(arena.Tank, TauntSpell, SpellCastTargets.ForUnit(arena.Dps.Guid), triggered: true);

        Assert.True(arena.Dps.Combat.Threat.IsEmpty);
        Assert.Same(arena.Dps, arena.Wolf.Combat.Victim);
    }

    [Fact]
    public void FadeFoldsATemporaryModifierIntoEveryListOnce_AndRemovalTakesItOut()
    {
        using var arena = new Arena();
        arena.Pull();
        arena.Wolf.Combat.Threat.AddThreat(arena.Tank, 900); // 920

        arena.Kit.System.CastSpell(arena.Tank, FadeSpell, SpellCastTargets.ForUnit(arena.Tank.Guid), triggered: true);
        Assert.Equal(420f, arena.Wolf.Combat.Threat.GetThreat(arena.Tank));

        arena.Kit.System.CastSpell(arena.Tank, FadeSpell, SpellCastTargets.ForUnit(arena.Tank.Guid), triggered: true); // refresh: no stacking
        Assert.Equal(420f, arena.Wolf.Combat.Threat.GetThreat(arena.Tank));

        arena.Kit.System.RemoveAuras(arena.Tank, FadeSpell);
        Assert.Equal(920f, arena.Wolf.Combat.Threat.GetThreat(arena.Tank));
    }

    [Fact]
    public void ModifyThreatPercent_ScalesTheCastersEntry()
    {
        using var arena = new Arena();
        arena.Pull();
        arena.Wolf.Combat.Threat.AddThreat(arena.Tank, 80); // 100

        arena.Cast(arena.Tank, HalfThreat);

        Assert.Equal(50f, arena.Wolf.Combat.Threat.GetThreat(arena.Tank));
        Assert.Equal(100f, arena.Wolf.Combat.Threat.GetThreat(arena.Dps)); // only the caster's entry
    }
}

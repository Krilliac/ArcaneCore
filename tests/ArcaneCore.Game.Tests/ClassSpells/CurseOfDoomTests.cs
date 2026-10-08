using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Spells.Warlock;
using ArcaneCore.Game.Tests.Pets;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.ClassSpells;

/// <summary>
/// Curse of Doom (vmangos SpellAuras.cpp:5921-5924 and Spell.cpp:7584-7592) and SPELL_EFFECT_SUMMON_DEMON (SpellEffects.cpp:5796-5819). Spell data
/// follow the build 5875 rows: 603 (warlock, PeriodicDamage 3200 after 60 s, 60 s), 18662 Curse of Doom Effect (SUMMON_DEMON of creature 11859 at
/// TARGET_LOCATION_CASTER_DEST, 15 s). The Doomguard is the pet kit's wild test creature.
/// </summary>
public sealed class CurseOfDoomTests
{
    private const uint Doomguard = PetTestKit.WildEntry;

    /// <summary>Answers <see cref="Random.Next(int, int)"/> for the 0..9 summon roll with a fixed value; everything else stays random.</summary>
    private sealed class SummonRoll(int roll) : Random(11)
    {
        public int Rolls { get; private set; }

        public override int Next(int minValue, int maxValue)
        {
            if (minValue == 0 && maxValue == CurseOfDoomScript.SummonOneIn)
            {
                Rolls++;
                return roll;
            }

            return base.Next(minValue, maxValue);
        }
    }

    private static IEnumerable<SpellInfo> Spells() =>
    [
        Spell(CurseOfDoomScript.CurseOfDoom, Effect(SpellEffectName.ApplyAura, 3200, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 60_000)) with
        {
            School = SpellSchool.Shadow,
            SpellFamilyName = 5,
            SpellFamilyFlags = 0x280000000,
            Duration = new SpellDuration(60_000, 0, 60_000),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        },
        Spell(CurseOfDoomScript.CurseOfDoomEffect, Effect(SpellEffectName.SummonDemon, 0, SpellImplicitTarget.LocationCasterDest, misc: (int)Doomguard)) with
        {
            School = SpellSchool.Shadow,
            Duration = new SpellDuration(15_000, 0, 15_000),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        },
    ];

    private static (PetTestKit Kit, Player Warlock, Player Victim) Kit(int roll, uint victimHealth)
    {
        var kit = new PetTestKit(Spells());
        (Player warlock, _) = kit.AddPlayer(1, 7, 8);
        (Player victim, _) = kit.AddPlayer(2, 12, 8);
        warlock.Level = 40;
        victim.MaxHealth = 10_000;
        victim.Health = victimHealth;
        kit.Spells.System.Random = new SummonRoll(roll);
        Assert.Equal(SpellCastResult.CastOk, kit.Spells.System.CastSpell(warlock, CurseOfDoomScript.CurseOfDoom, SpellCastTargets.ForUnit(victim.Guid), triggered: true));
        return (kit, warlock, victim);
    }

    [Fact]
    public void ATickThatKills_SummonsTheDoomguard_OneTimeInTen()
    {
        (PetTestKit kit, Player warlock, Player victim) = Kit(roll: 0, victimHealth: 500);
        using PetTestKit owned = kit;

        kit.Spells.Advance(60_000, step: 1000);

        Assert.False(victim.IsAlive);
        Creature doomguard = Assert.Single(kit.Creatures.Creatures, c => c.Entry == Doomguard);
        Assert.Equal(SummonKind.Wild, doomguard.Summon!.Kind);
        Assert.Equal(CurseOfDoomScript.CurseOfDoomEffect, doomguard.GetUInt32(UpdateFields.UnitCreatedBySpell));
        Assert.Equal(40, doomguard.Level);                       // the caster's level (SpellEffects.cpp:5813)
        Assert.Equal((7f, 8f), (doomguard.X, doomguard.Y));      // TARGET_LOCATION_CASTER_DEST: at the warlock
        Assert.Equal(14u, doomguard.FactionTemplate);            // its own (hostile) faction
        Assert.InRange(doomguard.Summon.RemainingMs, 13_000, 15_000);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    public void ATickThatKills_OtherwiseSummonsNothing(int roll)
    {
        (PetTestKit kit, _, Player victim) = Kit(roll, victimHealth: 500);
        using PetTestKit owned = kit;

        kit.Spells.Advance(60_000, step: 1000);

        Assert.False(victim.IsAlive);
        Assert.DoesNotContain(kit.Creatures.Creatures, c => c.Entry == Doomguard);
    }

    [Fact]
    public void ATickThatDoesNotKill_DoesNotRollAtAll()
    {
        (PetTestKit kit, _, Player victim) = Kit(roll: 0, victimHealth: 9_000);
        using PetTestKit owned = kit;

        kit.Spells.Advance(60_000, step: 1000);

        Assert.True(victim.IsAlive);
        Assert.Equal(0, ((SummonRoll)kit.Spells.System.Random).Rolls);
        Assert.DoesNotContain(kit.Creatures.Creatures, c => c.Entry == Doomguard);
    }

    [Fact]
    public void CurseOfDoom_CannotTargetAPlayer()
    {
        using var kit = new PetTestKit(Spells());
        SpellScriptDispatcher.Install(kit.Spells.System, new SpellScriptRegistry([new CurseOfDoomScript()]));
        (Player warlock, _) = kit.AddPlayer(1, 7, 8);
        (Player victim, _) = kit.AddPlayer(2, 12, 8);

        Assert.Equal(SpellCastResult.TargetIsPlayer,
            kit.Spells.System.CastSpell(warlock, CurseOfDoomScript.CurseOfDoom, SpellCastTargets.ForUnit(victim.Guid), triggered: true));
        Assert.False(kit.Spells.System.HasAura(victim, CurseOfDoomScript.CurseOfDoom));
    }
}

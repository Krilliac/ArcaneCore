using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Rogue;

/// <summary>
/// The aura-interrupt machinery (vmangos Unit.cpp:3735-3751, Spell.cpp:3440-3456, 3697-3714, 1622-1626, 1645-1668,
/// 1893-1897, 8301-8331, Unit.cpp:660-670). Every negative assertion sits next to the positive one in the same
/// fixture, so a no-op implementation fails one of the two.
/// </summary>
public sealed class AuraInterruptTests
{
    private const uint Stealth = 910001;
    private const uint Strike = 910002;
    private const uint Sprint = 910003;
    private const uint ActionAura = 910004;
    private const uint AttackingAura = 910005;
    private const uint HostileAura = 910006;
    private const uint Heal = 910007;
    private const uint Debuff = 910008;
    private const uint Sap = 910009;
    private const uint VanishLike = 910010;
    private const uint Invis = 910011;
    private const uint InvisPotionUser = 910012;
    private const uint DamageAura = 910013;
    private const uint LootingAura = 910014;
    private const uint BoxOpener = 910015;
    private const uint ImprovedSapR1 = 14076;
    private const uint ImprovedSapR2 = 14094;
    private const uint ImprovedSapR3 = 14095;
    private const uint ShadowmeldLike = 910016;
    private const uint CamouflageLike = 910017;

    private static SpellInfo Instant(SpellInfo spell) => spell with
    {
        RangeIndex = 4,
        Range = new SpellRange(0, 30),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellInfo SelfAura(uint id, AuraType aura, uint interrupt, uint dispel = 0) => Spell(id, Effect(SpellEffectName.ApplyAura, 0, aura: aura)) with
    {
        AuraInterruptFlags = (SpellAuraInterruptFlags)interrupt,
        Duration = new SpellDuration(60_000, 0, 60_000),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
        Dispel = dispel,
    };

    private static (SpellTestKit Kit, Player Rogue, Player Enemy) Setup(FixedRules? rules = null)
    {
        var kit = new SpellTestKit(
            SelfAura(Stealth, AuraType.ModStealth, AuraInterruptMask.StealthFamily, StealthBreakRules.DispelStealth),
            Instant(Spell(Strike, Effect(SpellEffectName.SchoolDamage, 5, SpellImplicitTarget.UnitEnemy))),
            Spell(Sprint, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
            {
                AttributesEx = (SpellAttributesEx)StealthBreakRules.AttributesExAllowWhileStealthed,
                Duration = new SpellDuration(8000, 0, 8000),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            SelfAura(ActionAura, AuraType.Dummy, AuraInterruptMask.Action),
            SelfAura(AttackingAura, AuraType.Dummy, AuraInterruptMask.Attacking),
            SelfAura(HostileAura, AuraType.Dummy, AuraInterruptMask.HostileActionReceived),
            Instant(Spell(Heal, Effect(SpellEffectName.Heal, 5, SpellImplicitTarget.UnitFriend))),
            Instant(Spell(Debuff, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.ModRoot))) with
            {
                Duration = new SpellDuration(5000, 0, 5000),
                SpellVisual = 1,
            },
            Instant(Spell(Sap, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.ModStun))) with
            {
                SpellIconId = StealthBreakRules.IconSap,
                Duration = new SpellDuration(5000, 0, 5000),
                SpellVisual = 1,
            },
            Instant(Spell(VanishLike, Effect(SpellEffectName.SchoolDamage, 1, SpellImplicitTarget.UnitEnemy))) with { SpellIconId = StealthBreakRules.IconVanish },
            Instant(Spell(ShadowmeldLike, Effect(SpellEffectName.SchoolDamage, 1, SpellImplicitTarget.UnitEnemy))) with { SpellIconId = StealthBreakRules.IconShadowmeld },
            Instant(Spell(CamouflageLike, Effect(SpellEffectName.SchoolDamage, 1, SpellImplicitTarget.UnitEnemy))) with { SpellIconId = StealthBreakRules.IconCamouflage },
            SelfAura(Invis, AuraType.ModInvisibility, AuraInterruptMask.Action, StealthBreakRules.DispelInvisibility),
            Spell(InvisPotionUser, Effect(SpellEffectName.Dummy, 0)) with
            {
                AttributesEx2 = (SpellAttributesEx2)StealthBreakRules.AttributesEx2AllowWhileInvisible,
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            SelfAura(DamageAura, AuraType.Dummy, AuraInterruptMask.Damage),
            SelfAura(LootingAura, AuraType.Dummy, AuraInterruptMask.Looting),
            Spell(BoxOpener, Effect(SpellEffectName.Dummy, 0)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 },
            SelfAura(ImprovedSapR1, AuraType.Dummy, 0),
            SelfAura(ImprovedSapR2, AuraType.Dummy, 0),
            SelfAura(ImprovedSapR3, AuraType.Dummy, 0));
        kit.System.RegisterAura(AuraType.ModStealth, new AuraHandler(null, null));
        kit.System.RegisterAura(AuraType.ModInvisibility, new AuraHandler(null, null));
        var relations = new FakeRelations();
        kit.System.Relations = relations;
        if (rules is not null)
        {
            kit.System.CombatRules = rules;
        }

        (Player rogue, _) = kit.AddPlayer(1);
        (Player enemy, _) = kit.AddPlayer(2, 2);
        relations.Hostile.Add(enemy.Guid);
        kit.Spellbook.Teach(rogue, Strike, Sprint, Heal, Debuff, Sap, VanishLike, ShadowmeldLike, CamouflageLike, InvisPotionUser, BoxOpener, ActionAura);
        kit.Spellbook.Teach(enemy, Strike, Heal);
        return (kit, rogue, enemy);
    }

    private static void Give(SpellTestKit kit, Unit unit, uint spell)
        => kit.System.CastSpell(unit, spell, SpellCastTargets.ForUnit(unit.Guid), triggered: true);

    private static void Request(SpellTestKit kit, Player caster, uint spell, Unit? target = null)
    {
        kit.Advance(2000); // clears every cooldown between requests
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, spell,
            target is null ? SpellCastTargets.ForSelf() : SpellCastTargets.ForUnit(target.Guid)));
    }

    [Fact]
    public void CastStart_PlainAbilityRemovesStealth_AllowWhileStealthedSpellDoesNot()
    {
        (SpellTestKit kit, Player rogue, Player enemy) = Setup();
        using (kit)
        {
            Give(kit, rogue, Stealth);
            Assert.True(kit.System.HasAura(rogue, Stealth));

            Request(kit, rogue, Sprint);
            Assert.True(kit.System.HasAura(rogue, Stealth));
            Assert.True(kit.System.HasAura(rogue, Sprint));

            Request(kit, rogue, Strike, enemy);
            Assert.False(kit.System.HasAura(rogue, Stealth));
        }
    }

    [Fact]
    public void TriggeredCast_NeverRemovesStealth_ExplicitCastDoes()
    {
        (SpellTestKit kit, Player rogue, Player enemy) = Setup();
        using (kit)
        {
            Give(kit, rogue, Stealth);
            kit.System.CastSpell(rogue, Strike, SpellCastTargets.ForUnit(enemy.Guid), triggered: true);
            Assert.True(kit.System.HasAura(rogue, Stealth));

            Request(kit, rogue, Strike, enemy);
            Assert.False(kit.System.HasAura(rogue, Stealth));
        }
    }

    [Fact]
    public void CastStart_RemovesActionAuras_ButNotTheAuraOfTheSpellBeingCast()
    {
        (SpellTestKit kit, Player rogue, Player enemy) = Setup();
        using (kit)
        {
            Give(kit, rogue, ActionAura);
            Give(kit, rogue, AttackingAura);
            Request(kit, rogue, ActionAura); // recasting itself: the dispatcher passes the spell as 'except'
            Assert.True(kit.System.HasAura(rogue, ActionAura));
            Assert.True(kit.System.HasAura(rogue, AttackingAura)); // a self cast is a positive target: ATTACKING stays

            Request(kit, rogue, Strike, enemy);
            Assert.False(kit.System.HasAura(rogue, ActionAura));
            Assert.False(kit.System.HasAura(rogue, AttackingAura)); // negative target: ATTACKING goes at completion
        }
    }

    [Fact]
    public void PositiveCastOnAFriend_RemovesActionButNotAttackingAuras()
    {
        (SpellTestKit kit, Player rogue, _) = Setup();
        using (kit)
        {
            Give(kit, rogue, ActionAura);
            Give(kit, rogue, AttackingAura);
            Request(kit, rogue, Heal, rogue);
            Assert.False(kit.System.HasAura(rogue, ActionAura));
            Assert.True(kit.System.HasAura(rogue, AttackingAura));
        }
    }

    [Fact]
    public void AurasWithoutTheInterruptBits_AreUntouched_WhileFlaggedOnesGoInTheSameCast()
    {
        (SpellTestKit kit, Player rogue, Player enemy) = Setup();
        using (kit)
        {
            Give(kit, rogue, ActionAura);
            Give(kit, rogue, ImprovedSapR1); // flags 0
            Request(kit, rogue, Strike, enemy);
            Assert.False(kit.System.HasAura(rogue, ActionAura));
            Assert.True(kit.System.HasAura(rogue, ImprovedSapR1));
        }
    }

    [Fact]
    public void CastOnAGameObject_AddsTheLootingBit()
    {
        (SpellTestKit kit, Player rogue, _) = Setup();
        using (kit)
        {
            Give(kit, rogue, LootingAura);
            Request(kit, rogue, BoxOpener); // not a game object target: the looting aura stays
            Assert.True(kit.System.HasAura(rogue, LootingAura));

            kit.Advance(2000);
            var targets = new SpellCastTargets { Mask = SpellCastTargetFlags.GameObject, GameObject = ObjectGuid.WithEntry(HighGuid.GameObject, 5, 6) };
            kit.System.CastSpell(rogue, BoxOpener, targets, triggered: false);
            Assert.False(kit.System.HasAura(rogue, LootingAura));
        }
    }

    [Fact]
    public void HostileDamagingHit_RemovesHostileActionAurasFromTheTarget_ANonDamagingDebuffDoesNot()
    {
        (SpellTestKit kit, Player rogue, Player enemy) = Setup();
        using (kit)
        {
            Give(kit, enemy, HostileAura);
            Request(kit, rogue, Debuff, enemy); // no damage: vmangos 'if (m_damage)' is false
            Assert.True(kit.System.HasAura(enemy, HostileAura));

            Request(kit, rogue, Strike, enemy);
            Assert.False(kit.System.HasAura(enemy, HostileAura));
        }
    }

    [Fact]
    public void HostileMiss_StillRemovesHostileActionAuras_AFriendlyHealDoesNot()
    {
        (SpellTestKit kit, Player rogue, Player enemy) = Setup(new FixedRules { Miss = SpellMissInfo.Miss });
        using (kit)
        {
            Give(kit, enemy, HostileAura);
            Request(kit, rogue, Heal, rogue);
            Assert.True(kit.System.HasAura(enemy, HostileAura));

            Request(kit, rogue, Strike, enemy);
            Assert.False(kit.System.HasAura(enemy, HostileAura));
        }
    }

    [Fact]
    public void HostileHit_RemovesTheTargetsStealth_AFriendlyCastNeverDoes()
    {
        (SpellTestKit kit, Player rogue, Player enemy) = Setup();
        using (kit)
        {
            Give(kit, enemy, Stealth);
            Request(kit, rogue, Heal, rogue);
            Assert.True(kit.System.HasAura(enemy, Stealth));

            Request(kit, rogue, Debuff, enemy);
            Assert.False(kit.System.HasAura(enemy, Stealth)); // any hostile hit that is an action breaks the victim's stealth
        }
    }

    [Fact]
    public void SelfDamage_BreaksDamageAuras_AtBuild5875_ZeroDamageDoesNot()
    {
        (SpellTestKit kit, Player rogue, _) = Setup();
        using (kit)
        {
            Give(kit, rogue, DamageAura);
            kit.System.OnDamageTaken(rogue, rogue, 0, periodic: false);
            Assert.True(kit.System.HasAura(rogue, DamageAura));

            // Patch 1.7.0: self-inflicted damage breaks stealth too (vmangos Unit.cpp:660-670, SKIP_STEALTH false).
            kit.System.OnDamageTaken(rogue, rogue, 5, periodic: false);
            Assert.False(kit.System.HasAura(rogue, DamageAura));
        }
    }

    [Fact]
    public void Dispatcher_ExceptSpellId_IsHonoured_AndSkipFlagsFilterByDispelType()
    {
        (SpellTestKit kit, Player rogue, _) = Setup();
        using (kit)
        {
            Give(kit, rogue, Stealth);
            Give(kit, rogue, Invis);
            Give(kit, rogue, ActionAura);

            // Stealth does not carry the Action bit... it does (0x3C07 includes 0x4): skip it and the invisibility, except ActionAura.
            Assert.Equal(0, kit.System.RemoveAurasWithInterruptFlags(rogue, AuraInterruptMask.Action, ActionAura, skipStealth: true, skipInvisibility: true));
            Assert.True(kit.System.HasAura(rogue, ActionAura));
            Assert.True(kit.System.HasAura(rogue, Stealth));
            Assert.True(kit.System.HasAura(rogue, Invis));

            Assert.Equal(2, kit.System.RemoveAurasWithInterruptFlags(rogue, AuraInterruptMask.Action, 0, skipStealth: true, skipInvisibility: false));
            Assert.False(kit.System.HasAura(rogue, Invis));
            Assert.False(kit.System.HasAura(rogue, ActionAura));
            Assert.True(kit.System.HasAura(rogue, Stealth));
        }
    }

    [Fact]
    public void InvisibilitySurvivesAnAllowWhileInvisibleCast_AndBreaksOnAPlainOne()
    {
        (SpellTestKit kit, Player rogue, Player enemy) = Setup();
        using (kit)
        {
            Give(kit, rogue, Invis);
            Request(kit, rogue, InvisPotionUser);
            Assert.True(kit.System.HasAura(rogue, Invis));

            Request(kit, rogue, Strike, enemy);
            Assert.False(kit.System.HasAura(rogue, Invis));
        }
    }

    [Theory]
    [InlineData(VanishLike)]
    [InlineData(ShadowmeldLike)]
    [InlineData(CamouflageLike)]
    public void IconExemptions_KeepStealth_WhileAnOrdinaryAbilityBreaksIt(uint exempt)
    {
        (SpellTestKit kit, Player rogue, Player enemy) = Setup();
        using (kit)
        {
            Give(kit, rogue, Stealth);
            Request(kit, rogue, exempt, enemy);
            Assert.True(kit.System.HasAura(rogue, Stealth));

            Request(kit, rogue, Strike, enemy);
            Assert.False(kit.System.HasAura(rogue, Stealth));
        }
    }

    [Theory]
    [InlineData(ImprovedSapR1, 30)]
    [InlineData(ImprovedSapR2, 60)]
    [InlineData(ImprovedSapR3, 90)]
    [InlineData(0u, 0)]
    public void ImprovedSap_RollsTheRankChance_AndWithoutItSapAlwaysBreaksStealth(uint rank, int expectedChance)
    {
        SpellInfo sap = new() { Id = Sap, SpellIconId = StealthBreakRules.IconSap };
        var rolled = new List<int>();
        bool Roll(int chance)
        {
            rolled.Add(chance);
            return true;
        }

        bool remove = StealthBreakRules.ShouldRemoveStealthAuras(sap, false, true, id => id == rank, Roll);

        if (expectedChance == 0)
        {
            Assert.True(remove);
            Assert.Empty(rolled);
        }
        else
        {
            Assert.Equal([expectedChance], rolled);
            Assert.False(remove); // the roll succeeded: stealth is kept
        }

        // A non-player Sap, or a Sap-less icon, never rolls.
        rolled.Clear();
        Assert.True(StealthBreakRules.ShouldRemoveStealthAuras(sap, false, false, _ => true, Roll));
        Assert.True(StealthBreakRules.ShouldRemoveStealthAuras(sap with { SpellIconId = 1 }, false, true, _ => true, Roll));
        Assert.Empty(rolled);
    }

    [Fact]
    public void ImprovedSapRank3_KeepsStealthAbout90Percent_OverTenThousandRolls()
    {
        var random = new Random(12345);
        SpellInfo sap = new() { Id = Sap, SpellIconId = StealthBreakRules.IconSap };
        int kept = 0;
        const int trials = 10_000;
        for (int i = 0; i < trials; i++)
        {
            if (!StealthBreakRules.ShouldRemoveStealthAuras(sap, false, true, id => id == ImprovedSapR3, chance => random.Next(100) < chance))
            {
                kept++;
            }
        }

        Assert.InRange(kept, 8900, 9100);
    }

    [Fact]
    public void ImprovedSap_EndToEnd_UsesTheSystemRandom()
    {
        (SpellTestKit kit, Player rogue, Player enemy) = Setup();
        using (kit)
        {
            Give(kit, rogue, ImprovedSapR1);
            Give(kit, rogue, Stealth);

            kit.System.Random = new FixedRandom(10); // 10 < 30: the roll succeeds, stealth is kept
            Request(kit, rogue, Sap, enemy);
            Assert.True(kit.System.HasAura(rogue, Stealth));

            kit.System.Random = new FixedRandom(80); // 80 >= 30: stealth is removed
            kit.System.RemoveAuras(enemy, Sap);
            Request(kit, rogue, Sap, enemy);
            Assert.False(kit.System.HasAura(rogue, Stealth));
        }
    }

    [Fact]
    public void ImprovedSapRollPerPhase_RollsAtCastStartAndAgainAtCompletion_LikeTheLiteralVmangosCode()
    {
        (SpellTestKit kit, Player rogue, Player enemy) = Setup();
        using (kit)
        {
            Give(kit, rogue, ImprovedSapR1);
            kit.System.ImprovedSapRollPerPhase = true;

            // roll 1 keeps (10 < 30), roll 2 removes (80 >= 30): the second phase breaks stealth
            Give(kit, rogue, Stealth);
            kit.System.Random = new SequenceRandom(10, 80);
            Request(kit, rogue, Sap, enemy);
            Assert.False(kit.System.HasAura(rogue, Stealth));

            // both rolls keep: stealth survives
            Give(kit, rogue, Stealth);
            kit.System.RemoveAuras(enemy, Sap);
            kit.System.Random = new SequenceRandom(10, 10);
            Request(kit, rogue, Sap, enemy);
            Assert.True(kit.System.HasAura(rogue, Stealth));

            // the default is one roll per cast: the same first roll alone decides
            kit.System.ImprovedSapRollPerPhase = false;
            kit.System.RemoveAuras(enemy, Sap);
            kit.System.Random = new SequenceRandom(10, 80);
            Request(kit, rogue, Sap, enemy);
            Assert.True(kit.System.HasAura(rogue, Stealth));
        }
    }

    [Theory]
    [InlineData(SpellImplicitTarget.UnitEnemy, SpellImplicitTarget.None, false)]
    [InlineData(SpellImplicitTarget.UnitCaster, SpellImplicitTarget.None, true)]
    [InlineData(SpellImplicitTarget.UnitFriend, SpellImplicitTarget.None, true)]
    [InlineData(SpellImplicitTarget.LocationCasterTargetPosition, SpellImplicitTarget.None, false)]
    [InlineData(SpellImplicitTarget.LocationCasterSrc, SpellImplicitTarget.EnumUnitsPartyAoeAtSrcLoc, true)]
    [InlineData(SpellImplicitTarget.LocationCasterSrc, SpellImplicitTarget.EnumUnitsEnemyAoeAtSrcLoc, false)]
    [InlineData(SpellImplicitTarget.UnitCaster, SpellImplicitTarget.EnumUnitsEnemyInCone24, false)]
    public void IsPositiveTarget_FollowsVmangos(SpellImplicitTarget a, SpellImplicitTarget b, bool expected)
        => Assert.Equal(expected, StealthBreakRules.IsPositiveTarget(a, b));

    [Fact]
    public void AuraInterruptMask_HasTheVmangosValues()
    {
        Assert.Equal(0x3C07u, AuraInterruptMask.StealthFamily);
        Assert.Equal(0x00001000u, AuraInterruptMask.Attacking);
        Assert.Equal(0x00010000u, AuraInterruptMask.ActionLate);
        Assert.Equal(0x00400000u, AuraInterruptMask.EnterWorld);
    }

    private sealed class SequenceRandom(params int[] values) : Random
    {
        private int _next;

        public override int Next(int maxValue) => values[Math.Min(_next++, values.Length - 1)];
    }

    private sealed class FixedRandom(int value) : Random
    {
        public override int Next(int maxValue) => value;
    }
}

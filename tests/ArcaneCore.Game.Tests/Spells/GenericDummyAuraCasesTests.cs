using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Generic;
using ArcaneCore.Game.Spells.Mods;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// The aura behaviour vmangos keeps in C++ (HandleAuraDummy, PeriodicDummyTick, the cast and healing-bonus readers of OVERRIDE_CLASS_SCRIPTS)
/// for the spells of lane g1-auras. Each test fails without its handler: the aura lands and nothing happens.
/// </summary>
public sealed class GenericDummyAuraCasesTests
{
    private static SpellInfo Instant(SpellInfo spell) => spell with { StartRecoveryCategory = 0, StartRecoveryTime = 0, SpellVisual = 1 };

    private static SpellInfo DummyAura(uint id, int durationMs = 60_000) =>
        Instant(Spell(id, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
        {
            Duration = new SpellDuration(durationMs, 0, durationMs),
        });

    private static SpellTestKit Kit(params SpellInfo[] spells)
    {
        var kit = new SpellTestKit(spells);
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        return kit;
    }

    [Fact]
    public void CologneAndPerfume_GrantDetectAmoreOnTheNextTick_AndTakeItAway()
    {
        using SpellTestKit kit = Kit(DummyAura(26681), DummyAura(26682),
            Instant(Spell(GenericDummyAuraModule.DetectAmore, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
            {
                Duration = new SpellDuration(60_000, 0, 60_000),
            }));
        (Player player, _) = kit.AddPlayer(1);

        kit.System.CastSpell(player, 26681, SpellCastTargets.ForSelf(), triggered: true);
        kit.World.RunTick(10);
        kit.Advance(200);
        Assert.True(kit.System.HasAura(player, GenericDummyAuraModule.DetectAmore));

        kit.System.RemoveAuras(player, 26681);
        kit.World.RunTick(10);
        kit.Advance(200);
        Assert.False(kit.System.HasAura(player, GenericDummyAuraModule.DetectAmore));
    }

    [Fact]
    public void PartyFever_MakesAPlayerOutOfCombatDance_AndNotOneInCombat()
    {
        using SpellTestKit kit = Kit(DummyAura(GenericDummyAuraModule.PartyFever));
        (Player dancer, FakeSession dancerSession) = kit.AddPlayer(1);
        (Player fighter, FakeSession fighterSession) = kit.AddPlayer(2);
        fighter.UnitFlags |= UnitFlags.InCombat;

        kit.System.CastSpell(fighter, GenericDummyAuraModule.PartyFever, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Empty(Packets(dancerSession, WorldOpcode.SmsgEmote));
        Assert.Empty(Packets(fighterSession, WorldOpcode.SmsgEmote));

        kit.System.CastSpell(dancer, GenericDummyAuraModule.PartyFever, SpellCastTargets.ForSelf(), triggered: true);
        Assert.NotEmpty(Packets(dancerSession, WorldOpcode.SmsgEmote));
    }

    [Fact]
    public void SleepVisual_PutsThePlayerToSleepHalfASecondLater()
    {
        using SpellTestKit kit = Kit(DummyAura(GenericDummyAuraModule.SleepUntilCancelled));
        (Player player, _) = kit.AddPlayer(1);

        kit.System.CastSpell(player, GenericDummyAuraModule.SleepUntilCancelled, SpellCastTargets.ForSelf(), triggered: true);
        Assert.NotEqual(StandState.Sleep, player.StandState);
        for (int i = 0; i < 8; i++)
        {
            kit.World.RunTick(100);
        }

        Assert.Equal(StandState.Sleep, player.StandState);
        Assert.Equal(GenericDummyAuraModule.EmoteStateSleep, player.GetUInt32(UpdateFields.UnitNpcEmotestate));
    }

    [Fact]
    public void GargoyleStoneform_FreezesAndHidesTheUnit_UntilTheAuraEnds()
    {
        using SpellTestKit kit = Kit(DummyAura(GenericDummyAuraModule.GargoyleStoneform));
        (Player player, _) = kit.AddPlayer(1);

        kit.System.CastSpell(player, GenericDummyAuraModule.GargoyleStoneform, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(GenericDummyAuraModule.StoneformStandState, player.StandState);
        Assert.True((player.UnitFlags & UnitFlags.NotSelectable) != 0);

        kit.System.RemoveAuras(player, GenericDummyAuraModule.GargoyleStoneform);
        Assert.Equal(StandState.Stand, player.StandState);
        Assert.True((player.UnitFlags & UnitFlags.NotSelectable) == 0);
    }

    [Fact]
    public void ForsakenSkills_CastsAForsakenSkillSpellEveryThirtySeconds()
    {
        SpellInfo[] skills = [.. Enumerable.Range(7038, 14).Append(7053).Select(id => (uint)id)
            .Select(id => Instant(Spell(id, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
            {
                Duration = new SpellDuration(60_000, 0, 60_000),
            }))];
        using SpellTestKit kit = Kit([DummyAura(GenericDummyAuraModule.ForsakenSkills, 120_000), .. skills]);
        (Player player, _) = kit.AddPlayer(1);

        kit.System.CastSpell(player, GenericDummyAuraModule.ForsakenSkills, SpellCastTargets.ForSelf(), triggered: true);
        Assert.DoesNotContain(kit.System.GetAuras(player), h => h.Spell.Id is >= 7038 and <= 7053);
        kit.Advance(31_000);

        Assert.Contains(kit.System.GetAuras(player), h => h.Spell.Id is >= 7038 and <= 7053);
    }

    [Fact]
    public void CorruptedMind_RefusesAHealerFamilyHeal_ButNotItsOtherCasts()
    {
        const uint mind = 29184, heal = 941101, damage = 941102;
        using SpellTestKit kit = Kit(
            Instant(Spell(mind, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.OverrideClassScripts, misc: 4327)) with
            {
                Duration = new SpellDuration(60_000, 0, 60_000),
            }),
            Instant(Spell(heal, Effect(SpellEffectName.Heal, 10, SpellImplicitTarget.UnitCaster)) with { SpellFamilyName = 6 }),
            Instant(Spell(damage, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitCaster)) with { SpellFamilyName = 6 }));
        (Player priest, _) = kit.AddPlayer(1);
        kit.System.CastSpell(priest, mind, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(SpellCastResult.Fizzle, kit.System.CastSpell(priest, heal, SpellCastTargets.ForSelf(), triggered: false));
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(priest, heal, SpellCastTargets.ForSelf(), triggered: true));
        Assert.NotEqual(SpellCastResult.Fizzle, kit.System.CastSpell(priest, damage, SpellCastTargets.ForSelf(), triggered: false));
    }

    [Fact]
    public void ImprovedFireWard_DummyEffectPassive_HoldsTheWardModUntilForgotten()
    {
        SpellInfo ward = Spell(11094, Effect(SpellEffectName.Dummy, 10, SpellImplicitTarget.UnitCaster)) with
        {
            Attributes = SpellAttributes.Passive,
            SpellFamilyName = 3,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
        using SpellTestKit kit = Kit(ward);
        (Player player, _) = kit.AddPlayer(1);

        kit.System.LearnSpell(player, 11094);
        SpellMod mod = Assert.Single(kit.System.Mods.ModsOf(player, SpellModOp.ResistMissChance));
        Assert.Equal(0x8UL, mod.Mask);
        Assert.Equal(10, mod.Value);

        kit.System.RemoveSpell(player, 11094);
        Assert.Empty(kit.System.Mods.ModsOf(player, SpellModOp.ResistMissChance));
    }

    [Theory]
    [InlineData(3736, 80, 80)]
    [InlineData(4415, 50, 12)]
    public void HealingTotemsAndIdols_AddTheirAmountToTheMatchingHeal(int misc, int amount, int expectedBonus)
    {
        const uint totem = 941103, heal = 941104;
        SpellEffectInfo marker = Effect(SpellEffectName.ApplyAura, amount, SpellImplicitTarget.UnitCaster, AuraType.OverrideClassScripts, misc: misc) with { ItemType = 1 };
        using SpellTestKit kit = Kit(
            Instant(Spell(totem, marker) with { SpellFamilyName = 11, Duration = new SpellDuration(60_000, 0, 60_000) }),
            Instant(Spell(heal, Effect(SpellEffectName.Heal, 100, SpellImplicitTarget.UnitCaster)) with { SpellFamilyName = 11, SpellFamilyFlags = 1 }));
        kit.System.AmountModifier = new ArcaneCore.Game.Spells.Casters.Bonus.SpellBonusModule(kit.System);
        (Player shaman, _) = kit.AddPlayer(1);
        shaman.MaxHealth = 5000;

        shaman.Health = 1000;
        kit.System.CastSpell(shaman, heal, SpellCastTargets.ForSelf(), triggered: true);
        uint plain = shaman.Health - 1000;

        kit.System.CastSpell(shaman, totem, SpellCastTargets.ForSelf(), triggered: true);
        shaman.Health = 1000;
        kit.System.CastSpell(shaman, heal, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(plain + (uint)expectedBonus, shaman.Health - 1000);
    }
}

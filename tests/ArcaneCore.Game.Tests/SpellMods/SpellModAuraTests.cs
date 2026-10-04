using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Mods;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.SpellMods;

/// <summary>
/// vmangos Aura::HandleAddModifier (SpellAuras.cpp:1081-1111): aura 107/108 on a player registers a modifier, removal takes it
/// away, a non-player holds none, and the ward mods the data does not carry are built in code (:2117-2155).
/// </summary>
public sealed class SpellModAuraTests
{
    private const uint Family = 3;
    private const uint Flat = 941001;
    private const uint Pct = 941002;
    private const uint HighBit = 941003;
    private const uint BadOp = 941004;
    private const uint Stacking = 941005;
    private const uint Charged = 941006;
    private const uint ShadowTrance = 17941;
    private const uint FrostWarding = 11189;
    private const uint ImprovedFireWard = 11094;
    private const uint Target = 941010;     // a spell of the family
    private const uint Dependent = 941011;  // a passive whose amount reads a damage mod
    private const uint CostMod = 941012;

    private static SpellInfo Passive(uint id, SpellEffectInfo effect, uint family = Family, ulong flags = 1) => Spell(id, effect) with
    {
        Attributes = SpellAttributes.Passive,
        Duration = new SpellDuration(-1, 0, -1),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
        SpellFamilyName = family,
        SpellFamilyFlags = flags,
    };

    private static SpellEffectInfo ModEffect(AuraType aura, SpellModOp op, int value, ulong mask) =>
        Effect(SpellEffectName.ApplyAura, value, aura: aura, misc: (int)op) with { ItemType = (uint)mask };

    private static SpellTestKit Kit(params SpellInfo[] extra) => new(
        [
            Passive(Flat, ModEffect(AuraType.AddFlatModifier, SpellModOp.Damage, 7, 1)),
            Passive(Pct, ModEffect(AuraType.AddPctModifier, SpellModOp.Damage, 20, 1)),
            Passive(HighBit, ModEffect(AuraType.AddFlatModifier, SpellModOp.Damage, 4, 0x100)),
            Passive(BadOp, ModEffect(AuraType.AddFlatModifier, (SpellModOp)29, 4, 1)),
            Passive(Stacking, ModEffect(AuraType.AddFlatModifier, SpellModOp.Damage, 4, 1)) with { StackAmount = 3, ProcCharges = 2 },
            Passive(Charged, ModEffect(AuraType.AddPctModifier, SpellModOp.Cost, -100, 1)) with { ProcCharges = 1 },
            Passive(ShadowTrance, ModEffect(AuraType.AddPctModifier, SpellModOp.CastingTime, -100, 1)),
            Passive(FrostWarding, Effect(SpellEffectName.ApplyAura, 3, aura: AuraType.Dummy), flags: 0),
            Passive(ImprovedFireWard, Effect(SpellEffectName.ApplyAura, 2, aura: AuraType.Dummy), flags: 0),
            Spell(Target, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with { SpellFamilyName = Family, SpellFamilyFlags = 1 },
            .. extra,
        ]);

    private static SpellInfo SpellOf(SpellTestKit kit, uint id) => kit.Store.Get(id)!;

    [Fact]
    public void LearningAPassiveFlatMod_RegistersExactlyOneMod_WithOpValueMaskAndFamily()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);

        Assert.True(kit.System.LearnSpell(player, Flat));

        SpellMod mod = Assert.Single(kit.System.Mods.ModsOf(player, SpellModOp.Damage));
        Assert.Equal(SpellModType.Flat, mod.Type);
        Assert.Equal(7, mod.Value);
        Assert.Equal(1UL, mod.Mask);
        Assert.Equal(Family, mod.FamilyName);
        Assert.Equal(Flat, mod.SpellId);
    }

    [Fact]
    public void ForgettingThePassive_RemovesTheMod()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        kit.System.LearnSpell(player, Flat);
        kit.System.LearnSpell(player, Pct);
        Assert.Equal(2, kit.System.Mods.ModsOf(player, SpellModOp.Damage).Count);

        Assert.True(kit.System.RemoveSpell(player, Flat));

        SpellMod left = Assert.Single(kit.System.Mods.ModsOf(player, SpellModOp.Damage));
        Assert.Equal(Pct, left.SpellId);
    }

    [Fact]
    public void TheEngineIsInstalledOnTheSeam_AndFlatPlusPctComposeLikeVmangos()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        kit.System.LearnSpell(player, Flat);
        kit.System.LearnSpell(player, Pct);

        Assert.Same(kit.System.Mods, kit.System.SpellModifiers);
        // 100 + ((100 + 7) * 20 / 100 + 7) = 128.4
        Assert.Equal(100f + (107f * 20f / 100.0f + 7f), kit.System.SpellModifiers.Apply(player, SpellOf(kit, Target), SpellModOp.Damage, 100f));
    }

    [Fact]
    public void AMaskAboveBitThirtyOne_IsHeldWhole_FromTheMaskSource()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        kit.System.Mods.MaskSource = new FixedMasks(new() { [(HighBit, 0)] = 1UL << 40 });

        kit.System.LearnSpell(player, HighBit);

        Assert.Equal(1UL << 40, Assert.Single(kit.System.Mods.ModsOf(player, SpellModOp.Damage)).Mask);
    }

    [Fact]
    public void ANonPlayerHoldsNoMods()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player other, _) = kit.AddPlayer(2, 2);

        kit.System.CastSpell(caster, Flat, SpellCastTargets.ForUnit(other.Guid), triggered: true);

        // The aura is self-targeted data, so it lands on the caster; the other player holds nothing.
        Assert.Empty(kit.System.Mods.ModsOf(other, SpellModOp.Damage));
        Assert.Single(kit.System.Mods.ModsOf(caster, SpellModOp.Damage));
    }

    [Fact]
    public void AnOperationAtOrAboveTheMaximum_IsIgnored()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);

        kit.System.LearnSpell(player, BadOp);

        for (int op = 0; op < (int)SpellModOp.Max; op++)
        {
            Assert.Empty(kit.System.Mods.ModsOf(player, (SpellModOp)op));
        }
    }

    [Fact]
    public void AStackableSpell_NeverCarriesCharges_SoItRegisters()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);

        kit.System.LearnSpell(player, Stacking);

        Assert.Equal(0, Assert.Single(kit.System.Mods.ModsOf(player, SpellModOp.Damage)).Charges);
    }

    [Fact]
    public void ShadowTrance_GetsItsCustomCharge_OnTheHolderAndTheMod()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);

        kit.System.LearnSpell(player, ShadowTrance);

        Assert.Equal(1, kit.System.GetAuras(player).Single(h => h.Spell.Id == ShadowTrance).Charges);
        Assert.Equal(1, Assert.Single(kit.System.Mods.ModsOf(player, SpellModOp.CastingTime)).Charges);
    }

    [Fact]
    public void FrostWarding_AddsAFlatResistMissMod_OnMask0x100_AndRemovalClearsIt()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);

        kit.System.LearnSpell(player, FrostWarding);

        SpellMod mod = Assert.Single(kit.System.Mods.ModsOf(player, SpellModOp.ResistMissChance));
        Assert.Equal((SpellModType.Flat, 3, 0x100UL), (mod.Type, mod.Value, mod.Mask));
        kit.System.RemoveSpell(player, FrostWarding);
        Assert.Empty(kit.System.Mods.ModsOf(player, SpellModOp.ResistMissChance));
    }

    [Fact]
    public void ImprovedFireWard_UsesMask0x8()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);

        kit.System.LearnSpell(player, ImprovedFireWard);

        Assert.Equal(0x8UL, Assert.Single(kit.System.Mods.ModsOf(player, SpellModOp.ResistMissChance)).Mask);
    }

    [Fact]
    public void WardMods_CanBeSwitchedOff()
    {
        using SpellTestKit kit = Kit();
        kit.System.Mods.Options.HardcodedWardMods = false;
        (Player player, _) = kit.AddPlayer(1);

        kit.System.LearnSpell(player, FrostWarding);

        Assert.Empty(kit.System.Mods.ModsOf(player, SpellModOp.ResistMissChance));
    }

    [Fact]
    public void TheKillSwitch_MakesTheAurasInertAgain()
    {
        using SpellTestKit kit = Kit();
        kit.System.Mods.Options.Enabled = false;
        (Player player, _) = kit.AddPlayer(1);

        kit.System.LearnSpell(player, Flat);

        Assert.Empty(kit.System.Mods.ModsOf(player, SpellModOp.Damage));
        Assert.Equal(100f, kit.System.SpellModifiers.Apply(player, SpellOf(kit, Target), SpellModOp.Damage, 100f));
    }

    [Fact]
    public void ThePassiveAndItsMod_SurviveDeath()
    {
        using SpellTestKit kit = Kit();
        (Player victim, _) = kit.AddPlayer(1);
        (Player enemy, _) = kit.AddPlayer(2, 2);
        kit.System.LearnSpell(victim, Flat);

        victim.Map!.FindUpdater<MapCombat>()!.Kill(enemy, victim);
        kit.System.OnUnitDied(victim);

        Assert.Single(kit.System.Mods.ModsOf(victim, SpellModOp.Damage));
    }

    [Fact]
    public void ARelogCastsThePassiveAgain_OnTheNewPlayer()
    {
        using SpellTestKit kit = Kit();
        (Player first, _) = kit.AddPlayer(1);
        kit.System.LearnSpell(first, Flat);

        // The login path (SpellFeature.OnPlayerLoggedIn) casts every known passive on the freshly built player.
        (Player second, _) = kit.AddPlayer(3, 5);
        kit.System.CastSpell(second, Flat, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Single(kit.System.Mods.ModsOf(second, SpellModOp.Damage));
    }

    [Fact]
    public void ASecondLaneRegisteringTheSameAura_FailsAtStartupNamingTheModule()
    {
        using SpellTestKit kit = Kit();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => kit.System.RegisterModules([typeof(StealsAddFlat)]));

        Assert.Contains(typeof(StealsAddFlat).FullName!, error.Message);
        Assert.Contains("AddFlatModifier", error.Message);
    }

    [Fact]
    public void ReapplyingAPassive_HappensForADamageMod_ButNotForACostMod()
    {
        var dependent = Passive(Dependent, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy), flags: 1);
        var costMod = Passive(CostMod, ModEffect(AuraType.AddFlatModifier, SpellModOp.Cost, -3, 1));
        using SpellTestKit kit = Kit(dependent, costMod);
        (Player player, _) = kit.AddPlayer(1);
        kit.System.LearnSpell(player, Dependent);
        SpellAuraHolder before = kit.System.GetAuras(player).Single(h => h.Spell.Id == Dependent);

        kit.System.LearnSpell(player, CostMod);
        Assert.Same(before, kit.System.GetAuras(player).Single(h => h.Spell.Id == Dependent));   // COST is on the skip list

        kit.System.LearnSpell(player, Flat);
        SpellAuraHolder after = kit.System.GetAuras(player).Single(h => h.Spell.Id == Dependent);
        Assert.NotSame(before, after);                                                           // DAMAGE reapplies the affected passive
        Assert.True(before.IsRemoved);
    }

    private sealed class FixedMasks(Dictionary<(uint, int), ulong> masks) : IClassMaskSource
    {
        public ulong? TryGetMask(uint spellId, int effectIndex) => masks.TryGetValue((spellId, effectIndex), out ulong mask) ? mask : null;
    }

    private sealed class StealsAddFlat : ISpellHandlerModule
    {
        public void Register(SpellSystem system) => system.RegisterAura(AuraType.AddFlatModifier, new AuraHandler(null, null));
    }
}

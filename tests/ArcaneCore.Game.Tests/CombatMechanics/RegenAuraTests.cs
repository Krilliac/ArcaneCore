using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.CombatMechanics;

/// <summary>
/// Regeneration through the auras of mage and warlock spells and consumables: vmangos Player::UpdateManaRegen (StatSystem.cpp:642-661),
/// Player::RegenerateAll / Regenerate / RegenerateHealth (Player.cpp:2269-2402). The aura values are the classic-db z2815 rows of Drink (430),
/// Evocation (12051), Mage Armor (6117), Demon Armor (706) and Health Funnel (755), quoted here as oracle constants (base points + 1); the Food
/// aura has no quoted row and uses an invented value. Test player: a mage with 100 spirit, so the spirit regen is (100 / 4 + 12.5) / 2 = 18.75
/// per second, 37.5 per two-second tick.
/// </summary>
public sealed class RegenAuraTests
{
    private const uint Drink = 430;
    private const uint Evocation = 12051;
    private const uint MageArmor = 6117;
    private const uint DemonArmor = 706;
    private const uint HealthFunnel = 755;
    private const uint FoodLike = 960_501;
    private const uint PolymorphLike = 960_502;

    private static SpellInfo Buff(uint id, params SpellEffectInfo[] effects) => Spell(id, effects) with
    {
        Duration = new SpellDuration(60_000, 0, 60_000),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellTestKit NewKit(CombatOptions? options = null)
    {
        var kit = new SpellTestKit(
            Buff(Drink, Effect(SpellEffectName.ApplyAura, 42, aura: AuraType.ModPowerRegen, misc: (int)PowerType.Mana)),
            Buff(Evocation,
                Effect(SpellEffectName.ApplyAura, 1500, aura: AuraType.ModPowerRegenPercent, misc: (int)PowerType.Mana),
                Effect(SpellEffectName.ApplyAura, 100, aura: AuraType.ModManaRegenInterrupt)),
            Buff(MageArmor,
                Effect(SpellEffectName.ApplyAura, 5, aura: AuraType.ModResistance, misc: 126),
                Effect(SpellEffectName.ApplyAura, 30, aura: AuraType.ModManaRegenInterrupt)),
            Buff(DemonArmor,
                Effect(SpellEffectName.ApplyAura, 210, aura: AuraType.ModResistance, misc: 1),
                Effect(SpellEffectName.ApplyAura, 7, aura: AuraType.ModHealthRegenInCombat)),
            Buff(HealthFunnel, Effect(SpellEffectName.ApplyAura, -100, aura: AuraType.ModHealthRegenPercent)),
            Buff(FoodLike, Effect(SpellEffectName.ApplyAura, 12, aura: AuraType.ModRegen, amplitude: 2000)),
            // The mage polymorph classification of vmangos SpellEntry.cpp:67-75: mage family, first effect MOD_CONFUSE, silence prevention type.
            Buff(PolymorphLike,
                Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModConfuse),
                Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Transform, misc: 1)) with { SpellFamilyName = 3, PreventionType = 1 });
        CombatEnvironment.Register(kit.World, new CombatEnvironment(options ?? new CombatOptions(), new SpellSystemPowerAuras(kit.System)));
        return kit;
    }

    private static Player Mage(SpellTestKit kit)
    {
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Mage);
        player.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 5000);
        player.SetUInt32(UpdateFields.UnitFieldPower1, 0);
        player.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 100); // spirit
        player.MaxHealth = 1000;
        player.Health = 500;
        return player;
    }

    private static void Apply(SpellTestKit kit, Unit unit, uint spell)
        => kit.System.CastSpell(unit, spell, SpellCastTargets.ForSelf(), triggered: true);

    private static void Regen(SpellTestKit kit) => kit.World.RunTick(2000);

    private static uint Mana(Unit unit) => SpellSystem.GetPower(unit, PowerType.Mana);

    private static void InCombat(SpellTestKit kit, Player player) => kit.World.GetMap(0).Combat.SetInCombatState(player, 60_000);

    [Fact]
    public void Baseline_SpiritRegenOutsideTheFiveSecondWindow()
    {
        using SpellTestKit kit = NewKit();
        Player mage = Mage(kit);

        Regen(kit);

        Assert.Equal(37u, Mana(mage)); // 18.75 * 2
        Assert.Equal(512u, mage.Health); // 500 + (100 * 0.11 + 1) = 12, standing
    }

    [Fact]
    public void Baseline_NoManaRegenInsideTheFiveSecondWindow()
    {
        using SpellTestKit kit = NewKit();
        Player mage = Mage(kit);
        mage.Combat.LastManaUseTimer = 3000;

        Regen(kit);

        Assert.Equal(0u, Mana(mage));
    }

    [Fact]
    public void Evocation_MultipliesTheSpiritRegen_AndKeepsItInsideTheWindow()
    {
        using SpellTestKit kit = NewKit();
        Player mage = Mage(kit);
        Apply(kit, mage, Evocation);

        Regen(kit);
        Assert.Equal(600u, Mana(mage)); // 18.75 * (1500 + 100) / 100 * 2

        SpellSystem.SetPower(mage, PowerType.Mana, 0);
        mage.Combat.LastManaUseTimer = 3000;
        kit.World.RunTick(2000);
        Assert.Equal(600u, Mana(mage)); // 100 percent of it inside the window (the interrupt amount is 100)
    }

    [Fact]
    public void Drink_AddsAFifthOfItsAmountPerSecond_AndOnlyThatInsideTheWindow()
    {
        using SpellTestKit kit = NewKit();
        Player mage = Mage(kit);
        Apply(kit, mage, Drink);

        Regen(kit);
        Assert.Equal(54u, Mana(mage)); // (42 / 5 + 18.75) * 2 = 54.3

        SpellSystem.SetPower(mage, PowerType.Mana, 0);
        mage.Combat.LastManaUseTimer = 3000;
        kit.World.RunTick(2000);
        Assert.Equal(16u, Mana(mage)); // 42 / 5 * 2 = 16.8 and no spirit regen
    }

    [Fact]
    public void MageArmor_GivesThirtyPercentOfTheSpiritRegenInsideTheWindow()
    {
        using SpellTestKit kit = NewKit();
        Player mage = Mage(kit);
        Apply(kit, mage, MageArmor);
        mage.Combat.LastManaUseTimer = 3000;

        Regen(kit);

        Assert.Equal(11u, Mana(mage)); // 18.75 * 30 / 100 * 2 = 11.25
    }

    [Fact]
    public void TheInterruptPercentIsCappedAtOneHundred()
    {
        using SpellTestKit kit = NewKit();
        Player mage = Mage(kit);
        Apply(kit, mage, Evocation); // 100
        Apply(kit, mage, MageArmor); // + 30 = 130, capped
        mage.Combat.LastManaUseTimer = 3000;

        Regen(kit);

        Assert.Equal(600u, Mana(mage)); // 18.75 * 16 * 100 / 100 * 2, not 130 percent
    }

    [Fact]
    public void TheManaRateScalesTheWholeRegen()
    {
        using SpellTestKit kit = NewKit(new CombatOptions { RateMana = 2.0f });
        Player mage = Mage(kit);
        Apply(kit, mage, Drink);

        Regen(kit);

        Assert.Equal(108u, Mana(mage)); // (8.4 + 18.75) * 2 * 2 = 108.6
    }

    [Fact]
    public void Food_AddsItsPerTickAmountToHealth_OnlyOutOfCombat()
    {
        using SpellTestKit kit = NewKit();
        Player mage = Mage(kit);
        Apply(kit, mage, FoodLike);

        Regen(kit);
        Assert.Equal(524u, mage.Health); // 500 + 12 + 12 * (2000 / 2000)

        mage.Health = 500;
        InCombat(kit, mage);
        kit.World.RunTick(2000);
        Assert.Equal(500u, mage.Health); // in combat: no regeneration at all
    }

    [Fact]
    public void DemonArmor_RegeneratesInCombat_AndAddsToTheNormalRegenOutOfCombat()
    {
        using SpellTestKit kit = NewKit();
        Player mage = Mage(kit);
        Apply(kit, mage, DemonArmor);
        InCombat(kit, mage);

        Regen(kit);
        Assert.Equal(502u, mage.Health); // 2 * (7 / 5) = 2.8 per tick, 2 whole points, 0.8 carried

        mage.Health = 500;
        kit.World.GetMap(0).Combat.CombatStop(mage);
        kit.World.RunTick(2000);
        Assert.Equal(515u, mage.Health); // 12 + 2.8 + the 0.8 carry = 15.6
    }

    [Fact]
    public void HealthFunnelAura_StopsHealthRegenerationOutOfCombat()
    {
        using SpellTestKit kit = NewKit();
        Player mage = Mage(kit);
        Apply(kit, mage, HealthFunnel);

        Regen(kit);

        Assert.Equal(500u, mage.Health); // 12 * (100 - 100) / 100
    }

    [Fact]
    public void APolymorphedUnit_RegeneratesATenthOfItsMaximumEvenInCombat()
    {
        using SpellTestKit kit = NewKit();
        Player mage = Mage(kit);
        Apply(kit, mage, PolymorphLike);
        InCombat(kit, mage);

        Regen(kit);

        Assert.Equal(600u, mage.Health); // 1000 / 10
    }
}

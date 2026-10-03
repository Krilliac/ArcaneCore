using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Casters;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells.Casters;

/// <summary>
/// The "five second rule": paying mana for a spell stops mana regeneration for 5 s, unless the spell carries
/// SPELL_ATTR_EX2_DONT_BLOCK_MANA_REGEN, and a channel keeps it blocked while it runs.
/// vmangos Spell.cpp:5070-5079 (Spell::TakePower), Unit.cpp:235-253 (Unit::Update, patch 1.7 channel fix).
/// </summary>
public sealed class ManaSpendRuleTests
{
    private const uint ManaHeal = 2001;
    private const uint FreeHeal = 2002;
    private const uint RageHeal = 2003;
    private const uint HealthCost = 2004;
    private const uint NoBlockHeal = 2005;
    private const uint ManaChannel = 2006;
    private const uint FreeChannel = 2007;

    private static SpellTestKit NewKit() => new(
        Mana(ManaHeal, 100),
        SpellTestKit.Spell(FreeHeal, SpellTestKit.Effect(SpellEffectName.Heal, 5)),
        SpellTestKit.Spell(RageHeal, SpellTestKit.Effect(SpellEffectName.Heal, 5)) with { PowerType = (int)PowerType.Rage, ManaCost = 10 },
        SpellTestKit.Spell(HealthCost, SpellTestKit.Effect(SpellEffectName.Heal, 5)) with { PowerType = SpellMath.PowerHealth, ManaCost = 5 },
        Mana(NoBlockHeal, 100) with { AttributesEx2 = (SpellAttributesEx2)CasterAttributes.Ex2DontBlockManaRegen },
        Channel(ManaChannel, 100),
        Channel(FreeChannel, 0));

    private static SpellInfo Mana(uint id, uint cost) => SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.Heal, 5)) with
    {
        PowerType = (int)PowerType.Mana,
        ManaCost = cost,
        School = SpellSchool.Frost,
    };

    private static SpellInfo Channel(uint id, uint cost) => SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
    {
        PowerType = (int)PowerType.Mana,
        ManaCost = cost,
        AttributesEx = SpellAttributesEx.IsChanneled,
        Duration = new SpellDuration(5000, 0, 5000),
        SpellVisual = 1,
    };

    private static Player CasterWithMana(SpellTestKit kit, uint mana = 500)
    {
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Mage);
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 1000);
        player.SetUInt32(UpdateFields.UnitFieldPower1, mana);
        player.SetUInt32(UpdateFields.UnitFieldMaxpower2, 1000);
        player.SetUInt32(UpdateFields.UnitFieldPower2, 500);
        kit.Spellbook.Teach(player, ManaHeal, FreeHeal, RageHeal, HealthCost, NoBlockHeal, ManaChannel, FreeChannel);
        return player;
    }

    [Fact]
    public void PaidManaCast_StartsTheFiveSecondTimer_AndRecordsTheSpell()
    {
        using SpellTestKit kit = NewKit();
        Player player = CasterWithMana(kit);

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(player, ManaHeal, SpellCastTargets.ForSelf()));

        Assert.Equal(400u, SpellSystem.GetPower(player, PowerType.Mana));
        Assert.Equal(CombatConstants.ManaRegenInterruptMs, player.Combat.LastManaUseTimer);
        Assert.Equal(ManaHeal, player.Combat.LastManaUseSpellId);
    }

    [Theory]
    [InlineData(FreeHeal)]
    [InlineData(RageHeal)]
    [InlineData(HealthCost)]
    [InlineData(NoBlockHeal)]
    public void UnpaidOrExemptCasts_DoNotStartTheTimer(uint spellId)
    {
        using SpellTestKit kit = NewKit();
        Player player = CasterWithMana(kit);

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(player, spellId, SpellCastTargets.ForSelf()));

        Assert.Equal(0u, player.Combat.LastManaUseTimer);
    }

    [Fact]
    public void TriggeredCast_DoesNotStartTheTimer()
    {
        using SpellTestKit kit = NewKit();
        Player player = CasterWithMana(kit);

        kit.System.CastSpell(player, ManaHeal, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(0u, player.Combat.LastManaUseTimer);
    }

    [Fact]
    public void ManaRegeneration_ResumesOnlyWhenTheTimerHasRunOut()
    {
        using SpellTestKit kit = NewKit();
        Player player = CasterWithMana(kit);
        kit.System.HandleCastRequest(player, ManaHeal, SpellCastTargets.ForSelf());
        Assert.Equal(400u, SpellSystem.GetPower(player, PowerType.Mana));

        kit.World.RunTick(2000);
        kit.World.RunTick(2000);
        Assert.Equal(1000u, player.Combat.LastManaUseTimer);
        Assert.Equal(400u, SpellSystem.GetPower(player, PowerType.Mana));

        kit.World.RunTick(1000);
        kit.World.RunTick(2000);
        Assert.Equal(0u, player.Combat.LastManaUseTimer);
        Assert.True(SpellSystem.GetPower(player, PowerType.Mana) > 400u);
    }

    [Fact]
    public void EveryPaidCast_RestartsTheTimer()
    {
        using SpellTestKit kit = NewKit();
        Player player = CasterWithMana(kit);
        kit.System.HandleCastRequest(player, ManaHeal, SpellCastTargets.ForSelf());
        kit.World.RunTick(3000);
        Assert.Equal(2000u, player.Combat.LastManaUseTimer);

        kit.Advance(2000);
        kit.System.HandleCastRequest(player, ManaHeal, SpellCastTargets.ForSelf());

        Assert.Equal(CombatConstants.ManaRegenInterruptMs, player.Combat.LastManaUseTimer);
    }

    [Fact]
    public void ChannellingTheSpellThatTookMana_HoldsTheTimer_UntilTheChannelEnds()
    {
        using SpellTestKit kit = NewKit();
        Player player = CasterWithMana(kit);
        kit.System.HandleCastRequest(player, ManaChannel, SpellCastTargets.ForSelf());
        Assert.Equal(ManaChannel, player.GetUInt32(UpdateFields.UnitChannelSpell));

        kit.World.RunTick(5000);
        kit.World.RunTick(5000);
        Assert.True(player.Combat.LastManaUseTimer > 0, "the timer must not run out during the channel");

        kit.Advance(5000);
        Assert.Equal(0u, player.GetUInt32(UpdateFields.UnitChannelSpell));
        kit.World.RunTick(5000);
        Assert.Equal(0u, player.Combat.LastManaUseTimer);
        Assert.Equal(0u, player.Combat.LastManaUseSpellId);
    }

    [Fact]
    public void ChannellingADifferentSpell_DoesNotHoldTheTimer()
    {
        using SpellTestKit kit = NewKit();
        Player player = CasterWithMana(kit);
        kit.System.HandleCastRequest(player, ManaHeal, SpellCastTargets.ForSelf());
        kit.Advance(2000);
        kit.System.HandleCastRequest(player, FreeChannel, SpellCastTargets.ForSelf());
        Assert.Equal(FreeChannel, player.GetUInt32(UpdateFields.UnitChannelSpell));

        kit.World.RunTick(5000);

        Assert.Equal(0u, player.Combat.LastManaUseTimer);
    }
}

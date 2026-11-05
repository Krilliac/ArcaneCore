using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Generic;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// <see cref="PeriodicTriggerAuraModule"/>: the Aura::TriggerSpell cases of vmangos (SpellAuras.cpp:1291-1301 Nitrous Boost, :1480-1486 rocket
/// boots) and the expiry case of Tranquilizing Poison (:7210-7217). Each test fails without its registration: the aura ticks or ends and nothing
/// of what retail does happens.
/// </summary>
public sealed class PeriodicTriggerAuraCasesTests
{
    private const uint Marker = 941201;

    private static SpellInfo Instant(SpellInfo spell) => spell with { StartRecoveryCategory = 0, StartRecoveryTime = 0, SpellVisual = 1 };

    private static SpellInfo TriggerAura(uint id, uint trigger, int durationMs = 60_000) =>
        Instant(Spell(id, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.PeriodicTriggerSpell, amplitude: 1000, trigger: trigger)) with
        {
            Duration = new SpellDuration(durationMs, 0, durationMs),
        });

    private static SpellInfo MarkerAura(uint id) =>
        Instant(Spell(id, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
        {
            Duration = new SpellDuration(60_000, 0, 60_000),
        });

    private static SpellTestKit MakeKit(params SpellInfo[] spells)
    {
        var kit = new SpellTestKit(spells);
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        return kit;
    }

    private sealed class FixedRandom(int value) : Random
    {
        public override int Next(int maxValue) => value;
    }

    [Fact]
    public void NitrousBoost_BurnsTenManaPerTick_AndEndsWhenTheTargetHasLess()
    {
        using SpellTestKit kit = MakeKit(TriggerAura(PeriodicTriggerAuraModule.NitrousBoost, 0));
        (Player player, FakeSession session) = kit.AddPlayer(1);
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana, 500);
        SpellSystem.SetPower(player, PowerType.Mana, 25);

        kit.System.CastSpell(player, PeriodicTriggerAuraModule.NitrousBoost, SpellCastTargets.ForSelf(), triggered: true);
        session.Clear();
        kit.Advance(1_200);
        Assert.Equal(15u, SpellSystem.GetPower(player, PowerType.Mana));
        Assert.NotEmpty(Packets(session, WorldOpcode.SmsgSpellenergizelog));
        Assert.True(kit.System.HasAura(player, PeriodicTriggerAuraModule.NitrousBoost));

        kit.Advance(1_000);
        Assert.Equal(5u, SpellSystem.GetPower(player, PowerType.Mana));
        Assert.True(kit.System.HasAura(player, PeriodicTriggerAuraModule.NitrousBoost));

        kit.Advance(1_000); // 5 mana is below the 10 a tick costs: the aura goes and the mana stays
        Assert.Equal(5u, SpellSystem.GetPower(player, PowerType.Mana));
        Assert.False(kit.System.HasAura(player, PeriodicTriggerAuraModule.NitrousBoost));
    }

    [Theory]
    [InlineData(PeriodicTriggerAuraModule.GoblinRocketBoots)]
    [InlineData(PeriodicTriggerAuraModule.GnomishRocketBoots)]
    public void RocketBoots_CastTheirTriggerOnlyOnTheOneInAHundredRoll(uint boots)
    {
        using SpellTestKit kit = MakeKit(TriggerAura(boots, Marker), MarkerAura(Marker));
        kit.System.Random = new FixedRandom(1);
        (Player player, _) = kit.AddPlayer(1);

        kit.System.CastSpell(player, boots, SpellCastTargets.ForSelf(), triggered: true);
        kit.Advance(3_500);
        Assert.False(kit.System.HasAura(player, Marker));

        kit.System.Random = new FixedRandom(0);
        kit.Advance(1_200);
        Assert.True(kit.System.HasAura(player, Marker));
    }

    [Theory]
    [InlineData(PeriodicTriggerAuraModule.TranquilizingPoison)]
    [InlineData(PeriodicTriggerAuraModule.TranquilizingPoisonB)]
    public void TranquilizingPoison_CastsItsEndSpellWhenItExpires_NotWhenItIsCancelled(uint poison)
    {
        using SpellTestKit kit = MakeKit(
            Instant(Spell(poison, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
            {
                Duration = new SpellDuration(2_000, 0, 2_000),
            }),
            MarkerAura(PeriodicTriggerAuraModule.TranquilizingPoisonEnd));
        (Player expired, _) = kit.AddPlayer(1);
        (Player cancelled, _) = kit.AddPlayer(2);

        kit.System.CastSpell(expired, poison, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(cancelled, poison, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.RemoveAuras(cancelled, poison, AuraRemoveMode.Cancel);
        Assert.False(kit.System.HasAura(cancelled, PeriodicTriggerAuraModule.TranquilizingPoisonEnd));

        kit.Advance(2_500);

        Assert.False(kit.System.HasAura(expired, poison));
        Assert.True(kit.System.HasAura(expired, PeriodicTriggerAuraModule.TranquilizingPoisonEnd));
        Assert.False(kit.System.HasAura(cancelled, PeriodicTriggerAuraModule.TranquilizingPoisonEnd));
    }

    [Theory]
    [InlineData(PeriodicTriggerAuraModule.FirestoneRank1, 1803u)]
    [InlineData(PeriodicTriggerAuraModule.FirestoneRank2, 1823u)]
    [InlineData(PeriodicTriggerAuraModule.FirestoneRank3, 1824u)]
    [InlineData(PeriodicTriggerAuraModule.FirestoneRank4, 1825u)]
    [InlineData(12345u, 0u)]
    public void Firestone_NamesTheEnchantOfItsRank(uint spell, uint enchant)
        => Assert.Equal(enchant, PeriodicTriggerAuraModule.FirestoneEnchantFor(spell));
}

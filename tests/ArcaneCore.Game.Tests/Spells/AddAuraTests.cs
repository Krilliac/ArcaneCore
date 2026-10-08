using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>vmangos Unit::AddAura (Objects/Unit.cpp:10511-10561) through <see cref="SpellSystem.AddAura"/>: auras without a cast, permanent on request.</summary>
public sealed class AddAuraTests
{
    private const uint TwentySecondInvisibility = 24699;

    private static SpellInfo Vanish() => SpellTestKit.Spell(TwentySecondInvisibility,
        SpellTestKit.Effect(SpellEffectName.Dummy, 0),
        SpellTestKit.Effect(SpellEffectName.ApplyAura, 10000, aura: AuraType.ModInvisibility)) with
    {
        Duration = new SpellDuration(20000, 0, 20000),
    };

    [Fact]
    public void APermanentAura_OutlivesTheSpellDuration_AndNoCastIsSent()
    {
        using var kit = new SpellTestKit(Vanish());
        (Player player, FakeSession session) = kit.AddPlayer(1);

        Assert.True(kit.System.AddAura(player, TwentySecondInvisibility, permanent: true));

        SpellAuraHolder holder = Assert.Single(kit.System.GetAuras(player));
        Assert.True(holder.IsPermanent);
        Assert.Equal(player.Guid, holder.CasterGuid); // the target is the caster without one
        SpellAura aura = Assert.Single(holder.Auras.OfType<SpellAura>()); // the dummy effect builds no aura
        Assert.Equal(AuraType.ModInvisibility, aura.Type);
        Assert.Equal(10000, aura.Amount);
        Assert.DoesNotContain(session.Sent, p => p.Opcode is WorldOpcode.SmsgSpellGo or WorldOpcode.SmsgSpellStart);

        kit.Advance(25_000);
        Assert.True(kit.System.HasAura(player, TwentySecondInvisibility));
    }

    [Fact]
    public void WithoutThePermanentFlag_TheAuraRunsItsSpellDuration()
    {
        using var kit = new SpellTestKit(Vanish());
        (Player player, _) = kit.AddPlayer(1);

        Assert.True(kit.System.AddAura(player, TwentySecondInvisibility));
        Assert.False(Assert.Single(kit.System.GetAuras(player)).IsPermanent);

        kit.Advance(19_900);
        Assert.True(kit.System.HasAura(player, TwentySecondInvisibility));
        kit.Advance(200);
        Assert.False(kit.System.HasAura(player, TwentySecondInvisibility));
    }

    [Fact]
    public void ASpellWithoutAnAura_OrAnUnknownSpell_AddsNothing()
    {
        using var kit = new SpellTestKit();
        (Player player, _) = kit.AddPlayer(1);

        Assert.False(kit.System.AddAura(player, SpellTestKit.InstantHeal, permanent: true));
        Assert.False(kit.System.AddAura(player, 999_999, permanent: true));
        Assert.Empty(kit.System.GetAuras(player));
    }

    [Fact]
    public void TheCreatureSpellSeam_AddsThroughTheSpellSystem()
    {
        using var kit = new SpellTestKit(Vanish());
        (Player player, _) = kit.AddPlayer(1);
        var caster = new SpellSystemCreatureCaster(kit.System);

        Assert.Equal(CreatureCastResult.UnknownSpell, caster.AddAura(player, 999_999, permanent: true));
        Assert.Equal(CreatureCastResult.Ok, caster.AddAura(player, TwentySecondInvisibility, permanent: true));
        Assert.True(Assert.Single(kit.System.GetAuras(player)).IsPermanent);
    }
}

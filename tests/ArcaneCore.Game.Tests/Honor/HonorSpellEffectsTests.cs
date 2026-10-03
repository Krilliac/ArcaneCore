using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Honor;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Honor;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Honor;

/// <summary>SPELL_EFFECT_ADD_HONOR and the Honorless Target aura (vmangos SpellEffects.cpp:2979-2988).</summary>
public sealed class HonorSpellEffectsTests
{
    private const uint HonorBranch = 970201;   // add honor, value 49 (classic spell 24960 style)
    private const uint Honorless = 970202;     // Honorless Target: aura 159
    private const uint Today = 20_000;

    private static SpellTestKit Kit() => new(
        Spell(HonorBranch, Effect(SpellEffectName.AddHonor, 49, SpellImplicitTarget.UnitCaster)),
        Spell(Honorless, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, aura: AuraType.NoPvpCredit)) with
        {
            Duration = new SpellDuration(-1, 0, -1),
            SpellVisual = 1,
        });

    [Fact]
    public void The_module_is_discovered_and_both_handlers_exist()
    {
        using var kit = Kit();
        Assert.Contains(typeof(HonorSpellEffects), kit.System.Modules);
        Assert.True(kit.System.HasEffectHandler(SpellEffectName.AddHonor));
        Assert.True(kit.System.HasAuraHandler(AuraType.NoPvpCredit));
    }

    [Fact]
    public void A_player_target_is_given_the_effect_value_as_quest_honor_without_level_scaling()
    {
        using var kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        var sink = new RecordingHonorSink();
        var honor = new HonorService(new HonorOptions(), new FixedHonorClock(Today), () => 19_997, sink);
        honor.Track(player, honor.Create(player, CharacterHonorData.Empty));
        honor.InstallForSpells(kit.System);
        session.Clear();

        kit.System.CastSpell(player, HonorBranch, SpellCastTargets.ForUnit(player.Guid), triggered: true);

        Assert.Equal([new HonorCpRecord(0, 1, 49f, Today, (byte)HonorKind.Quest)], sink.Rows);
        Assert.Equal(49u, player.GetUInt32(UpdateFields.PlayerFieldThisWeekContribution));
        Assert.Equal(0f, honor.For(player)!.RankPoints); // quest honor is contribution only
    }

    [Fact]
    public void A_spell_system_without_an_honor_service_ignores_the_effect()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        kit.System.CastSpell(player, HonorBranch, SpellCastTargets.ForUnit(player.Guid), triggered: true);
        Assert.Null(HonorService.ForSpells(kit.System));
    }

    [Fact]
    public void The_honorless_target_aura_applies_and_is_visible_to_the_aura_query()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        var auras = new SpellSystemPowerAuras(kit.System);
        Assert.False(auras.HasAuraType(player, AuraType.NoPvpCredit));

        kit.System.CastSpell(player, Honorless, SpellCastTargets.ForUnit(player.Guid), triggered: true);

        Assert.True(auras.HasAuraType(player, AuraType.NoPvpCredit));
    }
}

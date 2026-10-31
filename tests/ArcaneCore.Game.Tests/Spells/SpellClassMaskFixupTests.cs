using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Mods;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// Improved Sprint (13743, 13875) and Initiative (13976, 13979, 13980) are ADD_TARGET_TRIGGER talents whose client Spell.dbc class mask is empty;
/// the mask is in mangos-classic spell_affect (0x40 for Sprint, 0x700 for Garrote, Ambush and Cheap Shot). Without a built-in fixup the talent never triggers.
/// </summary>
public sealed class SpellClassMaskFixupTests
{
    private const uint ImprovedSprint = 13875;
    private const uint Sprint = 931101;
    private const uint SprintTrigger = 30918;
    private const uint OtherTalent = 931102;

    private sealed class FixedMask(ulong mask) : IClassMaskSource
    {
        public ulong? TryGetMask(uint spellId, int effectIndex) => spellId == ImprovedSprint ? mask : null;
    }

    private static SpellInfo Talent(uint id, uint trigger) => Spell(id, Effect(SpellEffectName.ApplyAura, 100, SpellImplicitTarget.UnitCaster, AuraType.AddTargetTrigger, trigger: trigger)) with
    {
        SpellFamilyName = 8,
        Duration = new SpellDuration(-1, 0, -1),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellTestKit NewKit() => new(
        Talent(ImprovedSprint, SprintTrigger),
        Talent(OtherTalent, SprintTrigger),
        Spell(Sprint, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
        {
            SpellFamilyName = 8,
            SpellFamilyFlags = 0x40,
            Duration = new SpellDuration(15_000, 0, 15_000),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        },
        Spell(SprintTrigger, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
        {
            Duration = new SpellDuration(5_000, 0, 5_000),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        });

    [Theory]
    [InlineData(13743u, 0x40ul)]
    [InlineData(13875u, 0x40ul)]
    [InlineData(13976u, 0x700ul)]
    [InlineData(13979u, 0x700ul)]
    [InlineData(13980u, 0x700ul)]
    public void ClassMask_OfAnEmptyMaskTalent_IsTheBuiltInFixup(uint talent, ulong expected)
    {
        var engine = new SpellModEngine();
        SpellInfo spell = Talent(talent, 1);

        Assert.Equal(expected, engine.ClassMask(spell, 0));
    }

    [Fact]
    public void ClassMask_OfOtherSpells_StaysEmpty_AndTheOverlayWins()
    {
        var engine = new SpellModEngine();
        Assert.Equal(0ul, engine.ClassMask(Talent(OtherTalent, 1), 0));

        engine.MaskSource = new FixedMask(0x1_0000_0000);
        Assert.Equal(0x1_0000_0000ul, engine.ClassMask(Talent(ImprovedSprint, 1), 0));
    }

    [Fact]
    public void ImprovedSprint_TriggersSprintTriggerOnSprint_ButAnUnmaskedTalentDoesNot()
    {
        using SpellTestKit kit = NewKit();
        (Player rogue, _) = kit.AddPlayer(1);
        kit.System.CastSpell(rogue, OtherTalent, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(rogue, Sprint, SpellCastTargets.ForSelf(), triggered: true);
        Assert.False(kit.System.HasAura(rogue, SprintTrigger)); // no mask: no trigger (the unfixed behaviour of every talent)

        kit.System.RemoveAuras(rogue, OtherTalent);
        kit.System.RemoveAuras(rogue, Sprint);
        kit.System.CastSpell(rogue, ImprovedSprint, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(rogue, Sprint, SpellCastTargets.ForSelf(), triggered: true);

        Assert.True(kit.System.HasAura(rogue, SprintTrigger));
    }
}

using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// <c>spell/cooldown-invalid</c> is report-only (hardening wave H1): a cooldown above <see cref="SpellSystem.MaxReasonableCooldownMs"/> is
/// reported once and then applied exactly as the data says (the base clamped only at zero); a normal cooldown reports nothing.
/// </summary>
public sealed class SpellCooldownInvalidTests : IDisposable
{
    private const uint LongSpell = 946001;
    private const uint NormalSpell = 946002;
    private const uint LongCategorySpell = 946003;

    private readonly SpellTestKit _kit;
    private readonly Player _player;

    public SpellCooldownInvalidTests()
    {
        uint tooLong = SpellSystem.MaxReasonableCooldownMs + 86_400_000u;
        _kit = new SpellTestKit(
            Spell(LongSpell, Effect(SpellEffectName.Heal, 1, SpellImplicitTarget.UnitCaster)) with { RecoveryTime = tooLong, StartRecoveryCategory = 0, StartRecoveryTime = 0 },
            Spell(NormalSpell, Effect(SpellEffectName.Heal, 1, SpellImplicitTarget.UnitCaster)) with { RecoveryTime = 30_000, StartRecoveryCategory = 0, StartRecoveryTime = 0 },
            Spell(LongCategorySpell, Effect(SpellEffectName.Heal, 1, SpellImplicitTarget.UnitCaster))
                with { RecoveryTime = 0, Category = 77, CategoryRecoveryTime = tooLong, StartRecoveryCategory = 0, StartRecoveryTime = 0 });
        _kit.System.SpellBatchWindowMs = 0; // assumes same-call resolution: each cooldown is measured from the cast call
        (_player, _) = _kit.AddPlayer(1);
        _kit.Spellbook.Teach(_player, LongSpell, NormalSpell, LongCategorySpell);
    }

    public void Dispose() => _kit.Dispose();

    private static bool ForSpell(AnomalyNotice n, uint spell)
        => n.Key == "spell/cooldown-invalid" && n.Subject == SpellSystem.FormatSpellSubject(spell);

    [Fact]
    public void ACooldownOverTheThreshold_IsReportedOnce_AndIsNotCapped()
    {
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => ForSpell(n, LongSpell));
        uint castAt = _kit.Now;

        Assert.Equal(SpellCastResult.CastOk, _kit.System.CastSpell(_player, LongSpell, SpellCastTargets.ForSelf(), triggered: false));

        Assert.Equal(1, capture.Count("spell/cooldown-invalid"));
        uint until = _kit.System.GetState(_player.Guid)!.SpellCooldowns[LongSpell];
        Assert.Equal(SpellSystem.MaxReasonableCooldownMs + 86_400_000u, until - castAt);
    }

    [Fact]
    public void ACategoryCooldownOverTheThreshold_IsReportedOnce_AndIsNotCapped()
    {
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => ForSpell(n, LongCategorySpell));
        uint castAt = _kit.Now;

        Assert.Equal(SpellCastResult.CastOk, _kit.System.CastSpell(_player, LongCategorySpell, SpellCastTargets.ForSelf(), triggered: false));

        Assert.Equal(1, capture.Count("spell/cooldown-invalid"));
        uint until = _kit.System.GetState(_player.Guid)!.CategoryCooldowns[77];
        Assert.Equal(SpellSystem.MaxReasonableCooldownMs + 86_400_000u, until - castAt);
    }

    [Fact]
    public void ANormalCooldown_IsSilent_AndStartsExactly()
    {
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => ForSpell(n, NormalSpell));
        uint castAt = _kit.Now;

        Assert.Equal(SpellCastResult.CastOk, _kit.System.CastSpell(_player, NormalSpell, SpellCastTargets.ForSelf(), triggered: false));

        Assert.False(capture.Any("spell/cooldown-invalid"), capture.Describe());
        Assert.Equal(30_000u, _kit.System.GetState(_player.Guid)!.SpellCooldowns[NormalSpell] - castAt);
    }
}

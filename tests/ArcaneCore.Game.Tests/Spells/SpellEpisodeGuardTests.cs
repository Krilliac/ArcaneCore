using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// Episode guards of the spell anomaly reports (hardening wave H1): <c>channel-object-without-active-cast</c> is one report per episode
/// (cleared when the object is empty again) and <c>effect-unhandled</c> is one report per (kind, value, spell), whatever the number of casts.
/// </summary>
public sealed class SpellEpisodeGuardTests : IDisposable
{
    private const uint UnhandledSpell = 947001;
    private const uint HandledSpell = 947002;
    private const uint KeepsStateSpell = 947003;

    private readonly SpellTestKit _kit;
    private readonly Player _player;

    public SpellEpisodeGuardTests()
    {
        _kit = new SpellTestKit(
            Spell(UnhandledSpell, new SpellEffectInfo { Effect = (SpellEffectName)250, TargetA = SpellImplicitTarget.UnitCaster })
                with { StartRecoveryCategory = 0, StartRecoveryTime = 0 },
            Spell(HandledSpell, Effect(SpellEffectName.Heal, 1, SpellImplicitTarget.UnitCaster)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 },
            Spell(KeepsStateSpell, Effect(SpellEffectName.Heal, 1, SpellImplicitTarget.UnitCaster)) with { RecoveryTime = 600_000, StartRecoveryCategory = 0, StartRecoveryTime = 0 });
        (_player, _) = _kit.AddPlayer(1);
        _kit.Spellbook.Teach(_player, UnhandledSpell, HandledSpell, KeepsStateSpell);
    }

    public void Dispose() => _kit.Dispose();

    [Fact]
    public void AChannelObjectWithoutACast_IsReportedOncePerEpisode_AndAgainAfterItClears()
    {
        string subject = SpellSystem.FormatUnitSubject(_player);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "spell/channel-orphaned" && n.Subject == subject);
        _kit.System.CastSpell(_player, KeepsStateSpell, SpellCastTargets.ForSelf(), triggered: false); // a running cooldown keeps the unit's spell state alive
        _kit.Advance(200);
        Assert.False(capture.Any("spell/channel-orphaned"), capture.Describe());

        _player.SetUInt64(UpdateFields.UnitFieldChannelObject, 0xF1300000000042UL);
        _kit.Advance(1_000);
        Assert.Equal(1, capture.Count("spell/channel-orphaned"));

        _player.SetUInt64(UpdateFields.UnitFieldChannelObject, 0);
        _kit.Advance(200);
        _player.SetUInt64(UpdateFields.UnitFieldChannelObject, 0xF1300000000043UL);
        _kit.Advance(1_000);
        Assert.Equal(2, capture.Count("spell/channel-orphaned"));
    }

    [Fact]
    public void AnUnhandledEffect_IsReportedOncePerSpell_NotPerCast_AndAHandledOneIsSilent()
    {
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "spell/effect-unhandled"
            && (n.Subject == SpellSystem.FormatSpellSubject(UnhandledSpell) || n.Subject == SpellSystem.FormatSpellSubject(HandledSpell)));

        for (int i = 0; i < 3; i++)
        {
            _kit.System.CastSpell(_player, UnhandledSpell, SpellCastTargets.ForSelf(), triggered: false);
            _kit.Advance(200);
        }

        Assert.Equal(1, capture.Count("spell/effect-unhandled"));
        Assert.All(capture.Notices, n => Assert.Equal(SpellSystem.FormatSpellSubject(UnhandledSpell), n.Subject));

        _kit.System.CastSpell(_player, HandledSpell, SpellCastTargets.ForSelf(), triggered: false);
        Assert.Equal(1, capture.Count("spell/effect-unhandled"));
    }
}

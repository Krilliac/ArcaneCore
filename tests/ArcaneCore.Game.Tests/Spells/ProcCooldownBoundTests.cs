using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// A hidden proc cooldown is bounded like the cast path (<see cref="SpellSystem.MaxReasonableCooldownMs"/>): a value past 2^31 ms would make
/// the signed <c>IsStillRunning</c> compare read it as already expired, so the proc would have no cooldown at all.
/// </summary>
public sealed class ProcCooldownBoundTests : IDisposable
{
    private const uint ProcSpell = 949_501;

    private readonly SpellTestKit _kit;
    private readonly Player _unit;

    public ProcCooldownBoundTests()
    {
        _kit = new SpellTestKit(Spell(ProcSpell, Effect(SpellEffectName.SchoolDamage, 5)));
        (_unit, _) = _kit.AddPlayer(1, 0, 0);
    }

    public void Dispose() => _kit.Dispose();

    [Fact]
    public void HugeProcCooldownStaysRunning()
    {
        SpellInfo spell = _kit.System.Store.Get(ProcSpell)!;

        _kit.System.AddProcCooldown(_unit, spell, uint.MaxValue);

        Assert.False(_kit.System.IsProcSpellReady(_unit, spell));
    }

    [Fact]
    public void BoundedProcCooldownIsNotLonger()
    {
        SpellInfo spell = _kit.System.Store.Get(ProcSpell)!;

        _kit.System.AddProcCooldown(_unit, spell, SpellSystem.MaxReasonableCooldownMs + 1u);

        Assert.False(_kit.System.IsProcSpellReady(_unit, spell));
    }
}

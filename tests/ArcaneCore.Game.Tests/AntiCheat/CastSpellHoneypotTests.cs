using ArcaneCore.Game.AntiCheat;
using Xunit;

namespace ArcaneCore.Game.Tests.AntiCheat;

/// <summary>The spell honeypot decision: off by default, a signal for a non-GM only, the trap categories, and option validation.</summary>
public sealed class CastSpellHoneypotTests
{
    private const uint NeverCastSpell = 482; // R.NeverCast in DeliberatelyInertSpells.Researched
    private const uint DeprecatedSpell = 110; // R.Deprecated in DeliberatelyInertSpells
    private const uint MarkerSpell = 605; // R.Marker: inert but a legitimate cast, never a trap
    private const uint ToolSpell = 999_001;

    [Fact]
    public void FlagOff_IsTheDefault_AndNeverProducesAHoneypotSignal()
    {
        var options = new AntiCheatOptions();
        Assert.False(options.SpellHoneypot.Enabled);
        Assert.Null(CastSpellHoneypot.Evaluate(options.SpellHoneypot, NeverCastSpell, callerExempt: false));
        options.SpellHoneypot.ToolSpellIds.Add(ToolSpell);
        Assert.Null(CastSpellHoneypot.Evaluate(options.SpellHoneypot, ToolSpell, callerExempt: false));
    }

    [Theory]
    [InlineData(NeverCastSpell)]
    [InlineData(DeprecatedSpell)]
    [InlineData(ToolSpell)]
    public void FlagOn_NonGm_GetsAStrongSpellSignal(uint spellId)
    {
        var options = new AntiCheatOptions();
        options.SpellHoneypot.Enabled = true;
        options.SpellHoneypot.ToolSpellIds.Add(ToolSpell);
        AntiCheatFinding? finding = CastSpellHoneypot.Evaluate(options.SpellHoneypot, spellId, callerExempt: false);
        Assert.NotNull(finding);
        Assert.Equal(AntiCheatViolation.Spell, finding.Value.Type);
        Assert.Equal(options.SpellHoneypot.Weight, finding.Value.Weight);
        Assert.Equal(CastSpellHoneypot.Detail, finding.Value.Detail);
    }

    [Fact]
    public void FlagOn_AGmOrExemptCaller_IsNotFlagged()
    {
        var options = new AntiCheatOptions();
        options.SpellHoneypot.Enabled = true;
        Assert.Null(CastSpellHoneypot.Evaluate(options.SpellHoneypot, NeverCastSpell, callerExempt: true));
    }

    [Fact]
    public void FlagOn_ABuiltInToolId_IsATrapForANonGm_AndNotFlaggedForAnExemptCaller()
    {
        const uint qaDebugInstantCast = 27204; // GmFirstLoginToolCatalog: QADebug Instant Cast
        var catalogIds = new HashSet<uint> { qaDebugInstantCast };
        var options = new AntiCheatOptions();
        options.SpellHoneypot.Enabled = true;
        Assert.Null(CastSpellHoneypot.Evaluate(options.SpellHoneypot, qaDebugInstantCast, callerExempt: false));
        AntiCheatFinding? finding = CastSpellHoneypot.Evaluate(options.SpellHoneypot, qaDebugInstantCast, callerExempt: false, catalogIds);
        Assert.NotNull(finding);
        Assert.Equal(AntiCheatViolation.Spell, finding.Value.Type);
        Assert.Null(CastSpellHoneypot.Evaluate(options.SpellHoneypot, qaDebugInstantCast, callerExempt: true, catalogIds));
    }

    [Fact]
    public void FlagOn_AnOrdinaryOrInertButCastableSpell_IsNotATrap()
    {
        var options = new AntiCheatOptions();
        options.SpellHoneypot.Enabled = true;
        Assert.Null(CastSpellHoneypot.Evaluate(options.SpellHoneypot, MarkerSpell, callerExempt: false));
        Assert.Null(CastSpellHoneypot.Evaluate(options.SpellHoneypot, 133, callerExempt: false)); // Fireball
    }

    [Fact]
    public void Options_CloneCopiesTheToolList_AndValidateRejectsABadWeight()
    {
        var options = new AntiCheatOptions();
        options.SpellHoneypot.ToolSpellIds.Add(ToolSpell);
        AntiCheatOptions copy = options.Clone();
        copy.SpellHoneypot.ToolSpellIds.Add(5);
        Assert.Single(options.SpellHoneypot.ToolSpellIds);
        Assert.Empty(options.Validate());

        options.SpellHoneypot.Weight = 500f;
        Assert.Contains(options.Validate(), p => p.Contains("SpellHoneypot:Weight", StringComparison.Ordinal));
    }
}

using ArcaneCore.Game.Spells.Rules;
using Xunit;

namespace ArcaneCore.Game.Tests.SpellRules;

/// <summary>Golden bytes of the spell-rule packets (layouts from D:\refs\wow_messages smsg_*.md, 1.12 blocks).</summary>
public sealed class SpellRulePacketTests
{
    private static readonly ObjectGuid Caster = new(0x0102);
    private static readonly ObjectGuid Target = new(0x0000_0000_0003_0004);

    [Fact]
    public void SpellOrDamageImmune_IsGuidGuidSpellFlag()
    {
        byte[] bytes = SpellRulePackets.BuildSpellOrDamageImmune(Caster, Target, 133, debugLogFormat: true);

        Assert.Equal("02010000000000000400030000000000" + "85000000" + "01", Convert.ToHexString(bytes).ToLowerInvariant());
        Assert.Equal(21, bytes.Length);
        Assert.Equal(0, SpellRulePackets.BuildSpellOrDamageImmune(Caster, Target, 133)[^1]);
    }

    [Fact]
    public void SpellDispelLog_Uses_The112Layout_PackedGuidsCountAndSpellIds()
    {
        byte[] bytes = SpellRulePackets.BuildSpellDispelLog(Target, Caster, [1, 2]);

        // packed victim 0x30004: mask 0b101, bytes 04 03; packed caster 0x0102: mask 0b11, bytes 02 01; u32 2; spells 1, 2
        Assert.Equal("050403" + "030201" + "02000000" + "01000000" + "02000000", Convert.ToHexString(bytes).ToLowerInvariant());
    }

    [Fact]
    public void DispelFailed_IsGuidGuidThenSpellsToTheEnd()
    {
        byte[] bytes = SpellRulePackets.BuildDispelFailed(Caster, Target, [7, 8]);

        Assert.Equal(16 + 8, bytes.Length);
        Assert.Equal("0201000000000000" + "0400030000000000" + "07000000" + "08000000", Convert.ToHexString(bytes).ToLowerInvariant());
        Assert.Equal(16, SpellRulePackets.BuildDispelFailed(Caster, Target, []).Length);
    }
}

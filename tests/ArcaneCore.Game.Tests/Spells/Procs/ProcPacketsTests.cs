using ArcaneCore.Game.Spells.Procs;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells.Procs;

/// <summary>
/// Golden bytes of the proc engine packets. Layouts per vmangos Server/Packets/Spell.cpp:123-137 (SMSG_PROCRESIST: u64 caster,
/// u64 target, u32 spell, u8 log format 0) and Server/Packets/Combat.cpp:139-153 (SMSG_SPELLDAMAGESHIELD: u64 victim, u64 attacker,
/// u32 damage, u32 school). The bytes are written out by hand from those layouts.
/// </summary>
public sealed class ProcPacketsTests
{
    [Fact]
    public void ProcResist_IsCasterTargetSpellAndZeroLogFormat()
    {
        byte[] log = ProcPackets.BuildProcResist(new ObjectGuid(0x0807060504030201UL), new ObjectGuid(0x1817161514131211UL), 0x00010203);

        byte[] golden =
        [
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,   // caster
            0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,   // target
            0x03, 0x02, 0x01, 0x00,                           // spell
            0x00,                                             // log format
        ];
        Assert.Equal(21, golden.Length);
        Assert.Equal(golden, log);
    }

    [Fact]
    public void SpellDamageShield_IsVictimAttackerDamageSchool()
    {
        byte[] log = ProcPackets.BuildSpellDamageShield(new ObjectGuid(0x0807060504030201UL), new ObjectGuid(2), 0x01020304, 5);

        byte[] golden =
        [
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,   // shield bearer
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,   // attacker
            0x04, 0x03, 0x02, 0x01,                           // damage
            0x05, 0x00, 0x00, 0x00,                           // school
        ];
        Assert.Equal(24, golden.Length);
        Assert.Equal(golden, log);
    }
}

using ArcaneCore.Game.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells.Casters;

/// <summary>
/// Golden bytes of the periodic SMSG_SPELLNONMELEEDAMAGELOG that the drain auras send (1.12 layout per
/// wow_messages smsg_spellnonmeleedamagelog.wowm: packed target, packed caster, u32 spell, u32 damage, u8 school,
/// u32 absorbed, u32 resisted, u8 periodic_log, u8 unused, u32 blocked, u32 hit info, u8 extend flag). The bytes are
/// written out by hand from that layout, not produced by the builder.
/// </summary>
public sealed class PeriodicSpellDamageLogPacketTests
{
    [Fact]
    public void PeriodicLog_With6ByteGuids_Is42BytesAndMatchesGolden()
    {
        byte[] log = SpellPackets.BuildSpellNonMeleeDamageLog(
            new ObjectGuid(0x0000_0605_0403_0201UL), new ObjectGuid(0x0000_0C0B_0A09_0807UL),
            689, 9, SpellSchool.Shadow, absorbed: 0, resisted: 3, periodic: true);

        byte[] golden =
        [
            0x3F, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06,   // packed target
            0x3F, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C,   // packed caster
            0xB1, 0x02, 0x00, 0x00,                     // spell 689
            0x09, 0x00, 0x00, 0x00,                     // damage
            0x05,                                       // school: shadow
            0x00, 0x00, 0x00, 0x00,                     // absorbed
            0x03, 0x00, 0x00, 0x00,                     // resisted
            0x01,                                       // periodic_log
            0x00,                                       // unused
            0x00, 0x00, 0x00, 0x00,                     // blocked
            0x00, 0x00, 0x00, 0x00,                     // hit info
            0x00,                                       // extend flag
        ];
        Assert.Equal(42, golden.Length);
        Assert.Equal(golden, log);
    }

    [Fact]
    public void PeriodicLog_WithSmallGuids_UsesShortPackedGuids()
    {
        byte[] log = SpellPackets.BuildSpellNonMeleeDamageLog(
            new ObjectGuid(2), new ObjectGuid(1), 5001, 0x01020304, SpellSchool.Fire, absorbed: 7, resisted: 0, periodic: true);

        byte[] golden =
        [
            0x01, 0x02,                                 // packed target 2
            0x01, 0x01,                                 // packed caster 1
            0x89, 0x13, 0x00, 0x00,                     // spell 5001
            0x04, 0x03, 0x02, 0x01,                     // damage 0x01020304
            0x02,                                       // school: fire
            0x07, 0x00, 0x00, 0x00,                     // absorbed
            0x00, 0x00, 0x00, 0x00,                     // resisted
            0x01, 0x00,                                 // periodic_log, unused
            0x00, 0x00, 0x00, 0x00,                     // blocked
            0x00, 0x00, 0x00, 0x00,                     // hit info
            0x00,                                       // extend flag
        ];
        Assert.Equal(golden, log);
    }
}

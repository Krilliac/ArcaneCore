using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells.Mods;

/// <summary>
/// SMSG_SET_FLAT_SPELL_MODIFIER (0x266) and SMSG_SET_PCT_SPELL_MODIFIER (0x267): u8 class-mask bit, u8 operation, i32 value
/// (vmangos Player::SendSpellMod, Player.cpp:17650-17672; wow_messages smsg_set_flat_spell_modifier.wowm, versions 1 2 3). The
/// wow_messages layout of the pct message carries an unresolved "CORRECT_LAYOUT" note; vmangos writes both identically.
/// </summary>
public static class SpellModPackets
{
    public static WorldOpcode OpcodeOf(SpellModType type) => type == SpellModType.Flat ? WorldOpcode.SmsgSetFlatSpellModifier : WorldOpcode.SmsgSetPctSpellModifier;

    public static byte[] Build(int bit, SpellModOp op, int value)
    {
        var writer = new PacketWriter(6);
        writer.WriteByte((byte)bit);
        writer.WriteByte((byte)op);
        writer.WriteInt32(value);
        return writer.ToArray();
    }
}

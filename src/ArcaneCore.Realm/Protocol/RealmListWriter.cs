using ArcaneCore.Kernel.Realms;

namespace ArcaneCore.Realm.Protocol;

/// <summary>
/// Builds the CMD_REALM_LIST reply in the vanilla (build &lt; 6299) layout, verified
/// against vmangos AuthSocket.cpp LoadRealmlistAndWriteIntoBuffer:
///   cmd(0x10), size(u16 LE), then body:
///     u32 = 0 (unused), u8 realm count,
///     per realm: u32 type, u8 flags, cstring name, cstring address,
///                float population, u8 character count, u8 category, u8 0x00,
///     u16 = 0x0002 (trailing unused — vmangos uses 2, not 0; noted in MILESTONE_M1.md).
/// </summary>
public static class RealmListWriter
{
    public static ReadOnlyMemory<byte> Build(IReadOnlyList<RealmEntry> realms, byte charactersPerRealm)
    {
        var body = new PacketWriter(64 + (realms.Count * 48));
        body.WriteUInt32(0);
        body.WriteByte((byte)realms.Count);

        foreach (RealmEntry realm in realms)
        {
            body.WriteUInt32((uint)realm.Type);
            body.WriteByte((byte)realm.Flags);
            body.WriteCString(realm.Name);
            body.WriteCString(realm.Address);
            body.WriteSingle(realm.Population);
            body.WriteByte(charactersPerRealm);
            body.WriteByte(realm.Category);
            body.WriteByte(0x00); // unused (realm id placeholder in 1.x layout)
        }

        body.WriteUInt16(0x0002);

        ReadOnlyMemory<byte> bodyBytes = body.AsMemory();

        var packet = new PacketWriter(bodyBytes.Length + 3);
        packet.WriteByte((byte)AuthCommand.RealmList);
        packet.WriteUInt16((ushort)bodyBytes.Length);
        packet.WriteBytes(bodyBytes.Span);
        return packet.AsMemory();
    }
}

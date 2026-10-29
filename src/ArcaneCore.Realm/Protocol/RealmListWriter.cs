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
    /// <summary>The same count for every realm (kept for callers that have no per-realm source).</summary>
    public static ReadOnlyMemory<byte> Build(IReadOnlyList<RealmEntry> realms, byte charactersPerRealm)
        => Build(realms, _ => charactersPerRealm);

    /// <summary>
    /// The account's character count comes from <paramref name="charactersByRealm"/> (realm id to count; a realm without an entry shows 0,
    /// vmangos AuthSocket.cpp:1062-1070). The wire field is a byte: a count above 255 is sent as 255 (the client reads a byte; ten is the most a character screen lists).
    /// </summary>
    public static ReadOnlyMemory<byte> Build(IReadOnlyList<RealmEntry> realms, IReadOnlyDictionary<int, int> charactersByRealm)
    {
        ArgumentNullException.ThrowIfNull(charactersByRealm);
        return Build(realms, realmId => charactersByRealm.TryGetValue(realmId, out int count) ? (byte)Math.Clamp(count, 0, byte.MaxValue) : (byte)0);
    }

    private static ReadOnlyMemory<byte> Build(IReadOnlyList<RealmEntry> realms, Func<int, byte> charactersFor)
    {
        ArgumentNullException.ThrowIfNull(realms);
        // The count is a u8 on the wire; a larger list would wrap into a corrupt reply, so fail closed.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(realms.Count, byte.MaxValue);
        var body = new PacketWriter(64 + (realms.Count * 48));
        body.WriteUInt32(0);
        body.WriteByte((byte)realms.Count);

        foreach (RealmEntry realm in realms)
        {
            if (realm is null)
            {
                throw new ArgumentException("Realm list cannot contain null entries.", nameof(realms));
            }

            body.WriteUInt32((uint)realm.Type);
            body.WriteByte((byte)realm.Flags);
            body.WriteCString(realm.Name);
            body.WriteCString(realm.Address);
            body.WriteSingle(realm.Population);
            body.WriteByte(charactersFor(realm.Id));
            body.WriteByte(realm.Category);
            body.WriteByte(0x00); // unused (realm id placeholder in 1.x layout)
        }

        body.WriteUInt16(0x0002);

        ReadOnlyMemory<byte> bodyBytes = body.AsMemory();

        // The size field is a u16; an oversized body must not be truncated into it.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bodyBytes.Length, ushort.MaxValue);
        var packet = new PacketWriter(bodyBytes.Length + 3);
        packet.WriteByte((byte)AuthCommand.RealmList);
        packet.WriteUInt16((ushort)bodyBytes.Length);
        packet.WriteBytes(bodyBytes.Span);
        return packet.AsMemory();
    }
}

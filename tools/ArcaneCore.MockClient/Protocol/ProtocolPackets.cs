using System.Buffers.Binary;
using System.Text;

namespace ArcaneCore.MockClient.Protocol;

/// <summary>Independently encoded protocol 3 / build 5875 packets; no server serializers are used.</summary>
internal static class ProtocolPackets
{
    internal static string NormalizeAccount(string value) => Normalize(value, "account", 16);

    internal static string NormalizePassword(string value) => Normalize(value, "password", 16);

    private static string Normalize(string value, string parameter, int maximum)
    {
        ArgumentNullException.ThrowIfNull(value, parameter);
        if (value.Length is 0 || value.Length > maximum || value.Any(character => character is < ' ' or > '~'))
        {
            throw new ArgumentException($"{parameter} must contain 1 to {maximum} printable ASCII characters.", parameter);
        }

        return value.ToUpperInvariant();
    }

    internal static byte[] LogonChallenge(string account, ushort build)
    {
        byte[] name = Encoding.ASCII.GetBytes(account);
        byte[] packet = new byte[34 + name.Length];
        packet[0] = 0x00;
        packet[1] = 0x03;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)(30 + name.Length));
        "WoW\0"u8.CopyTo(packet.AsSpan(4));
        packet[8] = 1;
        packet[9] = 12;
        packet[10] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(11), build);
        "68x\0"u8.CopyTo(packet.AsSpan(13));
        "niW\0"u8.CopyTo(packet.AsSpan(17));
        "BGne"u8.CopyTo(packet.AsSpan(21));
        // timezone = 0; client IP is written as four octets, matching the login protocol.
        packet[29] = 127;
        packet[32] = 1;
        packet[33] = (byte)name.Length;
        name.CopyTo(packet, 34);
        return packet;
    }

    internal static byte[] LogonProof(ClientSrp6.Session session)
    {
        byte[] packet = new byte[75];
        packet[0] = 0x01;
        session.PublicKey.CopyTo(packet, 1);
        session.ClientProof.CopyTo(packet, 33);
        // crc_hash[20], telemetry count and protocol 3 security flags remain zero.
        return packet;
    }

    internal static byte[] WorldAuth(string account, byte[] sessionKey, uint clientSeed, uint serverSeed, uint build)
    {
        byte[] name = Encoding.ASCII.GetBytes(account);
        byte[] payload = new byte[8 + name.Length + 1 + 4 + 20 + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, build);
        name.CopyTo(payload, 8);
        int seedOffset = 9 + name.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(seedOffset), clientSeed);
        WorldProof(account, sessionKey, clientSeed, serverSeed).CopyTo(payload, seedOffset + 4);
        // server id = 0; uncompressed addon size = 0 represents an empty addon list.
        return payload;
    }

    internal static byte[] WorldProof(string account, byte[] sessionKey, uint clientSeed, uint serverSeed)
    {
        byte[] seeds = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(seeds.AsSpan(4), clientSeed);
        BinaryPrimitives.WriteUInt32LittleEndian(seeds.AsSpan(8), serverSeed);
        return ClientSrp6.Hash(Encoding.ASCII.GetBytes(account), seeds, sessionKey);
    }

    internal static byte[] ClientHeader(ushort opcode, int payloadLength)
    {
        byte[] header = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(header, checked((ushort)(payloadLength + 4)));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(2), opcode);
        return header;
    }

    internal static IReadOnlyList<MockRealm> RealmList(byte[] body)
    {
        var cursor = new ByteCursor(body);
        cursor.Skip(4);
        int count = cursor.Byte();
        if (count > 128)
        {
            throw new MockProtocolException($"Realm list contains {count} entries; maximum is 128.");
        }

        var realms = new List<MockRealm>(count);
        for (int index = 0; index < count; index++)
        {
            cursor.Skip(5); // u32 realm type, u8 flags
            string name = cursor.CString(512);
            string address = cursor.CString(256);
            cursor.Skip(7); // float population, character count, category, realm id
            realms.Add(new MockRealm(name, address));
        }

        cursor.Skip(2); // trailing unused uint16
        if (cursor.Remaining != 0)
        {
            throw new MockProtocolException($"Realm list has {cursor.Remaining} unexpected trailing bytes.");
        }

        return realms.AsReadOnly();
    }

    private sealed class ByteCursor(byte[] body)
    {
        private int _position;
        internal int Remaining => body.Length - _position;

        internal byte Byte()
        {
            Require(1);
            return body[_position++];
        }

        internal void Skip(int count)
        {
            Require(count);
            _position += count;
        }

        internal string CString(int maximum)
        {
            int end = Array.IndexOf(body, (byte)0, _position);
            if (end < 0 || end - _position > maximum)
            {
                throw new MockProtocolException("Realm list contains an unterminated or oversized string.");
            }

            ReadOnlySpan<byte> bytes = body.AsSpan(_position, end - _position);
            if (bytes.ContainsAnyInRange((byte)128, byte.MaxValue))
            {
                throw new MockProtocolException("Realm list contains a non-ASCII string.");
            }

            string value = Encoding.ASCII.GetString(bytes);
            _position = end + 1;
            return value;
        }

        private void Require(int count)
        {
            if (Remaining < count)
            {
                throw new MockProtocolException($"Realm list is truncated at byte {_position}.");
            }
        }
    }
}

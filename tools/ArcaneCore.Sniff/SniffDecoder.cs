using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using ArcaneCore.Protocol;
using WowWorldMessages.Vanilla;

namespace ArcaneCore.Sniff;

/// <summary>Offline PKT 3.1 reader. Each record is decoded through the vendored vanilla wire oracle.</summary>
public static class SniffDecoder
{
    public static async Task<int> DecodeAsync(string path, TextWriter output)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(file, Encoding.ASCII, leaveOpen: true);
        if (file.Length < 66 || Encoding.ASCII.GetString(reader.ReadBytes(3)) != "PKT" || reader.ReadUInt16() != 0x301)
            throw new InvalidDataException("decode failed at byte 0: expected PKT 3.1");
        _ = reader.ReadByte(); // sniffer id
        if (reader.ReadUInt32() != 5875) throw new InvalidDataException($"decode failed at byte {file.Position - 4}: expected build 5875");
        _ = reader.ReadBytes(4); // locale
        _ = reader.ReadBytes(40); // no session key in ArcaneCore files
        uint startUnix = reader.ReadUInt32();
        uint startTick = reader.ReadUInt32();
        int headerExtra = reader.ReadInt32();
        if (headerExtra < 0 || headerExtra > file.Length - file.Position) throw new InvalidDataException($"decode failed at byte {file.Position - 4}: bad header length");
        file.Position += headerExtra;
        int errors = 0;
        byte[] header = new byte[6];
        while (file.Position < file.Length)
        {
            if (file.Length - file.Position < 20) throw new InvalidDataException($"decode failed at byte {file.Position}: truncated record header");
            string direction = Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (direction is not ("SMSG" or "CMSG")) throw new InvalidDataException($"decode failed at byte {file.Position - 4}: invalid record direction");
            int session = reader.ReadInt32();
            uint tick = reader.ReadUInt32();
            int extra = reader.ReadInt32();
            int size = reader.ReadInt32();
            // The world header's size field is 16-bit and counts the opcode (2 bytes SMSG, 4 bytes CMSG): no larger payload is a vanilla packet.
            int maxSize = direction == "SMSG" ? ushort.MaxValue + 2 : ushort.MaxValue;
            if (extra < 0 || size < 4 || size > maxSize || (long)extra + size > file.Length - file.Position)
                throw new InvalidDataException($"decode failed at byte {file.Position - 4}: invalid record size");
            file.Position += extra;
            int opcode = reader.ReadInt32();
            if (opcode is < 0 or > ushort.MaxValue) throw new InvalidDataException($"decode failed at byte {file.Position - 4}: opcode 0x{opcode:X} is not a 16-bit world opcode");
            byte[] payload = reader.ReadBytes(size - 4);
            if (payload.Length != size - 4) throw new InvalidDataException($"decode failed at byte {file.Position}: truncated record body");
            DateTimeOffset time = DateTimeOffset.FromUnixTimeSeconds(startUnix).AddMilliseconds(unchecked(tick - startTick));
            string name = WorldOpcodeNames.GetName((WorldOpcode)opcode);
            using var frame = new MemoryStream();
            BinaryPrimitives.WriteUInt16BigEndian(header, checked((ushort)(payload.Length + (direction == "SMSG" ? 2 : 4))));
            if (direction == "SMSG") BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), checked((ushort)opcode));
            else BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(2), checked((uint)opcode));
            frame.Write(header.AsSpan(0, direction == "SMSG" ? 4 : 6));
            frame.Write(payload);
            frame.Position = 0;
            try
            {
                object decoded = direction == "SMSG"
                    ? await ServerOpcodeReader.ReadUnencryptedAsync(frame)
                    : await ClientOpcodeReader.ReadUnencryptedAsync(frame);
                if (frame.Position != frame.Length) throw new InvalidDataException("trailing payload bytes");
                await output.WriteLineAsync($"{time:O} {direction} {name} size={payload.Length} session={session} {JsonSerializer.Serialize(decoded, decoded.GetType())}");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                errors++;
                await output.WriteLineAsync($"{time:O} {direction} {name} size={payload.Length} decode failed at byte {Math.Max(0, frame.Position - (direction == "SMSG" ? 4 : 6))}: {ex.Message}");
            }
        }
        return errors == 0 ? 0 : 1;
    }
}

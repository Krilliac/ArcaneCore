using System.Buffers.Binary;
using System.Numerics;

namespace ArcaneCore.World.Tests;

/// <summary>
/// Structural fallback for update objects containing a 1.12.1 transport movement block.
/// vmangos Object::BuildMovementUpdate (Objects/Object.cpp) writes MovementInfo with a fixed
/// u64 transport GUID and four floats when MOVEFLAG_ONTRANSPORT is 0x02000000; wowm gates
/// a packed transport GUID on 0x00000200. Ordinary updates stay on wowm's reader.
/// </summary>
internal static class VmangosTransportUpdateValidator
{
    public static bool Validate(ReadOnlySpan<byte> body)
    {
        var cursor = new Cursor(body);
        uint count = cursor.UInt32();
        if (count > 100_000) throw new InvalidDataException("update block count is excessive");
        byte hasTransport = cursor.Byte();
        if (hasTransport > 1) throw new InvalidDataException("invalid has-transport byte");
        bool sawTransportMovement = false;
        for (uint i = 0; i < count; i++)
        {
            byte kind = cursor.Byte();
            switch (kind)
            {
                case 0: // values
                    cursor.PackedGuid();
                    cursor.UpdateMask();
                    break;
                case 1: // movement
                    cursor.PackedGuid();
                    cursor.MovementBlock(ref sawTransportMovement);
                    break;
                case 2: // create
                case 3: // create2
                    cursor.PackedGuid();
                    cursor.Byte(); // object type
                    cursor.MovementBlock(ref sawTransportMovement);
                    cursor.UpdateMask();
                    break;
                case 4: // out of range
                case 5: // near objects
                    uint guidCount = cursor.UInt32();
                    if (guidCount > body.Length) throw new InvalidDataException("GUID count exceeds packet length");
                    for (uint j = 0; j < guidCount; j++) cursor.PackedGuid();
                    break;
                default:
                    throw new InvalidDataException($"unknown update type {kind}");
            }
        }

        if (cursor.Position != body.Length)
            throw new InvalidDataException($"{body.Length - cursor.Position} trailing update bytes");
        return sawTransportMovement;
    }

    private ref struct Cursor(ReadOnlySpan<byte> body)
    {
        private readonly ReadOnlySpan<byte> _body = body;
        public int Position { get; private set; }

        public byte Byte()
        {
            Need(1);
            return _body[Position++];
        }

        public uint UInt32()
        {
            Need(4);
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(_body[Position..]);
            Position += 4;
            return value;
        }

        public void Skip(int bytes)
        {
            Need(bytes);
            Position += bytes;
        }

        public void PackedGuid() => Skip(BitOperations.PopCount((uint)Byte()));

        public void UpdateMask()
        {
            int blocks = Byte();
            if (blocks > 64) throw new InvalidDataException("update mask has too many blocks");
            int values = 0;
            for (int i = 0; i < blocks; i++) values += BitOperations.PopCount(UInt32());
            Skip(checked(values * 4));
        }

        public void MovementBlock(ref bool sawTransportMovement)
        {
            byte updateFlags = Byte();
            if ((updateFlags & 0x20) != 0) // living
            {
                uint movementFlags = UInt32();
                Skip(4 + 16); // server time and x/y/z/orientation
                if ((movementFlags & 0x02000000) != 0)
                {
                    Skip(8 + 16); // fixed u64 transport GUID and four floats
                    sawTransportMovement = true;
                }
                if ((movementFlags & 0x00200000) != 0) Skip(4); // swimming pitch
                Skip(4); // fall time
                if ((movementFlags & 0x00002000) != 0) Skip(16); // jump vector
                if ((movementFlags & 0x04000000) != 0) Skip(4); // spline elevation
                Skip(6 * 4); // walk, run, backward run, swim, backward swim, turn
                if ((movementFlags & 0x00400000) != 0)
                    throw new InvalidDataException("transport fallback does not model spline create data");
            }
            else if ((updateFlags & 0x40) != 0) Skip(16); // stationary position
            if ((updateFlags & 0x08) != 0) Skip(4); // high GUID
            if ((updateFlags & 0x10) != 0) Skip(4); // all
            if ((updateFlags & 0x04) != 0) PackedGuid(); // melee victim
            if ((updateFlags & 0x02) != 0) Skip(4); // transport path progress
        }

        private void Need(int bytes)
        {
            if (bytes < 0 || bytes > _body.Length - Position)
                throw new InvalidDataException("truncated vmangos update object");
        }
    }
}

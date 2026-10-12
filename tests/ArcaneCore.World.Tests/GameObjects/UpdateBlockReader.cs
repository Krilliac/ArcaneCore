using ArcaneCore.Game;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Tests.GameObjects;

/// <summary>One create or values block of an SMSG_UPDATE_OBJECT, with its set fields by index.</summary>
internal sealed record UpdateBlock(ObjectUpdateType Type, ulong Guid, Dictionary<int, uint> Values);

/// <summary>Parses the create and values blocks of an update packet by their update masks (no byte scanning).</summary>
internal static class UpdateBlockReader
{
    public static List<UpdateBlock> Parse(byte[] payload)
    {
        var reader = new PacketReader(payload);
        uint count = reader.ReadUInt32();
        reader.ReadByte(); // hasTransport
        var blocks = new List<UpdateBlock>();
        for (uint i = 0; i < count; i++)
        {
            var type = (ObjectUpdateType)reader.ReadByte();
            switch (type)
            {
                case ObjectUpdateType.OutOfRangeObjects:
                    uint n = reader.ReadUInt32();
                    for (uint j = 0; j < n; j++)
                    {
                        reader.ReadPackedGuid();
                    }

                    break;

                case ObjectUpdateType.Values:
                    ulong valuesGuid = reader.ReadPackedGuid();
                    blocks.Add(new UpdateBlock(type, valuesGuid, ReadMaskAndValues(ref reader)));
                    break;

                case ObjectUpdateType.CreateObject:
                case ObjectUpdateType.CreateObject2:
                    ulong guid = reader.ReadPackedGuid();
                    reader.ReadByte(); // type id
                    var flags = (ObjectUpdateFlags)reader.ReadByte();
                    if ((flags & ObjectUpdateFlags.Living) != 0)
                    {
                        MovementInfo.Read(ref reader);
                        reader.Skip(6 * 4);
                    }
                    else if ((flags & ObjectUpdateFlags.HasPosition) != 0)
                    {
                        reader.Skip(4 * 4);
                    }

                    if ((flags & ObjectUpdateFlags.HighGuid) != 0)
                    {
                        reader.Skip(4);
                    }

                    if ((flags & ObjectUpdateFlags.All) != 0)
                    {
                        reader.Skip(4);
                    }

                    if ((flags & ObjectUpdateFlags.MeleeAttacking) != 0)
                    {
                        reader.ReadPackedGuid();
                    }

                    if ((flags & ObjectUpdateFlags.Transport) != 0)
                    {
                        reader.Skip(4);
                    }

                    blocks.Add(new UpdateBlock(type, guid, ReadMaskAndValues(ref reader)));
                    break;

                default:
                    throw new InvalidOperationException($"unexpected update block type {type}");
            }
        }

        return blocks;
    }

    private static Dictionary<int, uint> ReadMaskAndValues(ref PacketReader reader)
    {
        int blocks = reader.ReadByte();
        uint[] mask = new uint[blocks];
        for (int i = 0; i < blocks; i++)
        {
            mask[i] = reader.ReadUInt32();
        }

        var values = new Dictionary<int, uint>();
        for (int index = 0; index < blocks * 32; index++)
        {
            if ((mask[index >> 5] & (1u << (index & 31))) != 0)
            {
                values[index] = reader.ReadUInt32();
            }
        }

        return values;
    }
}

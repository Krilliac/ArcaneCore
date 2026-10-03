using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests;

/// <summary>One block of a parsed SMSG_UPDATE_OBJECT.</summary>
internal sealed record ParsedBlock(ObjectUpdateType Type, IReadOnlyList<ulong> Guids, byte TypeId, MovementInfo? Movement, Dictionary<int, uint> Values);

/// <summary>Builders and packet parsers for the creature tests.</summary>
internal static class CreatureTestSupport
{
    public const uint WolfEntry = 299;      // "Young Wolf" in vanilla data; any id works here
    public const uint GuardEntry = 68;

    public static CreatureTemplate Template(uint entry = WolfEntry, Action<CreatureTemplateBuilder>? configure = null)
    {
        var b = new CreatureTemplateBuilder { Entry = entry };
        configure?.Invoke(b);
        return b.Build();
    }

    public static CreatureSpawn Spawn(uint guid, uint entry, float x, float y, float z = 83.5f, uint mapId = 0, byte movementType = 0, float wander = 0, uint respawnSeconds = 120)
        => new()
        {
            Guid = guid,
            Entry = entry,
            MapId = mapId,
            X = x,
            Y = y,
            Z = z,
            Orientation = 1.5f,
            SpawnTimeMinSeconds = respawnSeconds,
            SpawnTimeMaxSeconds = respawnSeconds,
            WanderDistance = wander,
            MovementType = movementType,
        };

    public static CreatureContent Content(
        IEnumerable<CreatureTemplate> templates, IEnumerable<CreatureSpawn> spawns,
        IEnumerable<(uint, CreatureWaypoint)>? waypoints = null, IEnumerable<CreatureModelInfo>? models = null,
        IEnumerable<CreatureAddon>? addons = null, IEnumerable<(uint Entry, uint PathId, CreatureWaypoint Point)>? entryWaypoints = null)
        => new(templates, spawns, waypoints ?? [], models ?? [], addons ?? [], entryWaypoints: entryWaypoints);

    public static (WorldRuntime World, Map Map, CreatureMapSystem System) CreateSystem(CreatureContent content, CreatureOptions? options = null, int seed = 1)
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var system = new CreatureMapSystem(map, content, options, random: new Random(seed));
        map.AddUpdater(system);
        return (world, map, system);
    }

    /// <summary>Every SMSG_UPDATE_OBJECT block the session received (in order), draining the queue of other packets into <paramref name="others"/>.</summary>
    public static List<ParsedBlock> DrainBlocks(FakeSession session, List<(WorldOpcode Opcode, byte[] Payload)>? others = null)
    {
        var blocks = new List<ParsedBlock>();
        while (session.Sent.TryDequeue(out var packet))
        {
            if (packet.Opcode == WorldOpcode.SmsgUpdateObject)
            {
                blocks.AddRange(ParseUpdateObject(packet.Payload));
            }
            else
            {
                others?.Add(packet);
            }
        }

        return blocks;
    }

    public static List<ParsedBlock> ParseUpdateObject(byte[] payload)
    {
        var reader = new PacketReader(payload);
        uint count = reader.ReadUInt32();
        Assert.Equal(0, reader.ReadByte()); // hasTransport
        var blocks = new List<ParsedBlock>();
        for (uint i = 0; i < count; i++)
        {
            var type = (ObjectUpdateType)reader.ReadByte();
            switch (type)
            {
                case ObjectUpdateType.OutOfRangeObjects:
                {
                    uint n = reader.ReadUInt32();
                    var guids = new List<ulong>();
                    for (uint j = 0; j < n; j++)
                    {
                        guids.Add(reader.ReadPackedGuid());
                    }

                    blocks.Add(new ParsedBlock(type, guids, 0, null, []));
                    break;
                }

                case ObjectUpdateType.Values:
                {
                    ulong guid = reader.ReadPackedGuid();
                    blocks.Add(new ParsedBlock(type, [guid], 0, null, ReadMaskAndValues(ref reader)));
                    break;
                }

                case ObjectUpdateType.CreateObject:
                case ObjectUpdateType.CreateObject2:
                {
                    ulong guid = reader.ReadPackedGuid();
                    byte typeId = reader.ReadByte();
                    var flags = (ObjectUpdateFlags)reader.ReadByte();
                    MovementInfo? movement = null;
                    if ((flags & ObjectUpdateFlags.Living) != 0)
                    {
                        movement = MovementInfo.Read(ref reader);
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

                    blocks.Add(new ParsedBlock(type, [guid], typeId, movement, ReadMaskAndValues(ref reader)));
                    break;
                }

                default:
                    throw new InvalidOperationException($"unexpected block type {type}");
            }
        }

        Assert.Equal(0, reader.Remaining);
        return blocks;
    }

    /// <summary>A parsed linear SMSG_MONSTER_MOVE.</summary>
    public static MonsterMove ParseMonsterMove(byte[] payload)
    {
        var reader = new PacketReader(payload);
        ulong guid = reader.ReadPackedGuid();
        float sx = reader.ReadSingle();
        float sy = reader.ReadSingle();
        float sz = reader.ReadSingle();
        uint id = reader.ReadUInt32();
        var type = (MonsterMoveType)reader.ReadByte();
        if (type == MonsterMoveType.Stop)
        {
            Assert.Equal(0, reader.Remaining);
            return new MonsterMove(guid, sx, sy, sz, id, type, null, 0, 0, 0, 0, 0, 0);
        }

        float? angle = type == MonsterMoveType.FacingAngle ? reader.ReadSingle() : null;
        uint flags = reader.ReadUInt32();
        uint duration = reader.ReadUInt32();
        uint points = reader.ReadUInt32();
        float dx = reader.ReadSingle();
        float dy = reader.ReadSingle();
        float dz = reader.ReadSingle();
        Assert.Equal(0, reader.Remaining);
        return new MonsterMove(guid, sx, sy, sz, id, type, angle, flags, duration, points, dx, dy, dz);
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

internal sealed record MonsterMove(
    ulong Guid, float StartX, float StartY, float StartZ, uint SplineId, MonsterMoveType Type, float? FacingAngle,
    uint Flags, uint DurationMs, uint PointCount, float DestX, float DestY, float DestZ);

/// <summary>Mutable builder for <see cref="CreatureTemplate"/> in tests.</summary>
internal sealed class CreatureTemplateBuilder
{
    public uint Entry { get; set; }
    public string Name { get; set; } = "Young Wolf";
    public byte MinLevel { get; set; } = 2;
    public byte MaxLevel { get; set; } = 2;
    public uint[] DisplayIds { get; set; } = [903];
    public uint[] DisplayProbabilities { get; set; } = [];
    public uint Faction { get; set; } = 32;
    public uint Rank { get; set; }
    public byte UnitClass { get; set; } = 1;
    public uint MinLevelHealth { get; set; } = 55;
    public uint MaxLevelHealth { get; set; } = 55;
    public uint MinLevelMana { get; set; }
    public uint MaxLevelMana { get; set; }
    public uint NpcFlags { get; set; }
    public uint UnitFlags { get; set; }
    public uint ExtraFlags { get; set; }
    public uint CorpseDecaySeconds { get; set; }
    public float Scale { get; set; }
    public string AIName { get; set; } = string.Empty;
    public bool Civilian { get; set; }

    public CreatureTemplate Build() => new()
    {
        Entry = Entry,
        Name = Name,
        SubName = string.Empty,
        MinLevel = MinLevel,
        MaxLevel = MaxLevel,
        DisplayIds = DisplayIds,
        DisplayProbabilities = DisplayProbabilities,
        Faction = Faction,
        Rank = Rank,
        UnitClass = UnitClass,
        MinLevelHealth = MinLevelHealth,
        MaxLevelHealth = MaxLevelHealth,
        MinLevelMana = MinLevelMana,
        MaxLevelMana = MaxLevelMana,
        NpcFlags = NpcFlags,
        UnitFlags = UnitFlags,
        ExtraFlags = ExtraFlags,
        CorpseDecaySeconds = CorpseDecaySeconds,
        Scale = Scale,
        CreatureType = 1,
        Family = 1,
        AIName = AIName,
        Civilian = Civilian,
    };
}

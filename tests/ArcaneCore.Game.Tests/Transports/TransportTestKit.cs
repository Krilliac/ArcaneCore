using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Transports;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Tests.Transports;

/// <summary>
/// Synthetic ship routes (no client data on this machine): straight lines along +x at speed 10 yd/s and acceleration 1 yd/s²
/// (accelDist 50 yd, accelTime 10 s), so every key-frame time can be worked out by hand.
/// <list type="bullet">
/// <item><see cref="Ferry"/> (path <see cref="FerryPath"/>, map 0): stop at x=100 (10 s), x=200, x=300, stop at x=400 (10 s).
/// Key frames are nodes 1-4: depart 10 s, x=200 at 25 s, x=300 at 35 s, arrive 50 s, period 60 s.</item>
/// <item><see cref="Crossing"/> (path <see cref="CrossingPath"/>): map 0 stop at x=100 (5 s), x=200, x=300, then a map change and map 1 stop
/// at x=1100 (5 s), x=1200, x=1300. Depart 5 s, x=200 at 20 s, reaches x=300 at 35 s and jumps to map 1 x=1100, departs 40 s,
/// x=1200 at 55 s, x=1300 at 70 s = period, then back to map 0.</item>
/// </list>
/// </summary>
internal static class TransportTestKit
{
    public const uint Ferry = 990500;
    public const uint Crossing = 990501;
    public const uint FerryPath = 9001;
    public const uint CrossingPath = 9002;
    public const uint Speed = 10;
    public const uint Accel = 1;

    public static GameObjectTemplate Ship(uint entry, uint path, uint speed = Speed, uint accel = Accel)
    {
        uint[] data = new uint[GameObjectTemplate.DataCount];
        data[0] = path;
        data[1] = speed;
        data[2] = accel;
        return new GameObjectTemplate
        {
            Entry = entry, Type = TransportTemplateBuilder.MoTransportType, DisplayId = 3031, Name = "Synthetic ship " + entry,
            Faction = 35, Flags = 0x28, Size = 1f, Data = data,
        };
    }

    public static TaxiPathNodeRecord Node(uint path, uint index, uint map, float x, uint flags = 0, uint delay = 0)
        => new(path * 100 + index, path, index, map, x, 0f, 0f, flags, delay);

    public static TaxiPathNodeCatalog Paths() => new(
    [
        Node(FerryPath, 0, 0, 0),
        Node(FerryPath, 1, 0, 100, flags: 2, delay: 10),
        Node(FerryPath, 2, 0, 200),
        Node(FerryPath, 3, 0, 300),
        Node(FerryPath, 4, 0, 400, flags: 2, delay: 10),
        Node(FerryPath, 5, 0, 500),

        Node(CrossingPath, 0, 0, 0),
        Node(CrossingPath, 1, 0, 100, flags: 2, delay: 5),
        Node(CrossingPath, 2, 0, 200),
        Node(CrossingPath, 3, 0, 300),
        Node(CrossingPath, 4, 0, 400),
        Node(CrossingPath, 5, 1, 1000),
        Node(CrossingPath, 6, 1, 1100, flags: 2, delay: 5),
        Node(CrossingPath, 7, 1, 1200),
        Node(CrossingPath, 8, 1, 1300),
        Node(CrossingPath, 9, 1, 1400),
    ]);

    public static TransportTemplate Build(uint entry, uint? period = null)
    {
        uint path = entry == Ferry ? FerryPath : CrossingPath;
        return TransportTemplateBuilder.Build(Ship(entry, path), Paths(), _ => false, period, out TransportTemplateError error)
            ?? throw new InvalidOperationException("synthetic route refused: " + error);
    }

    /// <summary>A world on the manual clock (game time = sum of tick diffs, starting at 0).</summary>
    public static WorldRuntime ManualWorld()
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        world.UseManualClock();
        return world;
    }

    /// <summary>Install a transport system with the given routes on <paramref name="world"/> (world thread = the test thread).</summary>
    public static TransportSystem Install(WorldRuntime world, params uint[] entries)
    {
        var system = new TransportSystem(world, entries.Select(e => Build(e)));
        TransportSystem.Register(world, system);
        system.Install();
        return system;
    }

    /// <summary>Run ticks of 50 ms until <paramref name="milliseconds"/> of game time have passed.</summary>
    public static void Advance(WorldRuntime world, uint milliseconds)
    {
        for (uint done = 0; done < milliseconds; done += 50)
        {
            world.RunTick(Math.Min(50, milliseconds - done));
        }
    }

    /// <summary>A client movement block standing on <paramref name="ship"/> at the given offset (world fields as the client sends them).</summary>
    public static MovementInfo Aboard(ShipTransport ship, float offsetX, float offsetY = 0, float offsetZ = 0, float offsetO = 0)
    {
        float x = offsetX, y = offsetY, z = offsetZ, o = offsetO;
        ship.CalculatePassengerPosition(ref x, ref y, ref z, ref o);
        return new MovementInfo
        {
            Flags = MovementFlags.OnTransport, X = x, Y = y, Z = z, Orientation = o,
            TransportGuid = ship.Guid.Value, TransportX = offsetX, TransportY = offsetY, TransportZ = offsetZ, TransportOrientation = offsetO,
        };
    }

    /// <summary>
    /// The player's own create blocks (CREATE_OBJECT, its GUID, TYPEID_PLAYER, update flags with SELF) found in the
    /// SMSG_UPDATE_OBJECT packets <paramref name="session"/> received (compression is off in the test world): for each, the
    /// index of the packet, its has-transport byte, the GUID of the packet's first block and the movement block sent.
    /// </summary>
    public static List<SelfCreate> SelfCreates(FakeSession session, Player player)
    {
        var guid = new PacketWriter(9);
        guid.WritePackedGuid(player.Guid.Value);
        byte[] header = [(byte)ObjectUpdateType.CreateObject, .. guid.ToArray(), TypeId.Player];
        var found = new List<SelfCreate>();
        (WorldOpcode Opcode, byte[] Payload)[] sent = session.Sent.ToArray();
        for (int index = 0; index < sent.Length; index++)
        {
            if (sent[index].Opcode != WorldOpcode.SmsgUpdateObject)
            {
                continue;
            }

            byte[] payload = sent[index].Payload;
            int at = payload.AsSpan(5).IndexOf(header);
            while (at >= 0)
            {
                int flagsAt = 5 + at + header.Length;
                if ((payload[flagsAt] & (byte)ObjectUpdateFlags.Self) != 0)
                {
                    var first = new PacketReader(payload.AsSpan(5));
                    first.ReadByte();
                    ulong firstGuid = first.ReadPackedGuid();
                    var movement = new PacketReader(payload.AsSpan(flagsAt + 1));
                    found.Add(new SelfCreate(index, payload[4], firstGuid, MovementInfo.Read(ref movement)));
                }

                int next = payload.AsSpan(flagsAt).IndexOf(header);
                at = next < 0 ? -1 : flagsAt - 5 + next;
            }
        }

        return found;
    }

    /// <summary>One create block of the player for itself (see <see cref="SelfCreates"/>).</summary>
    public readonly record struct SelfCreate(int PacketIndex, byte HasTransport, ulong FirstBlockGuid, MovementInfo Movement);

    /// <summary>The SMSG_UPDATE_OBJECT payloads <paramref name="session"/> received with the has-transport byte set.</summary>
    public static List<byte[]> TransportUpdates(FakeSession session)
        => [.. session.Sent.ToArray().Where(p => p.Opcode == WorldOpcode.SmsgUpdateObject && p.Payload.Length > 4 && p.Payload[4] == 1).Select(p => p.Payload)];
}

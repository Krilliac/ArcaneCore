using ArcaneCore.Protocol;

namespace ArcaneCore.World.Ops.Lifecycle;

/// <summary>SMSG_SERVER_MESSAGE (0x291): u32 type, then a NUL-terminated string (wow_messages smsg_server_message.wowm, 1.12).</summary>
public static class ServerMessagePackets
{
    public static byte[] Build(ServerMessage message)
    {
        var writer = new PacketWriter();
        writer.WriteUInt32((uint)message.Type);
        writer.WriteCString(message.Text);
        return writer.ToArray();
    }
}

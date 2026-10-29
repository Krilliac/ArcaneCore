using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Honor;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Honor;

/// <summary>
/// CMSG_INSPECT and MSG_INSPECT_HONOR_STATS (vmangos MiscHandler.cpp:943-1036). Both take the target's u64 guid and answer only for
/// a player on the same map within 10 yards (INSPECT_DISTANCE, ObjectDefines.h:26) that the inspector could not attack: an
/// enemy-faction player, or anyone unreachable, gets no reply (the original returns silently). CMSG_INSPECT also selects the
/// target, and its answer is just the guid (the equipment is read by the client from the update fields). The honor statistics are the
/// 50-byte <see cref="HonorPackets.InspectHonorStats"/> read from the target's honor update fields, so they work for whatever the
/// honor feature published (zeros without it).
/// A real player inspecting a playerbot is also announced to the <see cref="IPlayerInspectReceiver"/> features, after the reply.
/// </summary>
public sealed class InspectHandlers : IOpcodeHandlerGroup
{
    /// <summary>INSPECT_DISTANCE: yards between the two players' centres.</summary>
    public const float InspectDistance = 10.0f;

    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgInspect, HandleInspect);
        table.OnWorld(WorldOpcode.MsgInspectHonorStats, HandleInspectHonorStats);
    }

    /// <summary>The player an inspect request may be answered for, or null (HandleInspectOpcode checks, MiscHandler.cpp:949-956).</summary>
    public static Player? InspectableTarget(Player inspector, ObjectGuid guid)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        if (inspector.Map is not { } map || map.FindPlayer(guid) is not { } target)
        {
            return null;
        }

        float dx = inspector.X - target.X;
        float dy = inspector.Y - target.Y;
        float dz = inspector.Z - target.Z;
        if (MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz)) > InspectDistance)
        {
            return null;
        }

        return map.Combat.Hooks.CanAttack(inspector, target) ? null : target;
    }

    /// <summary>Tell every <see cref="IPlayerInspectReceiver"/> that a real player inspected a playerbot (after the reply was sent).</summary>
    private static void NotifyBotInspected(WorldSession session, Player inspector, Player target)
    {
        if (!target.Session.IsPlayerbot || inspector.Session.IsPlayerbot)
        {
            return;
        }

        foreach (IPlayerInspectReceiver receiver in session.Services.GetServices<IWorldFeature>().OfType<IPlayerInspectReceiver>())
        {
            receiver.Inspected(inspector, target);
        }
    }

    private static void HandleInspect(WorldSession session, Player player, byte[] payload)
    {
        if (payload.Length < 8)
        {
            return;
        }

        var guid = new ObjectGuid(new PacketReader(payload).ReadUInt64());
        player.Selection = guid;
        if (InspectableTarget(player, guid) is { } target)
        {
            var writer = new PacketWriter(8);
            writer.WriteUInt64(target.Guid.Value);
            session.Send(WorldOpcode.SmsgInspect, writer.ToArray());
            NotifyBotInspected(session, player, target);
        }
    }

    private static void HandleInspectHonorStats(WorldSession session, Player player, byte[] payload)
    {
        if (payload.Length < 8)
        {
            return;
        }

        var guid = new ObjectGuid(new PacketReader(payload).ReadUInt64());
        if (InspectableTarget(player, guid) is { } target)
        {
            session.Send(WorldOpcode.MsgInspectHonorStats, HonorPackets.InspectHonorStats(target));
            NotifyBotInspected(session, player, target);
        }
    }
}

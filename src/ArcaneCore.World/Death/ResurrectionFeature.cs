using ArcaneCore.Game.Death.Resurrection;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Death;

/// <summary>
/// Resurrection by spell in the world daemon (docs/areas/graveyards-resurrection.md): creates the world's
/// <see cref="ResurrectionService"/> and gives it to the spell system, whose resurrect effects offer the requests; the
/// <see cref="ResurrectionHandlers"/> answer CMSG_RESURRECT_RESPONSE. The teleport service is looked up when a request is accepted,
/// not here, because the teleport feature attaches after this one.
/// </summary>
public sealed class ResurrectionFeature(IServiceProvider services) : IWorldFeature
{
    /// <summary>The service (after <see cref="Attach"/>).</summary>
    public ResurrectionService? Service { get; private set; }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        TeleportFeature? teleports = services.GetService<TeleportFeature>();
        Service = new ResurrectionService(world, () => teleports?.Teleports);
        if (services.GetService<SpellFeature>() is { } spells)
        {
            spells.System.Resurrection = Service;
        }
    }
}

/// <summary>CMSG_RESURRECT_RESPONSE (vmangos HandleResurrectResponseOpcode, MiscHandler.cpp:605-622): u64 resurrector GUID, u8 status.</summary>
public sealed class ResurrectionHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table) => table.OnWorld(WorldOpcode.CmsgResurrectResponse, HandleResponse);

    private static void HandleResponse(WorldSession session, Player player, byte[] payload)
    {
        if (ResurrectionPackets.ReadResponse(payload) is not { } response)
        {
            return;
        }

        session.Services.GetService<ResurrectionFeature>()?.Service?.Respond(player, response.Resurrector, response.Accept);
    }
}

/// <summary>CMSG_SELF_RES (vmangos HandleSelfResOpcode, SpellHandler.cpp:461-485): an empty body; casts the player's self-resurrection spell.</summary>
public sealed class SelfResurrectionHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table) => table.OnWorld(WorldOpcode.CmsgSelfRes, HandleSelfRes);

    private static void HandleSelfRes(WorldSession session, Player player, byte[] payload)
    {
        if (session.Services.GetService<SpellFeature>()?.System is { } system)
        {
            SelfResurrection.Use(system, player);
        }
    }
}

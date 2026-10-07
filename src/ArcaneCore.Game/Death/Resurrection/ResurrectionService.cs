using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Death.Resurrection;

/// <summary>
/// Resurrection by another character's spell (vmangos <c>Spell::EffectResurrect</c> / <c>EffectResurrectNew</c>, SpellEffects.cpp:209-263
/// and 5228-5251, <c>Spell::SendResurrectRequest</c>, Spell.cpp:4934-4944, <c>WorldSession::HandleResurrectResponseOpcode</c>,
/// MiscHandler.cpp:605-622, <c>Player::ResurrectUsingRequestData</c>, Player.cpp:20065-20110): the spell stores a request and sends
/// the dead player SMSG_RESURRECT_REQUEST; accepting it (CMSG_RESURRECT_RESPONSE) teleports the player to the caster when the caster
/// is a player (a dungeon instance the player is no longer bound to is replaced by the dungeon's entrance, the exploit rule of
/// Player.cpp:20069-20091), resurrects it with the offered health and mana, and the body goes (SpawnCorpseBones).
/// <para>
/// Limits: the pet branch of RESURRECT_NEW (a dead pet target, SpellEffects.cpp:216-246) is not modelled, since a pet is no
/// <see cref="Player"/> target here; the request is in memory only (<see cref="ResurrectionRequests"/>).
/// </para>
/// <para>Thread affinity: world thread.</para>
/// </summary>
/// <param name="world">The world (players by guid, the instance manager).</param>
/// <param name="teleports">The teleport service; null until the teleport feature has attached.</param>
public sealed class ResurrectionService(WorldRuntime world, Func<TeleportService?> teleports)
{
    private readonly HashSet<ObjectGuid> _awaitingTeleport = [];
    private TeleportService? _subscribedTo;

    /// <summary>The online player with this guid, wherever it is (vmangos ObjectAccessor::FindPlayer), or null.</summary>
    public Player? FindPlayer(ObjectGuid guid) => world.FindOnlinePlayer(guid);

    /// <summary>
    /// Raised on the world thread after an accepted request brought its player back (after any teleport); the world daemon saves the
    /// character then, as it does after a self-resurrection, so the restored life does not wait for the next autosave.
    /// </summary>
    public event Action<Player>? Resurrected;

    /// <summary>
    /// Offer <paramref name="target"/> a resurrection (the body of both effects after their checks): refused, returning false, when it
    /// already has a request (<c>IsRessurectRequested</c>). The request carries the caster's place, so accepting teleports a player to the
    /// resurrector. <paramref name="sickness"/> is whether the caster is a spirit healer; <paramref name="noResTimer"/> is
    /// SPELL_ATTR_EX3_NO_RES_TIMER (Rebirth): the client then resurrects at once instead of after its timer.
    /// </summary>
    public bool Request(Player target, Unit caster, string casterName, uint health, uint mana, bool sickness, bool noResTimer)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(caster);
        if (ResurrectionRequests.IsRequested(target))
        {
            return false;
        }

        ResurrectionRequests.Set(target, new ResurrectionRequest(
            caster.Guid, caster.MapId, caster.Map?.InstanceId ?? 0, caster.X, caster.Y, caster.Z, caster.Orientation, health, mana));

        // The name is only sent for a caster that is not a player (the client knows a player's).
        target.Session.Send(WorldOpcode.SmsgResurrectRequest,
            ResurrectionPackets.BuildRequest(caster.Guid, caster is Player ? string.Empty : casterName, sickness, delayed: !noResTimer));
        return true;
    }

    /// <summary>
    /// CMSG_RESURRECT_RESPONSE (vmangos HandleResurrectResponseOpcode): a living player is ignored; a refusal clears the request; an
    /// acceptance counts only for the player that cast it.
    /// </summary>
    public void Respond(Player player, ObjectGuid resurrector, bool accept)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (player.Combat.DeathState == DeathState.Alive)
        {
            return; // vmangos IsAlive is the death state: a revived player at zero health is alive too
        }

        if (!accept)
        {
            ResurrectionRequests.Clear(player); // "player denied rezz attempt"
            return;
        }

        if (!ResurrectionRequests.IsRequestedBy(player, resurrector))
        {
            return;
        }

        ResurrectUsingRequestData(player);
    }

    /// <summary>
    /// vmangos Player::ResurrectUsingRequestData: first the teleport to a player resurrector ("otherwise the player might get attacked
    /// from creatures near his corpse"), then, once any teleport has finished, the resurrection itself.
    /// </summary>
    public void ResurrectUsingRequestData(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (ResurrectionRequests.Get(player) is not { } request)
        {
            return;
        }

        TeleportService? service = teleports();
        if (request.Resurrector.IsPlayer && service is not null)
        {
            (uint mapId, float x, float y, float z, float o) = Destination(player, request);
            service.TeleportTo(player, mapId, x, y, z, o);
        }

        // "We cannot resurrect player when we triggered any kind of teleport": it happens when the teleport has finished.
        if (service is not null && service.IsBeingTeleported(player))
        {
            Subscribe(service);
            _awaitingTeleport.Add(player.Guid);
            return;
        }

        Complete(player, request);
    }

    /// <summary>
    /// The place a resurrecting player is teleported to: the resurrector's, unless it is inside a dungeon instance that the player is not
    /// bound to (Player.cpp:20069-20091, "prevents death exploit to reset dungeon and teleport directly to end boss"): then the
    /// dungeon's entrance trigger destination, else its go-back trigger's, else where the player stands.
    /// </summary>
    internal (uint MapId, float X, float Y, float Z, float Orientation) Destination(Player player, ResurrectionRequest request)
    {
        (uint MapId, float X, float Y, float Z, float Orientation) at = (request.MapId, request.X, request.Y, request.Z, request.Orientation);
        if (request.InstanceId == 0 || request.MapId == player.MapId
            || WorldMaps.Of(world).Registry.Find(request.MapId) is not { IsDungeon: true }
            || world.MapResolver is not InstanceManager instances)
        {
            return at;
        }

        InstanceSave? bound = instances.GetBoundSaveForSelfOrGroup(player.Guid, request.MapId);
        if (bound is not null && bound.InstanceId == request.InstanceId)
        {
            return at;
        }

        AreaTriggerTeleport? trigger = instances.GetMapEntranceTrigger(request.MapId) ?? instances.GetGoBackTrigger(request.MapId);
        return trigger is null
            ? (player.MapId, player.X, player.Y, player.Z, player.Orientation)
            : (trigger.TargetMap, trigger.TargetX, trigger.TargetY, trigger.TargetZ, trigger.TargetOrientation);
    }

    private void Subscribe(TeleportService service)
    {
        if (ReferenceEquals(_subscribedTo, service))
        {
            return;
        }

        if (_subscribedTo is not null)
        {
            _subscribedTo.TeleportCompleted -= OnTeleportCompleted;
        }
        else
        {
            world.PlayerLoggingOut += player => _awaitingTeleport.Remove(player.Guid); // a player that leaves is not resurrected later
        }

        service.TeleportCompleted += OnTeleportCompleted;
        _subscribedTo = service;
    }

    private void OnTeleportCompleted(Player player)
    {
        if (_awaitingTeleport.Remove(player.Guid) && ResurrectionRequests.Get(player) is { } request)
        {
            Complete(player, request);
        }
    }

    /// <summary>The resurrection itself, after any teleport: alive with the offered health and mana, no rage, full energy, the corpse gone.</summary>
    private void Complete(Player player, ResurrectionRequest request)
    {
        if (player.Map is { } map)
        {
            map.Combat.CompleteResurrection(player, request.Health, request.Mana);
            if (player.IsAlive)
            {
                Resurrected?.Invoke(player);
            }
        }
    }
}

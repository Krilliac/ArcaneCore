using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.GameObjects;

/// <summary>
/// The spell effects that create or end game object behaviours: SPELL_EFFECT_TRANS_DOOR for every object that is not a fishing bobber (mage
/// portals, rituals, Lightwell; vmangos Spell::EffectTransmitted, SpellEffects.cpp:5648-5790), SPELL_EFFECT_SUMMON_PLAYER (the ritual's summon
/// request, SpellEffects.cpp:4783-4800, Player.cpp:19636-19674), and the end of a channel for the rituals (Spell::cancel / Spell::update).
/// Install once per <see cref="SpellSystem"/>, before the fishing spells (they hand every object that is not a bobber to the handler they find).
/// </summary>
public sealed class GameObjectSpellEffects(Func<Map, GameObjectMapSystem?> systems) : ISpellCastObserver
{
    /// <summary>SMSG_SUMMON_REQUEST: u64 summoner, u32 zone id, u32 auto-decline delay in ms (vmangos Server/Packets/Misc.cpp:957-970).</summary>
    public static byte[] SummonRequest(ObjectGuid summoner, uint zoneId, uint autoDeclineMs)
    {
        var writer = new PacketWriter(16);
        writer.WriteUInt64(summoner.Value);
        writer.WriteUInt32(zoneId);
        writer.WriteUInt32(autoDeclineMs);
        return writer.ToArray();
    }

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        SpellEffectHandler? previous = system.GetEffectHandler(SpellEffectName.TransDoor);
        system.RegisterEffect(SpellEffectName.TransDoor, context =>
        {
            if (!Transmit(context))
            {
                previous?.Invoke(context);
            }
        });
        system.RegisterEffect(SpellEffectName.SummonPlayer, EffectSummonPlayer);
        system.RegisterObserver(this);
    }

    /// <summary>
    /// Spell::EffectTransmitted for an object that is not a fishing node: the object appears at the cast's destination, else at the effect radius
    /// in front of the caster for a spell without travel time, else at a random distance between the range bounds within an arc in front of the
    /// caster; it lives for the spell's duration, is owned by the caster (and remembers the caster's group), carries the caster's level and the
    /// creating spell, and its linked trap is created with it. A ritual created by a player takes the caster's selection as its summon target and
    /// the caster as its first participant. False when the effect is not this handler's (no object system, unknown template, a fishing node).
    /// Limit: the line-of-sight correction of the landing point (GetLosHitPosition) is not applied.
    /// </summary>
    private bool Transmit(SpellEffectContext context)
    {
        Unit caster = context.Caster;
        uint entry = (uint)context.Effect.MiscValue;
        if (caster.Map is not { } map || systems(map) is not { } objects || objects.FindTemplate(entry) is not { } template
            || (GameObjectType)template.Type == GameObjectType.FishingNode)
        {
            return false;
        }

        (float x, float y, float z) = Landing(context);
        int duration = context.Spell.GetDuration();
        GameObject? go = objects.Summon(entry, x, y, z, caster.Orientation, duration > 0 ? (uint)(duration / 1000) : 0);
        if (go is null)
        {
            return false;
        }

        go.SetOwner(caster.Guid);
        if (caster is Player owner && objects.GroupIdOf?.Invoke(owner) is { } groupId)
        {
            go.OwnerGroupId = groupId;
        }

        go.SetUInt32(UpdateFields.GameobjectLevel, caster.Level);
        go.SpellId = context.Spell.Id;
        if ((GameObjectType)template.Type == GameObjectType.SummoningRitual && caster is Player summoner)
        {
            objects.BeginRitual(go, summoner, summoner.Selection);
        }

        objects.SummonLinkedTrapIfAny(go);
        return true;
    }

    private static (float X, float Y, float Z) Landing(SpellEffectContext context)
    {
        Unit caster = context.Caster;
        SpellCastTargets targets = context.Cast.Targets;
        if (targets.HasDest)
        {
            return targets.Dest;
        }

        float distance;
        float angle = caster.Orientation;
        if (context.Effect.Radius > 0 && context.Spell.Speed == 0)
        {
            distance = context.Effect.Radius;
        }
        else
        {
            float min = context.Spell.Range.Min;
            float max = context.Spell.Range.Max;
            distance = ((float)context.System.Random.NextDouble() * (max - min)) + min;
            float maxAngle = max > 0 ? (max - min) / (max + caster.BoundingRadius) : 0;
            angle += maxAngle * ((float)context.System.Random.NextDouble() - 0.5f);
        }

        return (caster.X + (distance * MathF.Cos(angle)), caster.Y + (distance * MathF.Sin(angle)), caster.Z);
    }

    /// <summary>
    /// SPELL_EFFECT_SUMMON_PLAYER (Spell::EffectSummonPlayer, SpellEffects.cpp:4783-4800): a player target is offered a summon to a point beside
    /// the ritual (the cast's ritual object target) or beside the caster.
    /// </summary>
    private void EffectSummonPlayer(SpellEffectContext context)
    {
        if (context.Target is not Player target)
        {
            return;
        }

        GameObject? ritual = context.Cast.Targets.GameObject is { IsEmpty: false } guid && context.Caster.Map is { } map
            ? systems(map)?.Find(guid) : null;
        Offer(target, context.Caster, ritual is { Type: GameObjectType.SummoningRitual } ? ritual : null, context.System.NowMs);
    }

    /// <summary>
    /// Player::SendSummonRequest (Player.cpp:19636-19645): remember where the summon leads for two minutes and send SMSG_SUMMON_REQUEST. The
    /// landing point is one bounding radius beside the landing object (the ritual when there is one, else the summoner).
    /// </summary>
    public static void Offer(Player target, Unit summoner, GameObject? ritual, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(summoner);
        WorldObject landing = ritual is not null ? ritual : summoner;
        float distance = landing.BoundingRadius + target.BoundingRadius;
        float x = landing.X + (distance * MathF.Cos(landing.Orientation));
        float y = landing.Y + (distance * MathF.Sin(landing.Orientation));
        uint mapId = summoner.Map?.MapId ?? landing.Map?.MapId ?? 0;
        uint zone = summoner is Player player ? player.ZoneId : 0;
        target.PendingSummon = new PendingSummon(summoner.Guid, mapId, x, y, landing.Z, nowMs + Player.SummonAcceptMs);
        target.Session.Send(WorldOpcode.SmsgSummonRequest, SummonRequest(summoner.Guid, zone, Player.SummonAcceptMs));
    }

    /// <summary>
    /// CMSG_SUMMON_RESPONSE (WorldSession::HandleSummonResponseOpcode → Player::SummonIfPossible(true), MovementHandler.cpp:981-987,
    /// Player.cpp:19647-19674): a living player out of combat with an offer that has not expired is teleported to the summon point. The offer
    /// is used up either way once it was accepted in time. Returns whether the teleport was started.
    /// </summary>
    public static bool Accept(Player player, ObjectGuid summoner, long nowMs, ITeleportSink teleports)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(teleports);
        if (!player.IsAlive || player.Combat.IsInCombat || player.PendingSummon is not { } offer)
        {
            return false;
        }

        // vmangos ignores the guid the client sends back; the offer is the player's own state.
        _ = summoner;
        if (offer.ExpiresAtMs < nowMs)
        {
            return false;
        }

        player.PendingSummon = null;
        return teleports.Teleport(player, offer.MapId, offer.X, offer.Y, offer.Z, player.Orientation);
    }

    /// <summary>A channel ended (cancelled or finished): the rituals of the caster's map hear about it (<see cref="GameObjectMapSystem.OnChannelEnded"/>).</summary>
    public void OnFinished(SpellCast cast, bool completed)
    {
        if (cast.Spell.IsChanneled && cast.Caster is Player player && player.Map is { } map)
        {
            systems(map)?.OnChannelEnded(player, cast.Spell.Id);
        }
    }
}

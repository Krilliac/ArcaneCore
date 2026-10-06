using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Chooses and submits one ordinary hostile spell for a managed player. All decisions
/// and packet dispatch happen on the world thread; the normal CMSG_CAST_SPELL handler
/// remains the authoritative validator and executor.
/// </summary>
internal sealed class PlayerbotCombatSpells(WorldSession session)
{
    private const uint FailureBackoffMs = 750;
    private uint _failureBackoffMs;
    private uint _lastRefusedSpell;

    internal bool Update(Player player, Unit target, uint elapsedMs)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(target);

        if (elapsedMs > 0 && _failureBackoffMs > 0)
        {
            _failureBackoffMs = elapsedMs >= _failureBackoffMs ? 0 : _failureBackoffMs - elapsedMs;
            if (_failureBackoffMs != 0)
            {
                return false;
            }
        }

        if (!player.IsInWorld || !player.IsAlive || !target.IsInWorld || !target.IsAlive
            || !ReferenceEquals(player.Map, target.Map)
            || player.Map is null || !player.Map.Combat.Hooks.CanAttack(player, target))
        {
            return false;
        }

        SpellFeature? feature = session.Services.GetService<SpellFeature>();
        if (feature is null)
        {
            return false;
        }

        UnitSpellState? state = feature.System.GetState(player.Guid);
        if (state?.CurrentCast is { State: SpellCastState.Preparing or SpellCastState.Casting }
            )
        {
            return true;
        }

        // A queued next-swing spell is compatible with the ordinary melee action; let
        // the brain continue its normal swing/chase decision rather than suppressing it.
        if (state?.MeleeCast is not null)
        {
            return false;
        }

        SpellInfo? spell = Select(feature, player, target);
        if (spell is null)
        {
            return false;
        }

        if (player.Movement.HasFlag(MovementFlags.MaskMoving))
        {
            PlayerbotMovementControl.Stop(session, player);
            return true;
        }

        var writer = new PacketWriter(24);
        writer.WriteUInt32(spell.Id);
        SpellCastTargets.ForUnit(target.Guid).Write(writer);
        // A previous completed cast can leave GO packets in the bounded capture queue.
        // Consume prior notifications before submitting this request so they cannot
        // make an ordinary cooldown/resource refusal appear accepted.
        session.DrainManagedPackets(WorldOpcode.SmsgSpellStart, WorldOpcode.SmsgSpellGo,
            WorldOpcode.SmsgSpellFailure, WorldOpcode.SmsgSpellFailedOther, WorldOpcode.SmsgCastResult);

        // TryManagedAction is only transport/handler admission. Success is established
        // from the ordinary spell packets or authoritative cast state below.
        if (!session.TryManagedAction(WorldOpcode.CmsgCastSpell, writer.ToArray()))
        {
            return false;
        }

        IReadOnlyList<ManagedSessionPacket> packets = session.DrainManagedPackets(
            WorldOpcode.SmsgSpellStart,
            WorldOpcode.SmsgSpellGo,
            WorldOpcode.SmsgSpellFailure,
            WorldOpcode.SmsgSpellFailedOther,
            WorldOpcode.SmsgCastResult);
        bool accepted = packets.Any(packet =>
            (packet.Opcode is WorldOpcode.SmsgSpellStart or WorldOpcode.SmsgSpellGo)
            && SpellIdOf(packet.Payload) == spell.Id)
            || feature.System.GetState(player.Guid)?.CurrentCast is
                { State: SpellCastState.Preparing or SpellCastState.Casting } current && current.Spell.Id == spell.Id
            || feature.System.GetState(player.Guid)?.MeleeCast?.Spell.Id == spell.Id;

        if (!accepted)
        {
            _failureBackoffMs = FailureBackoffMs;
            _lastRefusedSpell = spell.Id;
        }
        else _lastRefusedSpell = 0;

        // A refused cast permits ordinary chase/melee while spell retries back off.
        return accepted && feature.System.GetState(player.Guid)?.MeleeCast is null;
    }

    private static uint SpellIdOf(byte[] payload)
    {
        var reader = new PacketReader(payload);
        _ = reader.ReadPackedGuid();
        _ = reader.ReadPackedGuid();
        return reader.ReadUInt32();
    }

    private SpellInfo? Select(SpellFeature feature, Player player, Unit target)
    {
        IReadOnlyList<uint> known = feature.Spellbook.GetSpells(player);
        // A normal refusal rotates the next attempt, so an expensive or conditionally
        // unavailable lowest-ID spell cannot starve the rest of the learned book.
        foreach (uint spellId in known.Where(id => id > _lastRefusedSpell)
            .Concat(known.Where(id => id <= _lastRefusedSpell)))
        {
            SpellInfo? spell = feature.System.Store.Get(spellId);
            if (spell is null || spell.IsPassive || spell.IsPositive || spell.CanTargetDead)
            {
                continue;
            }

            // This first vertical slice deliberately supports only an explicit unit
            // target. AOE/cone/near-caster spells need a separate target contract.
            if (!spell.Effects.Any(static effect => effect.TargetA == SpellImplicitTarget.UnitEnemy))
            {
                continue;
            }

            float dx = player.X - target.X;
            float dy = player.Y - target.Y;
            float dz = player.Z - target.Z;
            float distance = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            if (spell.RangeIndex == SpellConstants.RangeIndexCombat && distance > 4f) continue;
            if (spell.Range.Max > 0 && distance > spell.Range.Max + player.BoundingRadius + target.BoundingRadius)
            {
                continue;
            }

            if (spell.Range.Min > 0 && distance < spell.Range.Min)
            {
                continue;
            }

            if (feature.System.GetActiveCooldowns(player).Any(cooldown => cooldown.SpellId == spell.Id
                    || (cooldown.Category != 0 && cooldown.Category == spell.Category)))
            {
                continue;
            }

            // Power cost includes aura modifiers and dynamic costs. Only the ordinary
            // handler decides affordability; refusal above advances the candidate cursor.
            return spell;
        }

        return null;
    }
}

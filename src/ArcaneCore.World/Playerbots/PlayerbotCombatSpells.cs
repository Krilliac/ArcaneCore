using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Pets;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Chooses and submits one ordinary spell, item or pet command for a managed player. A bot of a playable class follows its
/// class rotation (<see cref="PlayerbotClassRotation"/>, vmangos PartyBotAI); a spellbook with none of the class's abilities
/// falls back to the first usable hostile spell. All decisions and packet dispatch happen on the world thread; the ordinary
/// CMSG_CAST_SPELL / CMSG_USE_ITEM / CMSG_PET_ACTION handlers remain the authoritative validators and executors.
/// </summary>
internal sealed class PlayerbotCombatSpells(WorldSession session)
{
    private const uint FailureBackoffMs = 750;

    /// <summary>How long a rotation spell the server refused is left out before it is tried again.</summary>
    internal const uint RefusalBackoffMs = 4000;

    private uint _failureBackoffMs;
    private uint _lastRefusedSpell;
    private readonly Dictionary<uint, uint> _refusedUntil = [];
    private uint _itemBlockedUntil;
    private PlayerbotAbilities? _abilities;
    private PlayerbotRole _role;

    /// <summary>
    /// The creature the bot is closing in on before a fight (set by its controller): the out-of-combat upkeep prepares for it
    /// (rogue stealth, a pet sent ahead). Not part of the fight itself; null between pulls.
    /// </summary>
    internal Unit? PullTarget { get; set; }

    /// <summary>
    /// The role the bot's group gave it (<see cref="Groups.PlayerbotGroupCoordinator"/>; a warrior without Shield Slam still tanks for
    /// a group that has no other tank). Null: the role its talents give (vmangos AutoAssignRole).
    /// </summary>
    internal PlayerbotRole? GroupRole { get; set; }

    /// <summary>Retire retry state when the player leaves a combat lifetime (death/reclaim).</summary>
    internal void Reset()
    {
        _failureBackoffMs = 0;
        _lastRefusedSpell = 0;
        _refusedUntil.Clear();
        _itemBlockedUntil = 0;
        _abilities = null;
        PullTarget = null;
    }

    /// <summary>
    /// One fight decision against <paramref name="target"/> (a pull when the bot is not in combat yet). True: an action was
    /// accepted or a cast is in progress (the caller keeps still); false: the caller chases or swings as usual.
    /// </summary>
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
            PlayerbotMovementControl.Stop(session, player);
            return true;
        }

        // A queued next-swing spell is compatible with the ordinary melee action; let
        // the brain continue its normal swing/chase decision rather than suppressing it.
        if (state?.MeleeCast is not null)
        {
            return false;
        }

        if (Rotation(player, feature) is { } rotation)
        {
            RotationState view = View(player, feature, target);
            return rotation.InCombat(view) is { } action && Execute(player, feature, action);
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

        bool accepted = Accepted(feature, player, spell);
        if (!accepted)
        {
            _failureBackoffMs = FailureBackoffMs;
            _lastRefusedSpell = spell.Id;
        }
        else _lastRefusedSpell = 0;

        // A refused cast permits ordinary chase/melee while spell retries back off.
        return accepted && feature.System.GetState(player.Guid)?.MeleeCast is null;
    }

    /// <summary>
    /// Upkeep between fights (vmangos UpdateOutOfCombatAI_&lt;Class&gt;): forms, auras and buffs on the bot and its group, the
    /// pet summoned, revived and called back, heals for the bot and its group, and stealth before a pull. True: an action was
    /// accepted or a cast is in progress (the caller keeps still).
    /// </summary>
    internal bool UpdateOutOfCombat(Player player, uint elapsedMs)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (!player.IsInWorld || !player.IsAlive || player.Map is null
            || session.Services.GetService<SpellFeature>() is not { } feature)
            return false;

        if (feature.System.GetState(player.Guid)?.CurrentCast is { State: SpellCastState.Preparing or SpellCastState.Casting })
        {
            PlayerbotMovementControl.Stop(session, player);
            return true;
        }

        if (Rotation(player, feature) is not { } rotation) return false;
        Unit? pull = PullTarget is { IsInWorld: true, IsAlive: true } target && ReferenceEquals(target.Map, player.Map)
            && player.Map.Combat.Hooks.CanAttack(player, target) ? target : null;
        return rotation.OutOfCombat(View(player, feature, pull)) is { } action && Execute(player, feature, action);
    }

    /// <summary>Where the bot fights from: melee distance, or a caster's or hunter's range (yards).</summary>
    internal float PreferredRange(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (session.Services.GetService<SpellFeature>() is not { } feature || Rotation(player, feature) is not { } rotation)
            return PlayerbotClassRotation.MeleeRange;
        return rotation.PreferredRange(View(player, feature, null));
    }

    /// <summary>The bot's role (vmangos AutoAssignRole) from the talent spells it knows.</summary>
    internal PlayerbotRole RoleOf(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (session.Services.GetService<SpellFeature>() is { } feature) Refresh(player, feature);
        else _role = PlayerbotRoles.Assign(player.Class, _ => false);
        return GroupRole ?? _role;
    }

    /// <summary>The resolved class abilities (rebuilt after a learned, superseded or removed spell).</summary>
    internal PlayerbotAbilities Abilities(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return session.Services.GetService<SpellFeature>() is { } feature ? Refresh(player, feature) : PlayerbotAbilities.Empty;
    }

    private PlayerbotAbilities? _escapes;
    private int _escapeBookCount = -1;

    /// <summary>
    /// The class escapes the bot knows (<see cref="PlayerbotEscapes.All"/>), resolved from its book at the highest rank; rebuilt when
    /// the book changes size.
    /// </summary>
    internal PlayerbotAbilities Escapes(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (session.Services.GetService<SpellFeature>() is not { } feature) return PlayerbotAbilities.Empty;
        IReadOnlyList<uint> book = feature.Spellbook.GetSpells(player);
        if (_escapes is not null && _escapeBookCount == book.Count) return _escapes;
        _escapeBookCount = book.Count;
        return _escapes = PlayerbotAbilities.Resolve([.. book.Select(feature.System.Store.Get).OfType<SpellInfo>()], PlayerbotEscapes.All);
    }

    /// <summary>Whether <paramref name="spell"/> can be cast at <paramref name="target"/> now (the rotation's castability check).</summary>
    internal bool CanCastNow(Player player, SpellInfo spell, Unit target)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (session.Services.GetService<SpellFeature>() is not { } feature || IsRefused(spell)) return false;
        Unit? victim = ReferenceEquals(target, player) ? null : target;
        RotationState view = View(player, feature, victim);
        RotationUnit? unit = victim is null ? view.Self : view.Victim;
        return unit is not null && view.CanCast(spell, unit);
    }

    /// <summary>Submit <paramref name="spell"/> at <paramref name="target"/> through CMSG_CAST_SPELL; true when the server took it.</summary>
    internal bool CastAt(Player player, SpellInfo spell, Unit target)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(spell);
        ArgumentNullException.ThrowIfNull(target);
        return session.Services.GetService<SpellFeature>() is { } feature && Cast(player, feature, spell, target.Guid);
    }

    /// <summary>
    /// Submit <paramref name="spell"/> at a player's corpse the way a 1.12 client does once that player released (the ghost cannot be
    /// targeted): CMSG_CAST_SPELL with TARGET_FLAG_CORPSE (0x8000, the resurrection rows' own <c>Targets</c>) and the corpse guid. The
    /// server resolves the corpse to its owner (vmangos Spell::SetTargetMap, Spell.cpp:3106-3117). True when the server took it.
    /// </summary>
    internal bool CastAtCorpse(Player player, SpellInfo spell, Corpse corpse)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(spell);
        ArgumentNullException.ThrowIfNull(corpse);
        return session.Services.GetService<SpellFeature>() is { } feature
            && Cast(player, feature, spell, new SpellCastTargets { Mask = SpellCastTargetFlags.CorpseAlly, Corpse = corpse.Guid });
    }

    private PlayerbotClassRotation? Rotation(Player player, SpellFeature feature)
        => PlayerbotRotations.For(player.Class) is { } rotation && Refresh(player, feature).Count > 0 ? rotation : null;

    private RotationState View(Player player, SpellFeature feature, Unit? victim)
        => PlayerbotCombatView.Build(session, feature, player, victim, Refresh(player, feature), GroupRole ?? _role, IsRefused);

    /// <summary>
    /// vmangos PartyBotAI::OnPacketReceived (PartyBotAI.cpp:544-553): a learned, superseded or removed spell marks the spell
    /// data stale; the next decision rebuilds it from the book. Only those opcodes are drained from the bounded capture queue.
    /// </summary>
    private PlayerbotAbilities Refresh(Player player, SpellFeature feature)
    {
        bool changed = session.DrainManagedPackets(WorldOpcode.SmsgLearnedSpell, WorldOpcode.SmsgSupercededSpell,
            WorldOpcode.SmsgRemovedSpell).Count > 0;
        if (_abilities is not null && !changed) return _abilities;

        PlayerbotClassRotation? rotation = PlayerbotRotations.For(player.Class);
        SpellInfo[] known = [.. feature.Spellbook.GetSpells(player).Select(feature.System.Store.Get).OfType<SpellInfo>()];
        _abilities = PlayerbotAbilities.Resolve(known, rotation?.Abilities ?? []);
        PlayerbotAbilities abilities = _abilities;
        _role = PlayerbotRoles.Assign(player.Class, id => abilities.HasSpellOrRank(id, feature.System.Store.Get));
        return _abilities;
    }

    private bool IsRefused(SpellInfo spell)
        => _refusedUntil.TryGetValue(spell.Id, out uint until) && unchecked((int)(until - session.World.NowMs)) > 0;

    private bool Execute(Player player, SpellFeature feature, RotationAction action)
    {
        switch (action.Kind)
        {
            case RotationActionKind.Cast when action.Spell is { } spell:
                return Cast(player, feature, spell, action.Target);

            case RotationActionKind.UseItem when action.Item is { } item:
                if (unchecked((int)(_itemBlockedUntil - session.World.NowMs)) > 0) return false;
                _itemBlockedUntil = unchecked(session.World.NowMs + 2000);
                return session.TryManagedAction(WorldOpcode.CmsgUseItem, PlayerbotNavigation.UseItemPayload(item.Bag, item.Slot));

            case RotationActionKind.PetAttack:
                return PetAction(player, PetCommandAttack, action.Target);

            case RotationActionKind.PetFollow:
                return PetAction(player, PetCommandFollow, ObjectGuid.Empty);

            default:
                return false;
        }
    }

    /// <summary>CMSG_PET_ACTION data: ACT_COMMAND (7) in the top byte, the command below (vmangos UNIT_ACTION_BUTTON_*).</summary>
    private const uint PetCommandFollow = (7u << 24) | 1u;
    private const uint PetCommandAttack = (7u << 24) | 2u;

    private bool PetAction(Player player, uint data, ObjectGuid target)
    {
        if (player.PetGuid.IsEmpty) return false;
        var writer = new PacketWriter(20);
        writer.WriteUInt64(player.PetGuid.Value);
        writer.WriteUInt32(data);
        writer.WriteUInt64(target.Value);
        return session.TryManagedAction(WorldOpcode.CmsgPetAction, writer.ToArray());
    }

    private bool Cast(Player player, SpellFeature feature, SpellInfo spell, ObjectGuid target)
        => Cast(player, feature, spell, target == player.Guid
            ? spell.IsPositive && IsExplicitUnit(spell) ? SpellCastTargets.ForUnit(target) : SpellCastTargets.ForSelf()
            : SpellCastTargets.ForUnit(target));

    private bool Cast(Player player, SpellFeature feature, SpellInfo spell, SpellCastTargets targets)
    {
        // A spell with a cast time needs the bot standing (vmangos DoCastSpell stops the mover); instants go while moving.
        if (spell.GetCastTime(player.Level) > 0 && player.Movement.HasFlag(MovementFlags.MaskMoving)
            && (!PlayerbotMovementControl.Stop(session, player) || player.Movement.HasFlag(MovementFlags.MaskMoving)))
            return true;

        var writer = new PacketWriter(24);
        writer.WriteUInt32(spell.Id);
        targets.Write(writer);
        session.DrainManagedPackets(WorldOpcode.SmsgSpellStart, WorldOpcode.SmsgSpellGo,
            WorldOpcode.SmsgSpellFailure, WorldOpcode.SmsgSpellFailedOther, WorldOpcode.SmsgCastResult);
        if (!session.TryManagedAction(WorldOpcode.CmsgCastSpell, writer.ToArray())) return false;

        if (Accepted(feature, player, spell))
        {
            _refusedUntil.Remove(spell.Id);
            return feature.System.GetState(player.Guid)?.MeleeCast is null;
        }

        // A refusal leaves that spell out for a while; the rest of the rotation goes on at the next decision.
        if (_refusedUntil.Count >= 64) _refusedUntil.Clear();
        _refusedUntil[spell.Id] = unchecked(session.World.NowMs + RefusalBackoffMs);
        return false;
    }

    private static bool IsExplicitUnit(SpellInfo spell) => spell.Effects[0].TargetA is SpellImplicitTarget.UnitFriend
        or SpellImplicitTarget.Unit or SpellImplicitTarget.UnitFriendAndParty or SpellImplicitTarget.UnitParty
        or SpellImplicitTarget.UnitCasterPet;

    /// <summary>The server took the request: its SMSG_SPELL_START / GO, or the cast, next-swing or auto-repeat slot now holds it.</summary>
    private bool Accepted(SpellFeature feature, Player player, SpellInfo spell)
    {
        IReadOnlyList<ManagedSessionPacket> packets = session.DrainManagedPackets(
            WorldOpcode.SmsgSpellStart,
            WorldOpcode.SmsgSpellGo,
            WorldOpcode.SmsgSpellFailure,
            WorldOpcode.SmsgSpellFailedOther,
            WorldOpcode.SmsgCastResult);
        UnitSpellState? state = feature.System.GetState(player.Guid);
        return packets.Any(packet =>
            (packet.Opcode is WorldOpcode.SmsgSpellStart or WorldOpcode.SmsgSpellGo)
            && SpellIdOf(packet.Payload) == spell.Id)
            || state?.CurrentCast is { State: SpellCastState.Preparing or SpellCastState.Casting } current && current.Spell.Id == spell.Id
            || state?.MeleeCast?.Spell.Id == spell.Id
            || state?.AutoRepeatCast?.Spell.Id == spell.Id;
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

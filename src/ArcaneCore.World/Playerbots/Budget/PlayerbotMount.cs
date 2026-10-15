using System.Runtime.CompilerServices;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Druid;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using System.Numerics;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// A bot riding on a long walk, and getting off for what a mount cannot do. One state object per bot session (<see cref="For"/>), owned by
/// that session's world thread; <see cref="BeforeStep"/> is called by <c>PlayerbotNavigation.TryAdvance</c> before every walking step.
/// <para>
/// Mounting goes through the real paths: CMSG_CAST_SPELL for a mount spell the bot knows, CMSG_USE_ITEM for a mount item it carries;
/// the server's own checks (<see cref="MountCastCheck"/>: indoors, in water, in a form, where mounts are not allowed, the item's riding
/// skill and level) decide. A refusal, or a cast that ends without a mount, backs the bot off (30 seconds, doubling to 10 minutes per
/// failure in a row), so a bot indoors does not retry every think.
/// </para>
/// <para>
/// Dismounting: the server already dismounts a player who casts (<see cref="DismountOnCastCheck"/>), but refuses an attack from a mounted
/// player and does not dismount for an NPC or a corpse, so <see cref="DismountForAction"/> (called by <c>TryManagedAction</c>) takes the
/// mount off before any managed packet that talks to an NPC, loots or attacks, the way a client's player clicks the mount aura off.
/// </para>
/// </summary>
internal sealed class PlayerbotMount
{
    internal const uint BackoffBaseMs = 30_000;
    internal const uint BackoffMaxMs = 600_000;
    internal const uint ScanIntervalMs = 10_000;
    internal const uint IndoorsRetryMs = 10_000;
    internal const uint TransientRetryMs = 1_500;

    private static readonly ConditionalWeakTable<WorldSession, PlayerbotMount> States = [];

    private uint _backoffUntil;
    private int _failures;
    private bool _casting;
    private uint _scanAt;
    private bool _scanned;
    private PlayerbotMountChoice _choice;

    /// <summary>The mount state of one bot session.</summary>
    internal static PlayerbotMount For(WorldSession session) => States.GetValue(session, _ => new PlayerbotMount());

    /// <summary>Consecutive failed mount attempts (tests, inspection).</summary>
    internal int Failures => _failures;

    /// <summary>The world-clock time before which the bot does not try to mount (0: none).</summary>
    internal uint BackoffUntil => _backoffUntil;

    /// <summary>
    /// Called before a walking step along <paramref name="route"/>. True when the bot took the step over (it stopped to cast, or waits
    /// for the cast to end): the caller must not move this step. False: walk on (mounted, or not mounting).
    /// </summary>
    internal static bool BeforeStep(WorldSession session, Player player, PlayerbotRoute route, PlayerbotOptions options)
        => options.Mounts.Enabled && For(session).Step(session, player, route, options);

    private bool Step(WorldSession session, Player player, PlayerbotRoute route, PlayerbotOptions options)
    {
        uint now = session.World.NowMs;
        // Nothing to ride on, scanned a moment ago: no lookup, no route walk, nothing, until the next scan is due.
        if (!_casting && _scanned && _choice.Kind == PlayerbotMountKind.None && unchecked((int)(now - _scanAt)) < 0) return false;
        SpellFeature? spells = session.Services.GetService<SpellFeature>();
        if (spells is null) return false;
        if (_casting)
        {
            if (MountService.IsMounted(player)) { _casting = false; _failures = 0; return false; }
            if (IsCasting(spells, player)) return true;
            _casting = false;
            Fail(session, player, now, "mount cast ended without applying mount aura");
            return false;
        }

        if (MountService.IsMounted(player) || (_backoffUntil != 0 && unchecked((int)(now - _backoffUntil)) < 0)) return false;
        if (route.Distance < options.Mounts.MountMinTravelYards || !CanMountHere(player)) return false;
        if (RemainingYards(player, route) < options.Mounts.MountMinTravelYards) return false;
        if (!_scanned || unchecked((int)(now - _scanAt)) >= 0)
        {
            _scanned = true;
            _scanAt = unchecked(now + ScanIntervalMs);
            _choice = session.Services.GetService<ArcaneCore.World.Npc.QuestNpcFeature>()?.Services.Deps.Spells is { } learner
                ? PlayerbotMountRules.Find(player, spells.System.Store, learner) : default;
        }

        if (_choice.Kind == PlayerbotMountKind.None) return false;
        if (player.Map is not { } map || !map.Collision.IsOutdoors(player.X, player.Y, player.Z))
        {
            _backoffUntil = Math.Max(1u, unchecked(now + IndoorsRetryMs));
            return false;
        }

        if (player.Movement.HasFlag(MovementFlags.MaskMoving) && !PlayerbotMovementControl.Stop(session, player)) return true;
        if (player.Movement.HasFlag(MovementFlags.MaskMoving)) return true;
        if (IsCasting(spells, player))
        {
            // Another cast is in progress (food, a buff): the mount-up waits for it, and that is not a failure.
            _backoffUntil = Math.Max(1u, unchecked(now + TransientRetryMs));
            return false;
        }

        session.DrainManagedPackets(WorldOpcode.SmsgSpellStart, WorldOpcode.SmsgSpellGo, WorldOpcode.SmsgSpellFailure,
            WorldOpcode.SmsgSpellFailedOther, WorldOpcode.SmsgCastResult);
        PlayerbotMountChoice attempted = _choice;
        bool sent;
        if (attempted.Kind == PlayerbotMountKind.Spell)
        {
            var writer = new PacketWriter(24);
            writer.WriteUInt32(attempted.SpellId);
            SpellCastTargets.ForSelf().Write(writer);
            sent = session.TryManagedAction(WorldOpcode.CmsgCastSpell, writer.ToArray());
        }
        else
        {
            sent = session.TryManagedAction(WorldOpcode.CmsgUseItem, PlayerbotNavigation.UseItemPayload(attempted.Bag, attempted.Slot));
            _scanned = false; // the bags may have changed
        }

        if (!sent) return true; // no budget left this think: ask again at the next
        if (MountService.IsMounted(player)) { _failures = 0; return false; }
        if (IsCasting(spells, player)) { _casting = true; return true; }
        if (RefusedForNow(session))
        {
            // The global cooldown, a cast still ending, a step not yet stopped: ask again in a moment; the bot has not failed to ride.
            _backoffUntil = Math.Max(1u, unchecked(now + TransientRetryMs));
            return false;
        }

        Fail(session, player, now, attempted.Kind == PlayerbotMountKind.Spell
            ? $"mount spell {attempted.SpellId} cast was refused"
            : $"mount item {attempted.ItemEntry} (spell {attempted.SpellId}) use was refused");
        return false;
    }

    /// <summary>The server's cast result for the attempt just sent names a reason that passes by itself (see <see cref="IsTransientRefusal"/>).</summary>
    private static bool RefusedForNow(WorldSession session)
    {
        foreach (ManagedSessionPacket packet in session.DrainManagedPackets(WorldOpcode.SmsgCastResult))
        {
            if (packet.Payload.Length >= 6 && packet.Payload[4] == (byte)SpellCastResultStatus.Failure
                && IsTransientRefusal((SpellCastResult)packet.Payload[5])) return true;
        }

        return false;
    }

    /// <summary>A cast refusal that is only about timing: the global cooldown or the spell not ready, a cast in progress, the bot still moving.</summary>
    internal static bool IsTransientRefusal(SpellCastResult result)
        => result is SpellCastResult.NotReady or SpellCastResult.SpellInProgress or SpellCastResult.ItemNotReady or SpellCastResult.Moving;

    private void Fail(WorldSession session, Player player, uint now, string detail)
    {
        _failures++;
        uint wait = BackoffBaseMs;
        for (int i = 1; i < _failures && wait < BackoffMaxMs; i++) wait *= 2;
        _backoffUntil = Math.Max(1u, unchecked(now + Math.Min(wait, BackoffMaxMs)));
        _scanned = false;
        if (!session.IsInChaosProbe)
            PlayerbotAnomalies.Report(player, "mount", "summon-failed", $"{detail} (failures={_failures})");
    }

    /// <summary>True while the bot's mount-up cast is running: walking stuck clocks do not run then (the bot stands still to cast).</summary>
    internal static bool IsBusy(WorldSession session) => States.TryGetValue(session, out PlayerbotMount? state) && state._casting;

    /// <summary>Out of combat, alive, in no instance or battleground, not swimming, not in a form that cannot ride (a warrior's stance can), not on a taxi.</summary>
    internal static bool CanMountHere(Player player)
        => player.IsAlive && !player.Combat.IsInCombat && (player.Flags & PlayerFlags.Ghost) == 0
            && player.Map is { Template: not ({ IsDungeon: true } or { IsBattleground: true }) }
            && !player.Movement.HasFlag(MovementFlags.Swimming)
            && (player.UnitFlags & UnitFlags.TaxiFlight) == 0
            && !FormInterlocks.IsInDisallowedMountForm(player);

    private static float RemainingYards(Player player, PlayerbotRoute route)
    {
        Vector3 at = new(player.X, player.Y, player.Z);
        float total = 0;
        for (int i = Math.Max(1, route.NextPoint); i < route.Points.Count; i++)
        {
            total += Vector3.Distance(at, route.Points[i]);
            at = route.Points[i];
        }

        return total;
    }

    private static bool IsCasting(SpellFeature spells, Player player)
        => spells.System.GetState(player.Guid)?.CurrentCast is { State: SpellCastState.Preparing or SpellCastState.Casting };

    /// <summary>The managed packets that talk to an NPC, loot, or attack: a mounted bot gets off before them.</summary>
    internal static bool NeedsDismount(WorldOpcode opcode) => opcode is WorldOpcode.CmsgGossipHello or WorldOpcode.CmsgGossipSelectOption
        or WorldOpcode.CmsgBuyItem or WorldOpcode.CmsgSellItem or WorldOpcode.CmsgListInventory or WorldOpcode.CmsgTrainerList
        or WorldOpcode.CmsgTrainerBuySpell or WorldOpcode.CmsgRepairItem or WorldOpcode.CmsgAttackswing or WorldOpcode.CmsgLoot
        or WorldOpcode.CmsgLootMoney or WorldOpcode.CmsgAutostoreLootItem or WorldOpcode.CmsgLootRelease or WorldOpcode.CmsgGameobjUse
        or WorldOpcode.CmsgQuestgiverHello or WorldOpcode.CmsgQuestgiverQueryQuest or WorldOpcode.CmsgQuestgiverAcceptQuest
        or WorldOpcode.CmsgQuestgiverCompleteQuest or WorldOpcode.CmsgQuestgiverChooseReward or WorldOpcode.CmsgAuctionListItems
        or WorldOpcode.CmsgAuctionPlaceBid or WorldOpcode.CmsgAuctionSellItem or WorldOpcode.CmsgBankerActivate
        or WorldOpcode.CmsgSpiritHealerActivate or WorldOpcode.CmsgActivatetaxi
        or WorldOpcode.CmsgActivatetaxiexpress;

    /// <summary>Takes the bot's mount off when <paramref name="opcode"/> is one a rider cannot send. Called on the world thread by <c>TryManagedAction</c>.</summary>
    internal static void DismountForAction(WorldSession session, Player player, WorldOpcode opcode)
    {
        if (!NeedsDismount(opcode) || !MountService.IsMounted(player) || session.Services.GetService<SpellFeature>() is not { } spells) return;
        spells.System.RemoveSpellsCausingAura(player, AuraType.Mounted);
        if (MountService.IsMounted(player)) MountService.Unmount(spells.System, player);
        For(session)._casting = false;
    }
}

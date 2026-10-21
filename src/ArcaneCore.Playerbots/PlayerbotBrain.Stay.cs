using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Scenario stay (<see cref="PlayerbotStay"/>): a bot waiting for its event scenario owns its think after the retreat, water and pvp steps
/// and before any quest, grind or explore step. In this order: fight back a player that provokes it, the pvp fight it is in, or a creature
/// attacking it; else walk back to the stay when it is outside the radius (and on to within 0.75 x the radius) and within
/// <see cref="StayWalkYards"/> on the same map; else hold, rest and buff. No PvP flag is raised and no post is scanned. An expired lease
/// ends the stay. Everything is memory only.
/// </summary>
internal sealed partial class PlayerbotBrain
{
    /// <summary>A stayer farther than this (or on another map) stands where it is: the owner teleports it back after its grace.</summary>
    internal const float StayWalkYards = 200f;

    private PlayerbotStay? _stay;
    private PlayerbotStay? _stayAreaStay;
    private PlayerbotHomeArea? _stayArea;

    /// <summary>
    /// The stay the bot keeps, or null. Setting the same stay id and centre again only renews the lease; any other value drops the current
    /// intent (the bot stops what it was walking to). World thread.
    /// </summary>
    internal PlayerbotStay? Stay
    {
        get => _stay;
        set
        {
            PlayerbotStay? old = _stay;
            _stay = value;
            if (value is null) return;
            if (old is not null && old.StayId == value.StayId && old.Centre == value.Centre && old.MapId == value.MapId && old.Radius == value.Radius) return;
            DropIntent();
        }
    }

    /// <summary>The stay as the inspection shows it (<c>r=30 stay=3 map=0 centre=x,y,z</c>), or null.</summary>
    internal string? StayReport => _stay is { } stay
        ? FormattableString.Invariant($"r={stay.Radius:0.#} stay={stay.StayId} map={stay.MapId} centre={stay.Centre.X:F1},{stay.Centre.Y:F1},{stay.Centre.Z:F1}")
        : null;

    /// <summary>One think of a stayer; true when it owns the think.</summary>
    private bool TryStay(Player player, uint interval)
    {
        if (_stay is not { } stay) return false;
        if (unchecked((int)(stay.UntilMs - _session.World.NowMs)) <= 0)
        {
            _stay = null;
            return false;
        }

        _stall.Reset();
        PlayerbotThinkProbe.Phase("stay");

        // Self-defence only (the first steps of a defender's foe): no scan of the surroundings.
        Unit? foe = null;
        if (player.Map is not null)
        {
            if (PlayerbotHostility.FirstProvokingPlayer(player) is { } provoker) foe = provoker;
            else if (_pvpFight is { Target: { IsInWorld: true, IsAlive: true } current }) foe = current;
            else foe = FindDefensiveAttacker(player);
        }

        if (foe is not null)
        {
            FightUnit(player, foe, interval);
            return true;
        }

        if (_pvpFight is { Target: not null } ended)
        {
            ended.Stop(player);
            AbandonRoute();
        }

        _goal = PlayerbotGoalKind.Home;
        TargetEntry = 0;
        if (player.Combat.IsInCombat) return true;

        if (player.MapId == stay.MapId && Vector2.Distance(new Vector2(player.X, player.Y), new Vector2(stay.Centre.X, stay.Centre.Y)) <= StayWalkYards
            && TryReturnHome(player, StayAreaOf(stay), interval, errandsFirst: false)) return true;

        SpellFeature? spells = _session.Services.GetService<SpellFeature>();
        if (_needsRest || (spells is not null && PlayerbotConsumables.TryFindRecovery(player, spells.System, out _)))
        {
            Rest(player);
            return true;
        }

        if (_combatSpells.UpdateOutOfCombat(player, interval)) return true;
        if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0) PlayerbotMovementControl.Stop(_session, player);
        return true;
    }

    /// <summary>The home area of a stay (cached per stay id and centre): a circle at its centre.</summary>
    private PlayerbotHomeArea StayAreaOf(PlayerbotStay stay)
    {
        if (_stayAreaStay is null || _stayAreaStay.StayId != stay.StayId || _stayAreaStay.Centre != stay.Centre || _stayAreaStay.MapId != stay.MapId
            || _stayAreaStay.Radius != stay.Radius || _stayArea is null)
        {
            _stayArea = new PlayerbotHomeArea(stay.MapId, stay.Centre, stay.Radius, "stay");
            _stayAreaStay = stay;
        }

        return _stayArea;
    }
}

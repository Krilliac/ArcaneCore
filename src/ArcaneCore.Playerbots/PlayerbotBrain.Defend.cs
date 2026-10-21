using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Defend mode (<c>.playerbot defend</c>, <see cref="PlayerbotDefendPost"/>): a bot with a post owns its think right after death, recovery and
/// the transit legs, before any quest, grind or explore step. In this order: keep the PvP flag on (CMSG_TOGGLE_PVP; clearing the post sends
/// it off again and vmangos' five-minute linger then runs out); fight a player attacking it, then the nearest hostile player inside the post;
/// else help a friendly creature in combat inside the post by fighting what it fights; else heal a wounded defender of the same post; else
/// walk back to the post when it is outside the radius (and on to within 0.75 x the radius), unless it carries resurrection sickness (it then
/// stays where it is until the aura ends: <c>decision=defend-wait-sickness</c>); else hold, rest and buff. Everything is memory only.
/// </summary>
internal sealed partial class PlayerbotBrain
{
    /// <summary>A defender looks for hostile players and for friendly creatures in combat at most this often.</summary>
    internal const uint DefendScanMs = 1_000;

    /// <summary>A defender heals a wounded defender of its post from this far.</summary>
    internal const float DefendHealYards = 40f;

    /// <summary>A defender keeps fighting what it fights this far outside the post radius, so a chase does not drop at the edge.</summary>
    private const float DefendChaseMarginYards = 10f;

    private PlayerbotDefendPost? _defend;
    private uint _defendScanAtMs;
    private bool _defendScanned;
    private Unit? _defendScanTarget;
    private uint _defendHealAtMs;
    private bool _defendHealScanned;
    private Player? _defendHealTarget;
    private PlayerbotDefendPost? _defendAreaPost;
    private PlayerbotHomeArea? _defendArea;
    private bool _defendWaitsSickness;

    /// <summary>The defender holds where it stands until its resurrection sickness ends (<c>decision=defend-wait-sickness</c> in the risk line).</summary>
    internal bool DefendWaitsSickness => _defendWaitsSickness;

    /// <summary>
    /// The post the bot defends, or null (<c>.playerbot defend</c>; <see cref="PlayerbotDefendPost"/>). Memory only: never saved, so a restart
    /// ends it. Setting it publishes the bot to the other defenders of the post (the heals) and drops what the last post scanned.
    /// </summary>
    internal PlayerbotDefendPost? Defend
    {
        get => _defend;
        set
        {
            _defend = value;
            _defendScanned = false;
            _defendScanTarget = null;
            _defendHealScanned = false;
            _defendHealTarget = null;
            _defendWaitsSickness = false;
            _pvpFight?.Forget();
            if (_session.Player is not { } player) return;
            if (value is null) PlayerbotDefend.Unregister(player);
            else PlayerbotDefend.Register(player, value);
        }
    }

    /// <summary>The post as the inspection shows it (<c>r=40 post=3 map=0 centre=x,y,z</c>), or null.</summary>
    internal string? DefendReport => _defend is { } post
        ? FormattableString.Invariant($"r={post.Radius:0.#} post={post.PostId} map={post.MapId} centre={post.Centre.X:F1},{post.Centre.Y:F1},{post.Centre.Z:F1}")
        : null;

    /// <summary>
    /// One think of a defender; true when it owns the think. Without a post: false, after letting go of the PvP flag the post turned on.
    /// </summary>
    private bool TryDefend(Player player, uint interval)
    {
        if (_defend is not { } post)
        {
            _defendWaitsSickness = false;
            ReleaseDefendFlag();
            return false;
        }

        // A defender holding a post is neither stalled nor idle: the stall watch would set its "goal" aside after a quiet while.
        _stall.Reset();
        if (!ReferenceEquals(PlayerbotDefend.PostOf(player), post)) PlayerbotDefend.Register(player, post);
        PlayerbotThinkProbe.Phase("defend");
        _defendWaitsSickness = WaitsOutSickness(player);

        // 1. The PvP flag: a defender is a fair target of the players it fights (and flags itself by fighting them anyway), asked for again every
        // think until the server shows it.
        if ((player.Flags & PlayerFlags.PvpDesired) == 0 && _session.TryManagedAction(WorldOpcode.CmsgTogglePvp, [1])) PlayerbotDefend.OwnFlag(player);

        // 2-3. Fight: whoever attacks the bot, what it fights already, then the scan of the post.
        Unit? foe = DefendFoe(player, post);
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

        // 4. Heal a wounded defender of the post.
        if (TryHealDefender(player, post))
        {
            _goal = PlayerbotGoalKind.Home;
            return true;
        }

        // 5. Back to the post: lift-aware like the home walk (PlayerbotBrain.TryReturnHome), and no errand holds a defender off its post. Not while
        // the bot carries resurrection sickness: a defender the spirit healer revived stays at the graveyard until the aura ends (rests, buffs,
        // fights back what attacks it, as above) instead of walking back under the raid that killed it and dying again.
        if (!_defendWaitsSickness && !player.Combat.IsInCombat && TryReturnHome(player, DefendAreaOf(post), interval, errandsFirst: false)) return true;

        // Hold the post (or the graveyard, while sick): rest, buff, and otherwise stand.
        _goal = PlayerbotGoalKind.Home;
        TargetEntry = 0;
        if (player.Combat.IsInCombat) return true;
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

    /// <summary>
    /// The defender carries a temporary resurrection sickness (<see cref="PlayerbotSickness"/>, spell
    /// <see cref="PlayerbotSickness.Spell"/>) and so does not walk back to its post yet. A permanent aura (one that never ends) does not hold it.
    /// </summary>
    private bool WaitsOutSickness(Player player) => PlayerbotSickness.Holder(_session, player) is { IsPermanent: false, Duration: > 0 };

    /// <summary>A defender that no longer has a post lets go of the PvP flag it turned on (the server's linger timer then runs).</summary>
    private void ReleaseDefendFlag()
    {
        if (_session.Player is { } player) PlayerbotDefend.ReleaseFlag(_session, player);
    }

    /// <summary>The home area of a post (cached per post): a circle at its centre.</summary>
    private PlayerbotHomeArea DefendAreaOf(PlayerbotDefendPost post)
    {
        if (!ReferenceEquals(_defendAreaPost, post) || _defendArea is null)
        {
            _defendArea = new PlayerbotHomeArea(post.MapId, post.Centre, post.Radius, "defend");
            _defendAreaPost = post;
        }

        return _defendArea;
    }

    /// <summary>
    /// Whom the defender fights now: a player attacking it, the unit it fights already while still in the post, a creature attacking it, then the
    /// result of the last scan of the post (hostile players first, then what a friendly creature in combat fights); null when there is none.
    /// </summary>
    private Unit? DefendFoe(Player player, PlayerbotDefendPost post)
    {
        if (player.Map is not { } map) return null;
        if (PlayerbotHostility.FirstProvokingPlayer(player) is { } provoker) return provoker;
        if (player.MapId != post.MapId) return null;
        if (_pvpFight is { Target: { } current } && DefendTargetValid(player, map, post, current)) return current;
        // An attacker of the defender is always fought; a creature that only holds it on its threat list is skipped like a scanned one.
        if (FindDefensiveAttacker(player) is { } creature && !DefendSkips(player, creature)) return creature;

        uint now = _session.World.NowMs;
        if (!_defendScanned || unchecked((int)(now - _defendScanAtMs)) >= DefendScanMs)
        {
            _defendScanned = true;
            _defendScanAtMs = now;
            _defendScanTarget = ScanPost(player, map, post);
            return _defendScanTarget;
        }

        return _defendScanTarget is { } cached && DefendTargetValid(player, map, post, cached) && !DefendSkips(player, cached) ? cached : null;
    }

    /// <summary>
    /// The defender passes over <paramref name="unit"/> when it picks a target: a civilian (<see cref="PlayerbotTargetFilter.IsCivilian"/>), one it found
    /// no path to in the last 30 s (<see cref="PlayerbotPvpFight.Unreachable"/>) or one on another level (a wall, a rampart, another floor). Never one
    /// that attacks the defender: that fight is not a choice.
    /// </summary>
    private bool DefendSkips(Player player, Unit unit)
    {
        if (ReferenceEquals(unit.Combat.Victim, player) || player.Combat.Attackers.Contains(unit)) return false;
        if (unit is Creature creature && PlayerbotTargetFilter.IsCivilian(creature)) return true;
        return Pvp.IsSkipped(unit) || PlayerbotPvpFight.OnOtherLevel(player, unit);
    }

    /// <summary>Whether the defender may fight <paramref name="unit"/> now: attackable, inside the post (a little beyond it while chasing).</summary>
    private static bool DefendTargetValid(Player player, Map map, PlayerbotDefendPost post, Unit unit)
    {
        if (!unit.IsInWorld || !unit.IsAlive || !ReferenceEquals(unit.Map, map)) return false;
        if (Vector2.Distance(new Vector2(unit.X, unit.Y), new Vector2(post.Centre.X, post.Centre.Y)) > post.Radius + DefendChaseMarginYards) return false;
        return unit switch
        {
            Player other => PlayerbotHostility.MayEngage(player, other, PlayerbotEngageReason.Defend, null),
            Creature creature => !creature.IsInEvadeMode && map.Combat.Hooks.CanAttack(player, creature),
            _ => false,
        };
    }

    /// <summary>
    /// The scan of the post, at most once per <see cref="DefendScanMs"/>: the nearest hostile player inside the radius, else the nearest enemy
    /// of a friendly creature inside the radius that is in combat (its victim and its attackers).
    /// </summary>
    private Unit? ScanPost(Player player, Map map, PlayerbotDefendPost post)
    {
        Player? bestPlayer = null;
        float bestPlayerDistance = float.MaxValue;
        Unit? bestHelp = null;
        float bestHelpDistance = float.MaxValue;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            WorldObject? obj = map.FindObject(guid);
            if (obj is Player other)
            {
                if (!InsidePost(other, post) || !PlayerbotHostility.MayEngage(player, other, PlayerbotEngageReason.Defend, null) || DefendSkips(player, other)) continue;
                float distance = Distance(player, other);
                if (distance < bestPlayerDistance) (bestPlayer, bestPlayerDistance) = (other, distance);
            }
            else if (obj is Creature friend && friend.IsAlive && friend.Combat.IsInCombat && InsidePost(friend, post) && map.Combat.Hooks.IsFriendly(player, friend))
            {
                ConsiderHelp(player, map, post, friend.Combat.Victim, ref bestHelp, ref bestHelpDistance);
                foreach (Unit attacker in friend.Combat.Attackers) ConsiderHelp(player, map, post, attacker, ref bestHelp, ref bestHelpDistance);
            }
        }

        return bestPlayer ?? bestHelp;
    }

    private void ConsiderHelp(Player player, Map map, PlayerbotDefendPost post, Unit? enemy, ref Unit? best, ref float bestDistance)
    {
        if (enemy is null || ReferenceEquals(enemy, player) || map.Combat.Hooks.IsFriendly(player, enemy) || !DefendTargetValid(player, map, post, enemy)
            || DefendSkips(player, enemy)) return;
        float distance = Distance(player, enemy);
        if (distance >= bestDistance) return;
        best = enemy;
        bestDistance = distance;
    }

    private static bool InsidePost(WorldObject unit, PlayerbotDefendPost post)
        => unit.IsInWorld && Vector2.Distance(new Vector2(unit.X, unit.Y), new Vector2(post.Centre.X, post.Centre.Y)) <= post.Radius;

    /// <summary>
    /// A healer class with a direct heal that lands on a friend tops up the most wounded defender of the same post (below 85% health) within
    /// <see cref="DefendHealYards"/> when <see cref="IsSafeHealTarget"/> holds: the heal of <see cref="TryHealTagger"/>, for the post mates.
    /// True: a heal was accepted and the think is spent.
    /// </summary>
    private bool TryHealDefender(Player player, PlayerbotDefendPost post)
    {
        if (player.Class is not (Game.Class.Priest or Game.Class.Paladin or Game.Class.Druid or Game.Class.Shaman) || player.Map is not { } map) return false;
        // The walk over what the bot sees runs at most once per DefendScanMs; between the scans the last pick is kept while it still needs the heal.
        uint now = _session.World.NowMs;
        if (!_defendHealScanned || unchecked((int)(now - _defendHealAtMs)) >= DefendScanMs)
        {
            _defendHealScanned = true;
            _defendHealAtMs = now;
            _defendHealTarget = null;
            float lowest = 85f;
            foreach (ObjectGuid guid in player.VisibleObjects)
            {
                if (map.FindObject(guid) is not Player mate || ReferenceEquals(mate, player) || mate.MaxHealth == 0
                    || PlayerbotDefend.PostOf(mate) is not { } matePost || matePost.PostId != post.PostId
                    || Distance(player, mate) > DefendHealYards || !IsSafeHealTarget(player, mate)) continue;
                float percent = mate.Health * 100f / mate.MaxHealth;
                if (percent < lowest) (_defendHealTarget, lowest) = (mate, percent);
            }
        }

        if (_defendHealTarget is not { } wounded || !wounded.IsInWorld || !wounded.IsAlive || wounded.MaxHealth == 0
            || wounded.Health * 100f / wounded.MaxHealth >= 85f || Distance(player, wounded) > DefendHealYards || !IsSafeHealTarget(player, wounded))
            return false;
        PlayerbotAbilities abilities = _combatSpells.Abilities(player);
        return _combatSpells.FirstCastable(player, abilities.DirectHeals, wounded, abilities.HealsAnotherUnit) is { } heal
            && _combatSpells.CastAt(player, heal, wounded);
    }
}

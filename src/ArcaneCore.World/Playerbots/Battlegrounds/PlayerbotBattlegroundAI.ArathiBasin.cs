using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.Battlegrounds;

/// <summary>
/// Arathi Basin for one bot: the team plan (<see cref="PlayerbotArathiBasinTactics.Plan"/>) read from the match as it is, and this bot's
/// order carried out. In order: a retreat (hurt and outnumbered, to an own node's graveyard); the enemy its order names at its node, else
/// the usual enemy pick; the out-of-combat spells; a battleground buff in reach (<see cref="PlayerbotArathiBasinTactics.ChooseBuff"/>,
/// stepped on: the buffs are traps, MatchRuntime.UpdateBuffTraps); then the node: an assault walks to the banner and casts 21651 at it
/// through the client's CMSG_CAST_SPELL (the open-lock effect turns the opening into the node click; a hit cancels the cast, Spell.dbc
/// interrupt flags 0x1F), leaving the cast to a teammate already casting there (vmangos <c>AtFlag</c>, BattleBotWaypoints.cpp:181-189);
/// a defence holds by the banner, and casts at it when the enemy has assaulted it.
/// </summary>
internal sealed partial class PlayerbotBattlegroundAI
{
    /// <summary>The team's bots and their roles, from the coordinator's last scan (<see cref="SetTeamRoles"/>); null before the first.</summary>
    private IReadOnlyDictionary<ObjectGuid, PlayerbotBattlegroundRole>? _teamRoles;

    /// <summary>Buffs this bot reached that did not go (a trap out of its reach on uneven ground): not walked to again.</summary>
    private readonly HashSet<ObjectGuid> _buffsTried = [];

    /// <summary>This bot's order of its last Arathi Basin think (inspection and tests).</summary>
    internal PlayerbotAbOrder? ArathiOrder { get; private set; }

    /// <summary>The buffs this bot walked onto (inspection and tests).</summary>
    internal int BuffsReached { get; private set; }

    /// <summary>The coordinator's roles of the bots of this bot's team in its match: the team plan orders exactly these.</summary>
    internal void SetTeamRoles(IReadOnlyDictionary<ObjectGuid, PlayerbotBattlegroundRole> roles) => _teamRoles = roles;

    private void PlayArathiBasin(Player player, ArathiBasin ab, Team team, PlayerbotBattlegroundRole role, uint interval)
    {
        List<(int Node, GameObject Banner)> banners = Banners(player, ArathiBasin.NodeCount);
        if (banners.Count == 0 || player.Map is not { } map)
        {
            ArathiOrder = null;
            Activity = "no-banners";
            Hold(player);
            return;
        }

        PlayerbotAbPicture picture = ArathiPicture(player, map, ab, team, role, banners);
        if (!PlayerbotArathiBasinTactics.Plan(picture).TryGetValue(player.Guid, out PlayerbotAbOrder order))
        {
            ArathiOrder = null;
            Activity = "idle";
            Hold(player);
            return;
        }

        ArathiOrder = order;
        Team enemyTeam = BattlegroundConstants.OtherTeam(team);
        if (order.Duty == PlayerbotAbDuty.Retreat)
        {
            if (_target is not null) ClearTarget(player);
            Activity = "retreating";
            TargetEntry = 0;
            WalkTo(player, order.Goal, interval, toward: true);
            return;
        }

        // The enemy at its node (a defender fights whoever comes to its node; an assaulter clears the banner before the cast a hit would
        // cancel), else the usual pick (an attacker of the bot, the one it fights, the nearest within 30 yards).
        Unit? enemy = null;
        if (!order.Target.IsEmpty && map.FindObject(order.Target) is Player named && IsEnemy(player, named, ab, enemyTeam)
            && (order.Duty == PlayerbotAbDuty.Defend || Distance(player, named) <= EngageYards * 1.5f))
            enemy = named;
        enemy ??= SelectEnemy(player, ab, team);
        if (enemy is not null)
        {
            Activity = "fighting";
            Fight(player, enemy, interval);
            return;
        }

        if (_target is not null) ClearTarget(player);
        TargetEntry = 0;
        if (!player.Combat.IsInCombat && _spells.UpdateOutOfCombat(player, interval)) return;

        if (TakeBuff(player, map, interval)) return;

        GameObject banner = banners.First(b => b.Node == order.Node).Banner;
        TargetEntry = banner.Entry;
        int teamIndex = BattlegroundConstants.TeamIndex(team);
        bool clickable = PlayerbotArathiBasinTactics.Clickable(ab.NodeStatus(order.Node), teamIndex);
        if (order.Duty == PlayerbotAbDuty.Defend && !clickable)
        {
            Activity = "defending";
            Guard(player, Position(banner), interval);
            return;
        }

        Activity = order.Duty == PlayerbotAbDuty.Defend ? "retaking" : "assaulting";
        if (Distance(player, banner) > InteractYards)
        {
            WalkTo(player, Position(banner), interval, toward: false);
            return;
        }

        if (!PlayerbotMovementControl.Stop(_session, player)) return;
        _route = null;
        if (TeammateCasting(player, banner))
        {
            // A friend casts there already (vmangos AtFlag): stand by it and cover the cast.
            Activity = "covering";
            return;
        }

        if (_session.Services.GetService<SpellFeature>() is not { } feature || feature.System.Store.Get(BannerSpell) is not { } spell)
        {
            Activity = "no-banner-spell";
            return;
        }

        if (_spells.CastAtObject(player, spell, banner)) BannerCasts++;
    }

    /// <summary>Walk onto the buff <see cref="PlayerbotArathiBasinTactics.ChooseBuff"/> picks; true while doing so.</summary>
    private bool TakeBuff(Player player, Map map, uint interval)
    {
        if (map.FindUpdater<GameObjectMapSystem>() is not { } objects) return false;
        var buffs = new List<PlayerbotBgBuff>();
        foreach (GameObject go in objects.GameObjects)
            if (go.IsSpawned && go.Type == GameObjectType.Trap && BattlegroundConstants.BuffEntries.Contains(go.Entry) && !_buffsTried.Contains(go.Guid))
                buffs.Add(new PlayerbotBgBuff(go.Guid, go.Entry, Position(go)));
        if (PlayerbotArathiBasinTactics.ChooseBuff(Position(player), player.Combat.IsInCombat, Fraction(player.Health, player.MaxHealth), ManaFraction(player), buffs)
            is not { } buff) return false;

        // On it (on the ground plane: the route ends at the ground's height, not the object's): the trap fires on the server's next map
        // update (3 yards); one that did not is not walked to again.
        if (Vector2.Distance(new Vector2(player.X, player.Y), new Vector2(buff.At.X, buff.At.Y)) <= 2.5f)
        {
            _buffsTried.Add(buff.Guid);
            BuffsReached++;
            return false;
        }

        Activity = "taking-buff";
        TargetEntry = buff.Entry;
        if (WalkTo(player, buff.At, interval, toward: false)) return true;
        _buffsTried.Add(buff.Guid);
        return false;
    }

    /// <summary>The team's side of the match as the plan reads it.</summary>
    private PlayerbotAbPicture ArathiPicture(Player player, Map map, ArathiBasin ab, Team team, PlayerbotBattlegroundRole role, List<(int Node, GameObject Banner)> banners)
    {
        var nodes = new List<PlayerbotAbNode>(ArathiBasin.NodeCount);
        foreach (IGrouping<int, (int Node, GameObject Banner)> node in banners.GroupBy(b => b.Node).OrderBy(g => g.Key))
            nodes.Add(new PlayerbotAbNode(node.Key, Position(node.First().Banner), ab.NodeStatus(node.Key)));

        var bots = new List<PlayerbotAbMember>();
        var friends = new List<PlayerbotAbUnit>();
        var enemies = new List<PlayerbotAbUnit>();
        Team enemyTeam = BattlegroundConstants.OtherTeam(team);
        bool selfListed = false;
        foreach (Player other in map.Players)
        {
            if (!other.IsInWorld || !ReferenceEquals(other.Map, map) || ab.PlayerTeam(other.Guid) is not { } side) continue;
            bool alive = other.IsAlive && (other.Flags & PlayerFlags.Ghost) == 0;
            if (side == enemyTeam)
            {
                if (alive) enemies.Add(new PlayerbotAbUnit(other.Guid, Position(other)));
                continue;
            }

            bool self = other.Guid == player.Guid;
            PlayerbotBattlegroundRole? botRole = self ? role
                : _teamRoles is not null && _teamRoles.TryGetValue(other.Guid, out PlayerbotBattlegroundRole r) ? r : null;
            if (botRole is { } known)
            {
                selfListed |= self;
                bots.Add(new PlayerbotAbMember(other.Guid, Position(other), alive, Fraction(other.Health, other.MaxHealth), known == PlayerbotBattlegroundRole.Defender));
            }
            else if (alive)
            {
                friends.Add(new PlayerbotAbUnit(other.Guid, Position(other)));
            }
        }

        if (!selfListed)
            bots.Add(new PlayerbotAbMember(player.Guid, Position(player), player.IsAlive, Fraction(player.Health, player.MaxHealth), role == PlayerbotBattlegroundRole.Defender));

        BattlegroundStartLocation start = team == Team.Alliance ? ab.Template.AllianceStart : ab.Template.HordeStart;
        return new PlayerbotAbPicture
        {
            Team = team, Nodes = nodes, Bots = bots, Enemies = enemies, Friends = friends, Home = new Vector3(start.X, start.Y, start.Z),
        };
    }

    private static float Fraction(uint value, uint max) => max == 0 ? 1f : Math.Clamp(value / (float)max, 0f, 1f);

    private static float ManaFraction(Player player)
        => player.PowerType == PowerType.Mana
            ? Fraction(player.GetUInt32(UpdateFields.UnitFieldPower1), player.GetUInt32(UpdateFields.UnitFieldMaxpower1))
            : 1f;
}

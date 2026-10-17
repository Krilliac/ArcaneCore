using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.Battlegrounds;

/// <summary>
/// Alterac Valley (map 30): the decisions are <see cref="PlayerbotAlteracValleyTactics"/>'s, over the match's shared
/// <see cref="PlayerbotAlteracValleyState"/>; this part carries them out with the bot's walk, fight and banner cast (spell 21651 at the
/// node's banner, the real gameobject use path: Spell::EffectOpenLock routes the opened banner to EventPlayerClickedOnFlag,
/// <see cref="MatchRuntime.RoutesBannerUse"/>).
/// </summary>
internal sealed partial class PlayerbotBattlegroundAI
{
    private readonly AvNode[] _avNodes = new AvNode[AlteracValley.NodeCount];

    /// <summary>The last Alterac Valley objective the bot decided (inspection and tests).</summary>
    internal AvObjective AvObjective { get; private set; }

    private void PlayAlteracValley(Player player, AlteracValley av, Team team, PlayerbotBattlegroundRole role, uint interval)
    {
        if (player.Map is not { } map || _session.Services.GetService<BattlegroundFeature>() is not { } feature)
        {
            Activity = "idle";
            Hold(player);
            return;
        }

        long now = Now;
        int teamIndex = BattlegroundConstants.TeamIndex(team);
        int enemyIndex = 1 - teamIndex;
        PlayerbotAlteracValleyState state = PlayerbotAlteracValleyState.For(av);
        RefreshAlteracValley(state, av, map, feature, now);
        for (int n = 0; n < AlteracValley.NodeCount; n++)
        {
            bool banner = state.Banners[n] is { IsSpawned: true } go && ReferenceEquals(go.Map, map);
            _avNodes[n] = new AvNode(av.NodeOwner(n), av.NodeTotalOwner(n), av.NodeState(n), av.IsTower(n), banner, state.NodeAt[n]);
        }

        Creature? captain = Living(state.Captains[enemyIndex], map);
        Creature? general = Living(state.Generals[enemyIndex], map);
        BattlegroundStartLocation enemyStart = team == Team.Alliance ? av.Template.HordeStart : av.Template.AllianceStart;
        ObjectGuid self = player.Guid;
        AvObjective objective = PlayerbotAlteracValleyTactics.Decide(new AvDecision
        {
            TeamIndex = teamIndex,
            Role = role,
            Lane = PlayerbotAlteracValleyTactics.LaneOf(self),
            Position = Position(player),
            Nodes = _avNodes,
            EnemyCaptain = captain is null ? null : Position(captain),
            EnemyGeneral = general is null ? null : Position(general),
            EnemyBase = new Vector3(enemyStart.X, enemyStart.Y, enemyStart.Z),
            OthersClaiming = node => state.ClaimsAt(node, self, now),
            Blocked = node => state.Banners[node] is { } banner && TeammateCasting(player, banner),
        });
        AvObjective = objective;
        state.Claim(self, objective.Kind is AvObjectiveKind.Defend or AvObjectiveKind.Hold ? objective.Node : -1, now);

        switch (objective.Kind)
        {
            case AvObjectiveKind.Assault or AvObjectiveKind.Defend:
                ClickAlteracBanner(player, state.Banners[objective.Node], objective, interval);
                return;
            case AvObjectiveKind.Hold:
                Activity = "av-guard";
                TargetEntry = state.Banners[objective.Node]?.Entry ?? 0;
                Guard(player, objective.At!.Value, interval);
                return;
            case AvObjectiveKind.KillCaptain or AvObjectiveKind.KillGeneral:
                Creature boss = objective.Kind == AvObjectiveKind.KillCaptain ? captain! : general!;
                Activity = objective.Kind == AvObjectiveKind.KillCaptain ? "av-captain" : "av-general";
                TargetEntry = boss.Entry;
                // Within reach the fight picks it (or its guards first, the nearest enemy); until then the bot closes in.
                if (Distance(player, boss) <= EngageYards && IsEnemyCreature(player, boss)) Fight(player, boss, interval);
                else WalkTo(player, Position(boss), interval, toward: true);
                return;
            case AvObjectiveKind.Advance:
                Activity = "av-advance";
                WalkTo(player, objective.At!.Value, interval, toward: true);
                return;
            default:
                Activity = "idle";
                Hold(player);
                return;
        }
    }

    /// <summary>Walk to the node's banner and cast the banner spell at it in interaction range (an assault or a defence).</summary>
    private void ClickAlteracBanner(Player player, GameObject? banner, AvObjective objective, uint interval)
    {
        Vector3 at = banner is null ? objective.At!.Value : Position(banner);
        TargetEntry = banner?.Entry ?? 0;
        if (banner is null || Vector3.Distance(Position(player), at) > InteractYards)
        {
            Activity = objective.Kind == AvObjectiveKind.Defend ? "av-defend" : "av-assault";
            WalkTo(player, at, interval, toward: Vector3.Distance(Position(player), at) > 60f);
            return;
        }

        if (!PlayerbotMovementControl.Stop(_session, player)) return;
        _route = null;
        Activity = "av-claim-banner";
        if (_session.Services.GetService<SpellFeature>() is not { } feature || feature.System.Store.Get(BannerSpell) is not { } spell)
        {
            Activity = "no-banner-spell";
            return;
        }

        if (_spells.CastAtObject(player, spell, banner)) BannerCasts++;
    }

    private static Creature? Living(Creature? creature, Map map)
        => creature is { IsInWorld: true, IsAlive: true } && ReferenceEquals(creature.Map, map) ? creature : null;

    /// <summary>The node signature: each node's owner and state, three bits apiece (a change makes the banners due for a rescan).</summary>
    private static long Signature(AlteracValley av)
    {
        long signature = 0;
        for (int n = 0; n < AlteracValley.NodeCount; n++)
            signature = (signature << 3) | (long)((av.NodeOwner(n) * 2) + av.NodeState(n));
        return signature;
    }

    /// <summary>
    /// The match's shared scans, when due (<see cref="PlayerbotAlteracValleyState"/>): the clickable banner of every node (the objects whose
    /// battleground event names a node, that 21651 opens and whose opening the match routes to the node click), and the captains and
    /// generals by their events (vmangos BG_AV_Events: 48/49 the captains, 61/62 the generals; creature_battleground).
    /// </summary>
    private static void RefreshAlteracValley(PlayerbotAlteracValleyState state, AlteracValley av, Map map, BattlegroundFeature feature, long now)
    {
        bool staleBanner = false;
        foreach (GameObject? banner in state.Banners)
            staleBanner |= banner is not null && (!banner.IsSpawned || !ReferenceEquals(banner.Map, map));
        state.RefreshObjects(now, Signature(av), staleBanner, s =>
        {
            if (map.FindUpdater<GameObjectMapSystem>() is not { } objects) return;
            foreach (GameObject go in objects.GameObjects)
            {
                if (!go.IsSpawned || go.Spawn is not { } spawn || GameObjectLocks.LockIdOf(go.Template) == 0 || !MatchRuntime.RoutesBannerUse(go.Template)) continue;
                foreach ((byte node, byte _) in feature.Events.ObjectEvents(spawn.Guid))
                {
                    if (node >= AlteracValley.NodeCount) continue;
                    s.Banners[node] = go;
                    s.NodeAt[node] = Position(go);
                }
            }
        });

        bool staleBoss = false;
        for (int team = 0; team < 2; team++)
            staleBoss |= (state.Captains[team] is { } c && !ReferenceEquals(c.Map, map)) || (state.Generals[team] is { } g && !ReferenceEquals(g.Map, map));
        state.RefreshBosses(now, staleBoss, s =>
        {
            if (map.FindUpdater<CreatureMapSystem>() is not { } creatures) return;
            foreach (Creature creature in creatures.Creatures)
            {
                if (creature.Spawn is not { } spawn || !creature.IsAlive) continue;
                foreach ((byte e1, byte _) in feature.Events.CreatureEvents(spawn.Guid))
                {
                    switch (e1)
                    {
                        case AlteracValley.EventCaptainAlliance: s.Captains[0] = creature; break;
                        case AlteracValley.EventCaptainHorde: s.Captains[1] = creature; break;
                        case AlteracValley.EventBossAlliance: s.Generals[0] = creature; break;
                        case AlteracValley.EventBossHorde: s.Generals[1] = creature; break;
                    }
                }
            }
        });
    }
}

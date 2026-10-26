using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.GameObjects;

/// <summary>
/// The summoning use of a dungeon meeting stone (GAMEOBJECT_TYPE_MEETING_STONE, type 23). Not vanilla 1.12: there the stone only queues for the
/// dungeon (CMSG_MEETINGSTONE_JOIN), and CMSG_GAMEOBJ_USE on it is refused (GameObject.cpp:1836-1841). This follows the later (TBC) summoning-stone
/// rules and is switched by <see cref="PlayerbotSummonOptions.MeetingStones"/> (on by default; off keeps the old refusal). A group member at the
/// stone (within interaction range) with another group member beside it and one group member selected summons the selected member: alive, not in
/// combat, not in a battleground, farther than <see cref="StoneRadiusYards"/> from the stone. The member gets the ordinary summon request toward the
/// stone (<see cref="GameObjectSpellEffects.Offer"/>) and accepts it with CMSG_SUMMON_RESPONSE as for a warlock's summon. The LFG queue is not touched.
/// Thread affinity: world thread (a game object use).
/// </summary>
internal sealed class MeetingStoneSummon(IServiceProvider services, WorldRuntime world)
{
    /// <summary>A second group member counts as "at the stone" within this many yards of it, and the summoned member must be farther.</summary>
    public const float StoneRadiusYards = 20f;

    /// <summary>The use handler for <see cref="GameObjectType.MeetingStone"/> objects.</summary>
    public GameObjectUseResult Use(Player player, GameObject stone)
    {
        if (services.GetService<IOptions<PlayerbotOptions>>()?.Value is not { Summon.MeetingStones: true }
            || services.GetService<MeetingStoneFeature>()?.Groups is not { } groups
            || groups.GetGroup(player.Guid) is not { IsBattlegroundGroup: false } group
            || !player.IsAlive || player.Combat.IsInCombat || player.Selection.IsEmpty || player.Selection == player.Guid
            || !group.IsMember(player.Selection)
            || world.FindOnlinePlayer(player.Selection) is not { IsInWorld: true, IsAlive: true } target
            || target.Combat.IsInCombat || target.PendingSummon is { } pending && pending.ExpiresAtMs >= world.NowMs
            || target.Map is null || player.Map is null || target.Map.Template is { IsBattleground: true } || player.Map.Template is { IsBattleground: true })
        {
            return GameObjectUseResult.NotUsable;
        }

        if (target.MapId == stone.Map?.MapId && DistanceSquared(target, stone) <= StoneRadiusYards * StoneRadiusYards)
        {
            return GameObjectUseResult.NotUsable;
        }

        bool partner = false;
        foreach (GroupMemberSlot slot in group.Members)
        {
            if (slot.Guid == player.Guid || slot.Guid == target.Guid || world.FindOnlinePlayer(slot.Guid) is not { IsAlive: true } other)
            {
                continue;
            }

            if (ReferenceEquals(other.Map, stone.Map) && DistanceSquared(other, stone) <= StoneRadiusYards * StoneRadiusYards)
            {
                partner = true;
                break;
            }
        }

        if (!partner)
        {
            return GameObjectUseResult.NotUsable;
        }

        GameObjectSpellEffects.Offer(target, player, stone, world.NowMs);
        return GameObjectUseResult.Ok;
    }

    private static float DistanceSquared(WorldObject a, WorldObject b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }
}

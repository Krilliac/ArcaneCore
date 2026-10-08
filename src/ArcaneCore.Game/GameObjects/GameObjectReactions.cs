using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.GameObjects;

/// <summary>
/// How a game object judges a unit (vmangos GameObject::IsHostileTo and GameObject::IsFriendlyTo, GameObject.cpp:2076-2166; behaviour
/// re-implemented, no code copied). In order: a game master is never hostile and always friendly; an object with an owner answers as its owner
/// (through the map's combat hooks); a unit with a charmer or owner (a pet, a totem, a charmed unit) is judged as that charmer or owner; a wild
/// object (faction 0) is hostile to everyone and friendly to nobody; then, for a player, a forced reaction or the standing with the object's
/// reputation faction (Hostile and below is hostile, Friendly and above is friendly); and last the faction template relations. Neutral is
/// neither hostile nor friendly. The faction templates and the reputation state come from the map's combat hooks
/// (<see cref="ReputationCombatHooks"/> in the world, <see cref="FactionCombatHooks"/> without Faction.dbc); without either, a faction object
/// is neither hostile nor friendly, as vmangos is for a missing template.
/// </summary>
public static class GameObjectReactions
{
    /// <summary>A charmer or owner chain longer than this is not followed (a loop in the owner fields would otherwise never end).</summary>
    private const int MaxOwnerHops = 4;

    /// <summary>vmangos GameObject::IsHostileTo.</summary>
    public static bool IsHostileTo(GameObject go, Unit target, CombatHooks? hooks)
    {
        ArgumentNullException.ThrowIfNull(go);
        ArgumentNullException.ThrowIfNull(target);
        return Judge(go, target, hooks, hostile: true, depth: 0);
    }

    /// <summary>vmangos GameObject::IsFriendlyTo.</summary>
    public static bool IsFriendlyTo(GameObject go, Unit target, CombatHooks? hooks)
    {
        ArgumentNullException.ThrowIfNull(go);
        ArgumentNullException.ThrowIfNull(target);
        return Judge(go, target, hooks, hostile: false, depth: 0);
    }

    /// <summary>
    /// Whether the object may cast a helpful spell at <paramref name="target"/> (vmangos WorldObject::IsValidHelpfulTarget, Object.cpp:3818-3857:
    /// both reactions at least Unfriendly). A wild object has no faction template, which reads as Neutral there, so it may help anyone; any other
    /// object may help whom it is not hostile to.
    /// </summary>
    public static bool CanHelp(GameObject go, Unit target, CombatHooks? hooks)
    {
        ArgumentNullException.ThrowIfNull(go);
        ArgumentNullException.ThrowIfNull(target);
        return (go.OwnerGuid.IsEmpty && go.GetUInt32(UpdateFields.GameobjectFaction) == 0) || !IsHostileTo(go, target, hooks);
    }

    private static bool Judge(GameObject go, Unit target, CombatHooks? hooks, bool hostile, int depth)
    {
        if (target is Player { IsGameMaster: true })
        {
            return !hostile;
        }

        if (!go.OwnerGuid.IsEmpty && go.Map?.FindObject(go.OwnerGuid) is Unit owner)
        {
            return hooks is not null && (hostile ? hooks.IsHostileTo(owner, target) : hooks.IsFriendly(owner, target));
        }

        if (depth < MaxOwnerHops && target.CharmerOrOwnerGuid is { IsEmpty: false } controllerGuid
            && target.Map?.FindObject(controllerGuid) is Unit controller && !ReferenceEquals(controller, target))
        {
            return Judge(go, controller, hooks, hostile, depth + 1);
        }

        uint faction = go.GetUInt32(UpdateFields.GameobjectFaction);
        if (faction == 0)
        {
            return hostile;
        }

        (FactionTemplateCatalog? templates, ReputationReactionResolver? resolver) = Catalogs(hooks);
        if (templates?.Find(faction) is not { } tester || templates.Find(target.FactionTemplate) is not { } other)
        {
            return false;
        }

        if (target is Player player && tester.Faction != 0 && resolver?.ReputationOf(player) is { } reputation)
        {
            if (reputation.TryGetForcedRank(tester.Faction, out ReputationRank forced))
            {
                return hostile ? forced <= ReputationRank.Hostile : forced >= ReputationRank.Friendly;
            }

            if (resolver.Factions.Find(tester.Faction) is { CanHaveReputation: true } raw)
            {
                ReputationRank rank = reputation.Rank(raw);
                return hostile ? rank <= ReputationRank.Hostile : rank >= ReputationRank.Friendly;
            }
        }

        return hostile ? tester.IsHostileTo(other) : tester.IsFriendlyTo(other);
    }

    private static (FactionTemplateCatalog? Templates, ReputationReactionResolver? Resolver) Catalogs(CombatHooks? hooks) => hooks switch
    {
        ReputationCombatHooks reputation => (reputation.Resolver.Templates, reputation.Resolver),
        FactionCombatHooks factions => (factions.Factions, null),
        _ => (null, null),
    };
}

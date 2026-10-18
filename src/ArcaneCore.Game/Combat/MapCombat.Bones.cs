using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Combat;

public sealed partial class MapCombat
{
    /// <summary>
    /// How long bones stay in the world (vmangos Corpse::IsExpired, Corpse.cpp: a bones object expires an hour after it was made,
    /// ObjectAccessor::RemoveOldCorpses).
    /// </summary>
    public const long BonesLifetimeSeconds = 60 * 60;

    private readonly List<(Corpse Bones, long ExpiresAt)> _bones = [];

    /// <summary>The bones in this map (insignia removed in a battleground), oldest first.</summary>
    public IEnumerable<Corpse> Bones => _bones.Select(b => b.Bones);

    /// <summary>
    /// vmangos ConvertCorpseForPlayer (Map.cpp:3630-3660) for a battleground body whose insignia is taken: the corpse leaves the world, its
    /// owner (when online) has no body any more and is told so (MSG_CORPSE_QUERY), and bones with the body's look take its place. Returns the
    /// bones, or null when <paramref name="corpse"/> is not a body of this map.
    /// </summary>
    public Corpse? ConvertCorpseToBones(Corpse corpse)
    {
        ArgumentNullException.ThrowIfNull(corpse);
        if (corpse.Map is { } ownerMap && !ReferenceEquals(ownerMap, _map))
        {
            return ownerMap.Combat.ConvertCorpseToBones(corpse);
        }

        if (!_corpses.Contains(corpse))
        {
            return null;
        }

        RemoveCorpse(corpse);
        if (_world.FindOnlinePlayer(corpse.Owner) is { } owner && ReferenceEquals(owner.Combat.Corpse, corpse))
        {
            owner.Combat.Corpse = null;
            if (owner.IsInWorld)
            {
                owner.Session.Send(WorldOpcode.MsgCorpseQuery, BuildCorpseQueryFor(owner)); // "Notify the client that the corpse is gone"
            }
        }

        Corpse bones = Corpse.CreateBones(corpse);
        bones.InstanceId = _map.InstanceId;
        _map.AddObject(bones, isNewObject: true);
        _bones.Add((bones, NowSeconds + BonesLifetimeSeconds));
        return bones;
    }

    /// <summary>Take expired bones out of the world (vmangos ObjectAccessor::RemoveOldCorpses).</summary>
    private void RemoveExpiredBones()
    {
        if (_bones.Count == 0 || _bones[0].ExpiresAt > NowSeconds)
        {
            return;
        }

        long now = NowSeconds;
        for (int i = _bones.Count - 1; i >= 0; i--)
        {
            if (_bones[i].ExpiresAt <= now)
            {
                Corpse bones = _bones[i].Bones;
                _bones.RemoveAt(i);
                BonesRemoved?.Invoke(bones);
                if (ReferenceEquals(bones.Map, _map))
                {
                    _map.RemoveObject(bones);
                }
            }
        }
    }

    /// <summary>Raised when expired bones leave the world (their insignia loot goes with them).</summary>
    public event Action<Corpse>? BonesRemoved;
}

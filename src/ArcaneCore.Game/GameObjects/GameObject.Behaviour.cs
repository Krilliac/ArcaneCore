namespace ArcaneCore.Game.GameObjects;

// The per-object state of the type behaviours that vmangos keeps on GameObject (GameObject.h:276-295): unique users of a ritual,
// its summon target and first user, the group of the summoner, the arming of a trap and the restock timer of a chest.
public sealed partial class GameObject
{
    /// <summary>vmangos m_UniqueUsers (GameObject.h:283): the players taking part in a summoning ritual (one entry per player).</summary>
    internal HashSet<ObjectGuid> UniqueUsers { get; } = [];

    /// <summary>vmangos m_firstUser: the first player who used a ritual (AddUniqueUse, GameObject.cpp:739-772).</summary>
    internal ObjectGuid FirstUser { get; set; }

    /// <summary>vmangos m_summonTarget (SetSummonTarget, SpellEffects.cpp:5751-5752): the caster's selection when the ritual was created.</summary>
    public ObjectGuid SummonTarget { get; internal set; }

    /// <summary>vmangos m_playerGroupId (SetOwnerGroupId, SpellEffects.cpp:5770-5773): the group of the player who created the object, 0 for none.</summary>
    public uint OwnerGroupId { get; internal set; }

    /// <summary>
    /// GO_NOT_READY → GO_READY of a trap (GameObject.cpp:346-357): false until the first update after the object spawned, which starts
    /// the arming delay (trap.startDelay, data7). Hunter traps keep their own flag in the spell object registry.
    /// </summary>
    internal bool TrapArmed { get; set; }

    /// <summary>
    /// vmangos m_cooldownTime of a restocking chest in GO_NOT_READY (GameObject.cpp:381-394 and 629-639): the whole second after which the
    /// chest is ready again, or null.
    /// </summary>
    internal long? RestockAfterSecond { get; set; }

    /// <summary>vmangos ClearAllUsesData (GameObject.h:174-180) and the trap and restock state, at spawn and respawn.</summary>
    private void ResetBehaviourState()
    {
        UniqueUsers.Clear();
        FirstUser = default;
        TrapArmed = false;
        RestockAfterSecond = null;
    }
}

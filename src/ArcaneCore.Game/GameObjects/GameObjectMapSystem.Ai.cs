using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.GameObjects;

/// <summary>
/// A script of the objects of one entry (vmangos GameObjectAI, Objects/GameObjectAI.h): <see cref="OnTrapTarget"/> is asked when an
/// environmental trap found a target, <see cref="OnUse"/> when a player uses the object, and <see cref="Update"/> runs every update of the
/// object, spawned or not (GameObject::Update calls UpdateAI before its state switch, GameObject.cpp:338-340). One instance serves every
/// object of its entry, so a script keeps per-object state by the object. World thread.
/// </summary>
public interface IGameObjectAi
{
    /// <summary>vmangos GameObjectAI::OnUse for a trap's target: true when the script handled it and the trap must not cast (GameObject.cpp:536).</summary>
    bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target);

    /// <summary>vmangos GameObjectAI::UpdateAI.</summary>
    void Update(GameObjectMapSystem objects, GameObject go, uint diffMs);

    /// <summary>
    /// vmangos GameObjectAI::OnUse at the start of GameObject::Use (GameObject.cpp:1405-1407): true when the script handled the use and the
    /// object does nothing more. Not asked for a trap's target (that is <see cref="OnTrapTarget"/>).
    /// </summary>
    bool OnUse(GameObjectMapSystem objects, GameObject go, Unit user) => false;

    /// <summary>vmangos GameObjectAI::OnActivateBySpell: true when the object script handled effect 86 before its generic action.</summary>
    bool OnActivateBySpell(GameObjectMapSystem objects, GameObject go, Unit caster, uint spellId, uint action) => false;

    /// <summary>
    /// The open-lock path (Spell::SendLoot after the lock check passed, SpellEffects.cpp): true when the script handled the opening and the
    /// object does nothing more. Defaults to false so existing scripts keep their behaviour on the spell path; a script whose plain click
    /// is refused (<see cref="OnUse"/> true) but whose validated opening acts, such as a Molten Core rune, overrides both.
    /// </summary>
    bool OnUnlockedUse(GameObjectMapSystem objects, GameObject go, Player user) => false;
}

/// <summary>The object scripts by entry and the little a script may do to its object.</summary>
public sealed partial class GameObjectMapSystem
{
    private readonly Dictionary<uint, IGameObjectAi> _ais = [];

    /// <summary>Run <paramref name="ai"/> for every object of <paramref name="entry"/> in this map (a later registration replaces the earlier one).</summary>
    public void RegisterAi(uint entry, IGameObjectAi ai) => _ais[entry] = ai ?? throw new ArgumentNullException(nameof(ai));

    /// <summary>Stop running the script of <paramref name="entry"/>.</summary>
    public void UnregisterAi(uint entry) => _ais.Remove(entry);

    private IGameObjectAi? AiOf(GameObject go) => _ais.Count == 0 ? null : _ais.GetValueOrDefault(go.Entry);

    private void UpdateAis(uint diffMs)
    {
        if (_ais.Count == 0)
        {
            return;
        }

        foreach (GameObject go in _objects.Values.ToArray())
        {
            if (_ais.TryGetValue(go.Entry, out IGameObjectAi? ai))
            {
                ai.Update(this, go, diffMs);
            }
        }
    }

    /// <summary>
    /// vmangos GameObject::Despawn for a script: a spawned database object goes now and comes back after its respawn time (the loot-state
    /// path the use of an object takes); false when it is not spawned here.
    /// </summary>
    public bool DespawnForRespawn(GameObject go)
    {
        ArgumentNullException.ThrowIfNull(go);
        if (!go.IsSpawned || !Tracks(go) || go.Spawn is null)
        {
            return false;
        }

        go.LootState = GameObjectLootState.JustDeactivated;
        return true;
    }

    /// <summary>vmangos GameObject::SetRespawnTime(seconds) on a despawned object: it comes back <paramref name="seconds"/> from now.</summary>
    public void SetRespawnIn(GameObject go, uint seconds)
    {
        ArgumentNullException.ThrowIfNull(go);
        if (!go.IsSpawned && Tracks(go))
        {
            go.RespawnAtMs = _clockMs + (seconds * 1000L);
        }
    }

    /// <summary>The respawn time of an object's spawn row, in seconds (vmangos GameObjectData::GetRandomRespawnTime; 0 without a row).</summary>
    public static uint SpawnRespawnSeconds(GameObject go) => go.Spawn is { SpawnTimeSeconds: > 0 } spawn ? (uint)spawn.SpawnTimeSeconds : 0;
}

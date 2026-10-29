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

    /// <summary>
    /// Whether an environmental trap may pick <paramref name="candidate"/> as its target: the ScriptDevAI <c>pTrapSearching</c> functor that
    /// narrows the trap's target search (mangos-classic GameObject.cpp:466-473; go_transpolyporter_bb accepts only a player holding a
    /// Goblin Transponder, go_scripts.cpp:261-276). True by default, so a trap without a script takes the nearest living player.
    /// </summary>
    bool AcceptsTrapTarget(GameObjectMapSystem objects, GameObject go, Unit candidate) => true;

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

    /// <summary>
    /// The same object instance came back after a despawn (GameObject::Update respawn path, which calls AI()->Reset() "required for example in
    /// SmartAI to clear one time events": AzerothCore GameObject.cpp:663-665). Defaults to nothing, so existing scripts keep their behaviour.
    /// </summary>
    void OnRespawn(GameObjectMapSystem objects, GameObject go)
    {
    }

    /// <summary>
    /// cmangos GameObjectAI::OnLootStateChange(user) for GO_JUST_DEACTIVATED on the loot release path: the last viewer released the
    /// object's loot and it was looted out (DoLootRelease → SetLootState(GO_JUST_DEACTIVATED, player)). <paramref name="looter"/> is the
    /// releasing player when known. Defaults to nothing.
    /// </summary>
    void OnLootedOut(GameObjectMapSystem objects, GameObject go, Player? looter)
    {
    }
}

/// <summary>
/// An object script that is not registered by entry but decides per object whether it runs (the smart-script AI: an object runs it when
/// <c>smart_scripts</c> has rows for it). A script registered with <see cref="GameObjectMapSystem.RegisterAi"/> always wins over it.
/// </summary>
public interface IGameObjectFallbackAi : IGameObjectAi
{
    /// <summary>Whether this script runs <paramref name="go"/>.</summary>
    bool Handles(GameObject go);

    /// <summary>Whether this script can run any object at all (false lets the map skip walking its objects every update).</summary>
    bool HandlesAny { get; }

    /// <summary>
    /// What <see cref="Handles"/> answers from: while it stays the same instance an object's answer stays the same, so the map remembers which
    /// of its objects run a script instead of asking for every object every update. Null (the default) means ask every time.
    /// </summary>
    object? HandlesKey => null;
}

/// <summary>The object scripts by entry and the little a script may do to its object.</summary>
public sealed partial class GameObjectMapSystem
{
    private readonly Dictionary<uint, IGameObjectAi> _ais = [];

    /// <summary>Run <paramref name="ai"/> for every object of <paramref name="entry"/> in this map (a later registration replaces the earlier one).</summary>
    public void RegisterAi(uint entry, IGameObjectAi ai)
    {
        _ais[entry] = ai ?? throw new ArgumentNullException(nameof(ai));
        _aiObjectsStale = true;
    }

    /// <summary>Stop running the script of <paramref name="entry"/>.</summary>
    public void UnregisterAi(uint entry)
    {
        _ais.Remove(entry);
        _aiObjectsStale = true;
    }

    /// <summary>The script that runs an object whose entry has none registered (see <see cref="IGameObjectFallbackAi"/>); null for none.</summary>
    public IGameObjectFallbackAi? FallbackAi
    {
        get => _fallbackAi;
        set
        {
            _fallbackAi = value;
            _aiObjectsStale = true;
        }
    }

    private IGameObjectFallbackAi? _fallbackAi;

    /// <summary>
    /// The objects that run a script, in <c>_objects</c> order: an object's script follows from its entry and spawn (both fixed), the registered
    /// scripts and the fallback's <see cref="IGameObjectFallbackAi.HandlesKey"/>, so the list is rebuilt only when one of those or the object set
    /// changes. Asking every object of a map every update was 10% of map work on live (thousands of objects, a handful scripted).
    /// </summary>
    private readonly List<GameObject> _aiObjects = [];

    private bool _aiObjectsStale = true;
    private object? _aiObjectsKey;

    /// <summary>The object set changed: the scripted objects are found again on the next update.</summary>
    private void ObjectsChanged()
    {
        _aiObjectsStale = true;
        _walkListStale = true;
        _transportsStale = true;
    }

    private IGameObjectAi? AiOf(GameObject go)
        => _ais.GetValueOrDefault(go.Entry) ?? (_fallbackAi?.Handles(go) == true ? _fallbackAi : null);

    /// <summary>The script that runs <paramref name="go"/>: its registered one, else the fallback when it handles the object, else null.</summary>
    public IGameObjectAi? AiFor(GameObject go)
    {
        ArgumentNullException.ThrowIfNull(go);
        return AiOf(go);
    }

    private void UpdateAis(uint diffMs)
    {
        if (_ais.Count == 0 && FallbackAi?.HandlesAny != true)
        {
            return;
        }

        object? key = _fallbackAi?.HandlesKey;
        if (_aiObjectsStale || !ReferenceEquals(key, _aiObjectsKey) || (key is null && _fallbackAi is not null))
        {
            _aiObjects.Clear();
            foreach (GameObject go in _objects.Values)
            {
                if (AiOf(go) is not null) _aiObjects.Add(go);
            }

            _aiObjectsKey = key;
            _aiObjectsStale = false;
        }

        if (_aiObjects.Count == 0)
        {
            return;
        }

        // A snapshot (an AI may spawn or despawn objects), into a reused buffer: a fresh array per map per tick was 20 MB per 30 s on live.
        List<GameObject> snapshot = _aiUpdateBuffer is { Count: 0 } idle ? idle : [];
        _aiUpdateBuffer = null;
        snapshot.AddRange(_aiObjects);
        try
        {
            foreach (GameObject go in snapshot)
            {
                AiOf(go)?.Update(this, go, diffMs);
            }
        }
        finally
        {
            snapshot.Clear();
            _aiUpdateBuffer = snapshot;
        }
    }

    private List<GameObject>? _aiUpdateBuffer = [];

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
            SaveRespawnOnDespawn(go);
        }
    }

    /// <summary>The respawn time of an object's spawn row, in seconds (vmangos GameObjectData::GetRandomRespawnTime; 0 without a row).</summary>
    public static uint SpawnRespawnSeconds(GameObject go) => go.Spawn is { SpawnTimeSeconds: > 0 } spawn ? (uint)spawn.SpawnTimeSeconds : 0;
}

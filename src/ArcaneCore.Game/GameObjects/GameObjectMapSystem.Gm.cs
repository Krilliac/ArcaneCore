using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.GameObjects;

/// <summary>
/// The live-object operations the GM commands need (docs/integration/gm-objects-npc-lane.md): activate, move or turn a runtime
/// object and read respawn state. None of them writes a spawn row; ArcaneCore has no spawn write path.
/// </summary>
public sealed partial class GameObjectMapSystem
{
    /// <summary>The number of respawn times kept for spawns whose grid unloaded while they were despawned.</summary>
    public int DormantRespawnCount => _respawnAt.Count;

    /// <summary>
    /// vmangos <c>.gobject activate</c> (GameObject::SetLootState(GO_READY) then UseDoorOrButton): the object becomes ready, its state
    /// flips (ready to active and back) with the in-use flag, and it returns after the template's auto-close time. False when the object is
    /// not tracked here or is despawned.
    /// </summary>
    public bool Activate(GameObject go)
    {
        ArgumentNullException.ThrowIfNull(go);
        if (!Tracks(go) || !go.IsSpawned)
        {
            return false;
        }

        go.LootState = GameObjectLootState.Ready;
        return ActivateDoorOrButton(go, go.Template.AutoCloseSeconds()) == GameObjectUseResult.Ok;
    }

    /// <summary>
    /// Move and turn a runtime object (no database spawn: its row would still say the old place). Clients that saw it get it destroyed and
    /// created again at the new place, which is how a static object is relocated without a movement packet. False when the object is not a
    /// tracked, spawned runtime object.
    /// </summary>
    public bool Relocate(GameObject go, float x, float y, float z, float orientation)
    {
        ArgumentNullException.ThrowIfNull(go);
        if (!Tracks(go) || go.Spawn is not null || !go.IsSpawned)
        {
            return false;
        }

        Map.RemoveObject(go);
        foreach (List<GameObject> grid in _grids.Values)
        {
            grid.Remove(go);
        }

        go.SetPosition(x, y, z, orientation);
        go.SetFloat(UpdateFields.GameobjectPosX, x);
        go.SetFloat(UpdateFields.GameobjectPosY, y);
        go.SetFloat(UpdateFields.GameobjectPosZ, z);
        go.SetFloat(UpdateFields.GameobjectFacing, orientation);
        (float rx, float ry, float rz, float rw) = GameObject.ComputeRotation(orientation, 0, 0, 0, 0);
        go.SetFloat(UpdateFields.GameobjectRotation, rx);
        go.SetFloat(UpdateFields.GameobjectRotation + 1, ry);
        go.SetFloat(UpdateFields.GameobjectRotation + 2, rz);
        go.SetFloat(UpdateFields.GameobjectRotation + 3, rw);
        if (_grids.TryGetValue(CreatureMapSystem.ComputeGrid(x, y), out List<GameObject>? list))
        {
            list.Add(go);
        }

        go.ClearChangedFields();
        Map.AddObject(go);
        return true;
    }

    /// <summary>Milliseconds until a despawned database spawn comes back; null when it is spawned or waits for a script or event (no timer).</summary>
    public long? RespawnRemainingMs(GameObject go)
    {
        ArgumentNullException.ThrowIfNull(go);
        return go.IsSpawned || go.RespawnAtMs <= 0 ? null : Math.Max(0L, go.RespawnAtMs - _clockMs);
    }

    /// <summary>
    /// Bring a despawned database spawn back now when it is waiting on a respawn timer. A chest whose state is kept durably (instances)
    /// is left alone: its stored consumed row would disagree with the live object. False when nothing was respawned.
    /// </summary>
    public bool RespawnPending(GameObject go)
    {
        ArgumentNullException.ThrowIfNull(go);
        if (!Tracks(go) || go.Spawn is null || go.IsSpawned || go.RespawnAtMs <= 0 || DurableKeyOf(go) is not null)
        {
            return false;
        }

        Respawn(go);
        return true;
    }
}

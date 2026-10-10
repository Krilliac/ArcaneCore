using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.Game.Instances.Scripts.BlackwingLair;

/// <summary>
/// mangos-classic blackwing_lair/blackwing_lair.cpp go_ai_suppression (Suppression Room traps, 179784).
/// OnLootStateChange: once a device is used up or disarmed it rearms 30 s to 2 min later while Broodlord Lashlayer lives, and
/// not again for the instance lifetime (7 days) after his death. UpdateAI: every 5 s (first after a random 0-5 s) a ready device
/// plays its fume custom animation. Limit: the reference also casts the trap spell with no unit target there, which the
/// <see cref="IGameObjectSpells"/> seam cannot express (the reference notes it needs core GO-casting support too); the
/// proximity trigger of the trap template still casts at players.
/// </summary>
internal sealed class SuppressionDeviceAI(BlackwingLairInstance instance) : IGameObjectAi
{
    public const uint Entry = 179784;
    public const uint FumeIntervalMs = 5000;
    public const uint RearmMinSeconds = 30;
    public const uint RearmMaxSeconds = 2 * 60;
    public const uint DeadRearmSeconds = 7 * 24 * 3600;

    private sealed class State
    {
        public uint FumeMs;
        public bool Spawned;
    }

    private readonly Dictionary<ObjectGuid, State> _states = [];

    public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;

    public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
    {
        if (!_states.TryGetValue(go.Guid, out State? state))
        {
            state = new State { FumeMs = (uint)objects.Random.Next(0, (int)FumeIntervalMs + 1), Spawned = go.IsSpawned };
            _states[go.Guid] = state;
        }

        if (state.Spawned && !go.IsSpawned)
        {
            // GO_ACTIVATED -> despawn: SetRespawnTime replaces the spawn row's delay.
            objects.SetRespawnIn(go, instance.GetData(2) == EncounterState.Done
                ? DeadRearmSeconds
                : (uint)objects.Random.Next((int)RearmMinSeconds, (int)RearmMaxSeconds + 1));
        }

        state.Spawned = go.IsSpawned;
        if (!go.IsSpawned)
        {
            return;
        }

        if (state.FumeMs > diffMs)
        {
            state.FumeMs -= diffMs;
            return;
        }

        if (go.LootState == GameObjectLootState.Ready)
        {
            objects.SendCustomAnim(go, 0);
        }

        state.FumeMs = FumeIntervalMs;
    }
}

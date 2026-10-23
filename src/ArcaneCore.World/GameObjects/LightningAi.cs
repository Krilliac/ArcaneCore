using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.World.GameObjects;

/// <summary>
/// go_lightning (mangos-classic kalimdor/azshara.cpp:368-397, registered :518-521): the Theatric Lightning (183356) of Azshara plays its custom
/// animation (SMSG_GAMEOBJECT_CUSTOM_ANIM, animation 0) every two to five whole seconds for as long as it is spawned. The timer starts at a
/// random 2-5 s when the object is first updated (the source's constructor) and is held, not counted down, while the object is not spawned.
/// One instance serves every object of the entry: the timer is kept by the object. World thread.
/// </summary>
internal sealed class LightningAi(Random random) : IGameObjectAi
{
    public const uint MinSeconds = 2, MaxSeconds = 5;

    private readonly ConditionalWeakTable<GameObject, Timer> _timers = new();

    private sealed class Timer
    {
        public uint RemainingMs;
    }

    /// <summary>The time left before <paramref name="go"/>'s next flash, or null before its first update.</summary>
    public uint? RemainingMs(GameObject go) => _timers.TryGetValue(go, out Timer? timer) ? timer.RemainingMs : null;

    private uint NextMs() => (uint)random.Next((int)MinSeconds, (int)MaxSeconds + 1) * 1000;

    public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;

    public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
    {
        Timer timer = _timers.GetValue(go, _ => new Timer { RemainingMs = NextMs() });
        if (!go.IsSpawned)
        {
            return;
        }

        if (timer.RemainingMs <= diffMs)
        {
            objects.SendCustomAnim(go, 0);
            timer.RemainingMs = NextMs();
        }
        else
        {
            timer.RemainingMs -= diffMs;
        }
    }
}

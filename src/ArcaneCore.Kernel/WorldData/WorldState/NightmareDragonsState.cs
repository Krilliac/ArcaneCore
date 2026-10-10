namespace ArcaneCore.Kernel.WorldData.WorldState;

/// <summary>
/// The vmangos Dragons of Nightmare saved variables (ObjectMgr.h VAR_REQ_UPDATE 30001, VAR_RESP_TIME 30002, VAR_PERM_1..4 30004-30007) plus
/// what vmangos keeps elsewhere: whether event 66 is active (game_event_status) and which spawn slots are dead (their creature respawn times).
/// <paramref name="Permutation"/> holds the dragon entry of each slot (0: the slot's own dragon).
/// </summary>
public readonly record struct NightmareDragonsState(uint[] Permutation, long RespawnUnix, uint RequiredUpdates, bool Active, byte KilledMask)
{
    /// <summary>vmangos DEF_STOP_DELAY: updates the event waits, after the last dragon died, before it stops.</summary>
    public const uint DefaultStopDelay = 20;

    /// <summary>Nothing saved: no permutation, respawn time 0 (the event starts at the first update), the default stop delay.</summary>
    public static NightmareDragonsState Initial => new([0, 0, 0, 0], 0, DefaultStopDelay, false, 0);
}

/// <summary>The saved Dragons of Nightmare state (characters database).</summary>
public interface INightmareDragonsStore
{
    Task<NightmareDragonsState?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(NightmareDragonsState state, CancellationToken cancellationToken = default);
}

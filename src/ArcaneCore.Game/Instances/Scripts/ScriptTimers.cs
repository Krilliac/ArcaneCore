namespace ArcaneCore.Game.Instances.Scripts;

/// <summary>Countdown helpers shared by the scripted AIs. All three count a millisecond timer down by one update and
/// report whether it ran out; they differ in how a timer at 0 and a firing timer are treated.</summary>
public static class ScriptTimers
{
    /// <summary>Fires when the countdown reaches 0 (timer &lt;= diff) and leaves the timer at 0, so the caller re-arms it
    /// after acting. A timer already at 0 fires on every call.</summary>
    public static bool Due(ref uint timer, uint diffMs)
    {
        if (timer > diffMs) { timer -= diffMs; return false; }
        timer = 0;
        return true;
    }

    /// <summary>Same countdown as <see cref="Due"/> for world-boss abilities that retry on every update after a failed cast:
    /// the timer stays at 0 until the caller re-arms it.</summary>
    public static bool DueRetry(ref uint timer, uint diffMs) => Due(ref timer, diffMs);

    /// <summary>A disabled action (0) never fires; an armed one fires when its countdown reaches 0 and then stays latched at 1
    /// until the cast succeeds and the caller sets the next delay.</summary>
    public static bool DueLatched(ref uint timer, uint diffMs)
    {
        if (timer == 0) return false;
        timer = timer > diffMs ? timer - diffMs : 0;
        if (timer != 0) return false;
        timer = 1;
        return true;
    }
}

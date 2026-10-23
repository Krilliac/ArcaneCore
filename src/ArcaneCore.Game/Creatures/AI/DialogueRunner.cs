namespace ArcaneCore.Game.Creatures;

/// <summary>
/// The timer half of ScriptDev2's <c>DialogueHelper</c> (cmangos <c>DialogueUpdate</c>), shared by the quest scripts that play a table of
/// timed steps. Steps are positional: a step is its index in the script's table, and <see cref="Step"/> is -1 while idle. The runner owns
/// only the position and the countdown; the script owns the table (its text ids, speakers and per-step side effects) and runs a step
/// itself after <see cref="Start"/> or when <see cref="Advance"/> returns its index, so the speaker resolution and the
/// <c>System</c> guard stay in the script's own step method. State is committed before the step runs. A delay of 0 ends the run after
/// that step. Per-creature, driven from the AI's update thread; not thread-safe.
/// </summary>
internal sealed class DialogueRunner(uint[] delaysMs)
{
    private uint _stepMs;

    /// <summary>The current table index, or -1 when no dialogue is running.</summary>
    public int Step { get; private set; } = -1;

    /// <summary>
    /// Makes <paramref name="index"/> the current step and loads its delay. Throws <see cref="IndexOutOfRangeException"/> for an index
    /// outside the table (a lookup that found nothing is -1), as the per-script copies did.
    /// </summary>
    public void Start(int index)
    {
        Step = index;
        _stepMs = delaysMs[index];
    }

    /// <summary>
    /// Counts the current step's delay down by <paramref name="diffMs"/>. Returns the index of the next step to run (already made current
    /// as by <see cref="Start"/>), or -1 when nothing is due: idle, still waiting, or the run just ended (delay 0 or last step).
    /// </summary>
    public int Advance(uint diffMs)
    {
        if (Step < 0)
        {
            return -1;
        }

        if (_stepMs == 0)
        {
            Step = -1;
        }
        else if (_stepMs <= diffMs)
        {
            if (Step + 1 < delaysMs.Length)
            {
                Start(Step + 1);
                return Step;
            }

            Step = -1;
        }
        else
        {
            _stepMs -= diffMs;
        }

        return -1;
    }
}

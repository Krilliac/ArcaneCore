namespace ArcaneCore.Game.Reload;

/// <summary>
/// The undo log of one swap on the world thread. A candidate applies its change in
/// <see cref="Step"/>s, each paired with the action that undoes it; if a later step (or anything
/// after) throws, <see cref="Rollback"/> runs the undo actions of the steps that did apply, newest
/// first, so a reload is all-or-nothing.
/// </summary>
public sealed class ReloadTransaction
{
    private readonly List<(string Description, Action Undo)> _undo = [];
    private readonly List<string> _notes = [];

    /// <summary>Descriptions of the steps applied so far, oldest first.</summary>
    public IReadOnlyList<string> AppliedSteps => [.. _undo.Select(u => u.Description)];

    /// <summary>Lines the swap wants shown to the invoker (e.g. options that cannot change at reload).</summary>
    public IReadOnlyList<string> Notes => _notes;

    /// <summary>Apply one step. If <paramref name="apply"/> throws the step is not recorded (it must undo its own partial effect) and the exception propagates.</summary>
    public void Step(string description, Action apply, Action undo)
    {
        ArgumentException.ThrowIfNullOrEmpty(description);
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(undo);
        apply();
        _undo.Add((description, undo));
    }

    /// <summary>Add a line for the invoker.</summary>
    public void Note(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        _notes.Add(text);
    }

    /// <summary>Undo every applied step, newest first. An undo that throws does not stop the others; its exception is returned.</summary>
    public IReadOnlyList<Exception> Rollback()
    {
        var failures = new List<Exception>();
        for (int i = _undo.Count - 1; i >= 0; i--)
        {
            try
            {
                _undo[i].Undo();
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        _undo.Clear();
        return failures;
    }
}

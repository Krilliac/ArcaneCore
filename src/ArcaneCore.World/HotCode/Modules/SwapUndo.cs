using ArcaneCore.Kernel.Diagnostics;
namespace ArcaneCore.World.HotCode.Modules;

/// <summary>The slots one module swap commits, in commit order (the names <see cref="ModuleHost"/>'s fault injection and the undo log use).</summary>
internal static class SwapSlots
{
    public const string Opcodes = "opcodes";
    public const string CreatureAi = "creature-ai";
    public const string ScriptHooks = "script-hooks";
    public const string SpellScripts = "spell-scripts";
    public const string InstanceScripts = "instance-scripts";
    public const string EventAi = "eventai";
    public const string SpellHandlers = "spell-handlers";
    public const string Commands = "commands";

    public static IReadOnlyList<string> InCommitOrder { get; } = [Opcodes, CreatureAi, ScriptHooks, SpellScripts, InstanceScripts, EventAi, SpellHandlers, Commands];
}

/// <summary>
/// The undo steps of one module swap (the content lane's <c>ReloadTransaction</c> pattern): every slot that changed records how to put its previous
/// state back, and a failure later in the same commit runs the steps in reverse order. World thread only, one commit at a time.
/// </summary>
internal sealed class SwapUndo
{
    private readonly List<(string Slot, Action Undo)> _steps = [];

    /// <summary>The slots that changed so far, in commit order.</summary>
    public IReadOnlyList<string> Slots => [.. _steps.Select(s => s.Slot)];

    public void Add(string slot, Action undo)
    {
        ArgumentException.ThrowIfNullOrEmpty(slot);
        ArgumentNullException.ThrowIfNull(undo);
        _steps.Add((slot, undo));
    }

    /// <summary>
    /// Run every undo step, last first. Every step runs even when an earlier one throws; null when all of them worked, otherwise which slots could
    /// not be put back (the live state is then mixed, and the caller must say so).
    /// </summary>
    public string? RollBack(Action<string, Exception> onStepFailed)
    {
        ArgumentNullException.ThrowIfNull(onStepFailed);
        List<string>? failed = null;
        for (int i = _steps.Count - 1; i >= 0; i--)
        {
            (string slot, Action undo) = _steps[i];
            try
            {
                undo();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("hotcode.undo", ex);
                AnomalyLedger.Shared.Report("hotcode", "undo-failed", slot, $"error={ex.GetType().Name}: {ex.Message}", AnomalySeverity.Error);
                onStepFailed(slot, ex);
                (failed ??= []).Add($"{slot} ({ex.GetType().Name}: {ex.Message})");
            }
        }

        _steps.Clear();
        return failed is null ? null : string.Join(", ", failed);
    }
}

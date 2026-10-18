namespace ArcaneCore.Game.Creatures;

/// <summary>Queued creature work of one map as <c>.debug scheduler</c> shows it (docs/reference/gm-commands.md).</summary>
public readonly record struct CreaturePendingWork(int AiRelocations, int Assists, int RelayScriptSteps, int DbScriptSteps, int ArrivalRelays, int ScriptDespawns);

public sealed partial class CreatureMapSystem
{
    /// <summary>Sizes of this map's creature work queues (read-only; world thread or under the map's serial gate).</summary>
    public CreaturePendingWork PendingWork => new(
        _relocation?.PendingCount ?? 0,
        _pendingAssists.Count,
        _relays.PendingCount,
        _dbScriptRunners.Values.Sum(r => r.PendingCount),
        _arrivalRelays.Count,
        _scriptDespawns.Count + _scriptObjectDespawns.Count);
}

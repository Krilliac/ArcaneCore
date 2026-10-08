using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>Coverage of loaded EventAI type IDs against the executable handler registry.</summary>
public sealed record EventAiCoverageReport(
    int Rows,
    int CreaturesWithScripts,
    int CreaturesFullySupported,
    IReadOnlyDictionary<byte, int> UsedUnsupportedEventIds,
    IReadOnlyDictionary<byte, int> UsedUnsupportedActionIds,
    IReadOnlyList<byte> UnusedUnsupportedEventIds,
    IReadOnlyList<byte> UnusedUnsupportedActionIds,
    int RowsWithUnsupportedParameters);

/// <summary>
/// Reports used unsupported IDs separately from reference IDs absent from loaded content.
/// The reference range is mangos-classic CreatureEventAI.h EventAI_Type and EventAI_ActionType;
/// action IDs 6-8 are marked UNUSED, and 49 is a REUSE marker, so they are not executable types.
/// </summary>
public static class EventAiCoverage
{
    private static readonly byte[] ReferenceEvents = [.. Enumerable.Range(0, 43).Select(i => (byte)i)];
    private static readonly byte[] ReferenceActions = [.. Enumerable.Range(0, 66)
        .Where(i => i is not (6 or 7 or 8 or 49)).Select(i => (byte)i)];

    public static EventAiCoverageReport Analyze(IEnumerable<CreatureAiEvent> rows, EventAiRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        registry ??= EventAiRegistry.Default;
        var eventCounts = new SortedDictionary<byte, int>();
        var actionCounts = new SortedDictionary<byte, int>();
        var byCreature = new Dictionary<(uint Entry, uint Guid), bool>();
        int rowCount = 0;
        int unsupportedParameters = 0;
        foreach (CreatureAiEvent row in rows)
        {
            rowCount++;
            bool supported = true;
            EventAiEventHandler? handler = registry.FindEvent(row.EventType);
            if (handler is null)
            {
                Add(eventCounts, row.EventType);
                supported = false;
            }
            else if (handler.UnsupportedReason(row) is not null)
            {
                unsupportedParameters++;
                supported = false;
            }

            foreach (CreatureAiAction action in row.Actions)
            {
                if (action.IsEmpty || registry.FindAction(action.Type) is not null) continue;
                Add(actionCounts, action.Type);
                supported = false;
            }

            var key = (row.CreatureId, row.CreatureGuid);
            byCreature[key] = !byCreature.TryGetValue(key, out bool previous) ? supported : previous && supported;
        }

        return new EventAiCoverageReport(
            rowCount, byCreature.Count, byCreature.Values.Count(value => value),
            eventCounts, actionCounts,
            [.. ReferenceEvents.Where(id => registry.FindEvent(id) is null && !eventCounts.ContainsKey(id))],
            [.. ReferenceActions.Where(id => id != 0 && registry.FindAction(id) is null && !actionCounts.ContainsKey(id))],
            unsupportedParameters);
    }

    private static void Add(IDictionary<byte, int> counts, byte id)
        => counts[id] = counts.TryGetValue(id, out int count) ? count + 1 : 1;
}

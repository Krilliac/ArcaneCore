using System.Runtime.CompilerServices;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.World.Gm.ServerConfig;

/// <summary>
/// The overrides made through <c>.server config</c> (and the alias commands that route into it) for one world: for each changed key the
/// value it had before the first change (the baseline <c>reset</c> puts back), the value set, who set it and why. A key that tracks its
/// own override (the rates) is not stored here. <c>.reload config</c> drops the entries of the keys it puts back (<see cref="EndsAtReload"/>); an
/// entry whose key no longer holds the value that was set (another path changed it) is stale: reads ignore it and the next change of
/// that key drops it. Thread affinity: world thread, no locking. Set and reset are posted (<c>World.Post</c>, serial at tick
/// start, so they stay safe even if the Packets stage is ever run in parallel); a read never writes.
/// </summary>
public sealed class OptionOverrides
{
    private static readonly ConditionalWeakTable<WorldRuntime, OptionOverrides> s_perWorld = new();

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    internal sealed record Entry(object Baseline, object Value, string Actor, string? Reason);

    /// <summary>The overrides of <paramref name="world"/>, created on first use.</summary>
    public static OptionOverrides For(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return s_perWorld.GetValue(world, static _ => new OptionOverrides());
    }

    /// <summary>How many keys are overridden (a stale entry is counted until its key is next changed).</summary>
    public int Count => _entries.Count;

    internal Entry? Find(string path) => _entries.GetValueOrDefault(path);

    internal IEnumerable<string> Paths => _entries.Keys.ToArray();

    internal void Remember(string path, object before, object after, string actor, string? reason)
    {
        object baseline = _entries.TryGetValue(path, out Entry? existing) ? existing.Baseline : before;
        if (Equals(baseline, after))
        {
            _entries.Remove(path);
            return;
        }

        _entries[path] = new Entry(baseline, after, actor, reason);
    }

    internal bool Forget(string path) => _entries.Remove(path);

    private static readonly Lazy<HashSet<string>> s_reloadKeys = new(() => new HashSet<string>(Reload.WorldConfigKeys.All.Select(k => k.Path), StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// True when <c>.reload config</c> puts the file's value back into the key at <paramref name="path"/> (the reload keys and the AntiCheat
    /// section), so an override of it ends at the reload. The PacketHeuristics keys and the pack keys are not part of the reload and keep
    /// theirs until restart.
    /// </summary>
    public static bool EndsAtReload(string path)
        => s_reloadKeys.Value.Contains(path) || path.StartsWith(Game.AntiCheat.AntiCheatOptions.SectionName + ":", StringComparison.OrdinalIgnoreCase);

    /// <summary>Drops the entries of the keys a reload puts back (<see cref="EndsAtReload"/>); returns them so a rolled back reload can restore them.</summary>
    internal IReadOnlyList<KeyValuePair<string, Entry>> ForgetReloaded()
    {
        KeyValuePair<string, Entry>[] reloaded = [.. _entries.Where(e => EndsAtReload(e.Key))];
        foreach (KeyValuePair<string, Entry> entry in reloaded)
        {
            _entries.Remove(entry.Key);
        }

        return reloaded;
    }

    internal void Restore(IEnumerable<KeyValuePair<string, Entry>> entries)
    {
        foreach (KeyValuePair<string, Entry> entry in entries)
        {
            _entries[entry.Key] = entry.Value;
        }
    }
}

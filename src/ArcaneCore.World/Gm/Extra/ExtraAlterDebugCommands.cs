using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Gm.Inspect;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Extra;

/// <summary>
/// <c>.alter auraduration #spell #seconds</c> and <c>.alter aurastacks #spell #stacks</c>: edit a live aura holder of the selected unit
/// (or you) through the spell system, so the client's aura timer and the stacked amounts follow. No vmangos equivalent.
/// </summary>
public sealed class ExtraAlterCommands : ICommandExtension
{
    /// <summary>Longest duration <c>.alter auraduration</c> sets: one day.</summary>
    public const int MaxDurationSeconds = 86_400;

    public string Path => "alter";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new("auraduration", AccountSecurity.GameMaster,
            "Syntax: .alter auraduration #spellid #seconds\nSet the remaining time of the selected unit's (or your) aura of #spellid (a spell link works) to #seconds (0 lets it expire now, at most 86400). Permanent auras are not changed.",
            AuraDuration, RetailLevel: 3),
        new("aurastacks", AccountSecurity.GameMaster,
            "Syntax: .alter aurastacks #spellid #stacks\nSet the stack count of the selected unit's (or your) aura of #spellid to #stacks (1 up to the spell's limit); the effect amounts scale with it and an increase refreshes the duration.",
            AuraStacks, RetailLevel: 3),
    ];

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    private static bool TryHolder(CommandContext context, CommandArgs args, out SpellSystem spells, out SpellAuraHolder? holder, out Unit? unit)
    {
        holder = null;
        unit = null;
        spells = null!;
        if (!args.ExtractUInt32KeyFromLink("Hspell", out uint spellId) || spellId == 0)
        {
            return false;
        }

        if (context.Session.Services.GetService<SpellFeature>()?.System is not { } system)
        {
            context.Reply("The spell system is not running.");
            return true;
        }

        spells = system;
        if ((unit = InspectCommands.Target(context)) is null)
        {
            return true;
        }

        holder = system.GetAuras(unit).FirstOrDefault(h => h.Spell.Id == spellId && !h.IsRemoved);
        if (holder is null)
        {
            context.Reply(string.Create(C, $"{InspectCommands.Describe(unit)} has no aura of spell {spellId}."));
        }

        return true;
    }

    private static bool AuraDuration(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!TryHolder(context, args, out SpellSystem spells, out SpellAuraHolder? holder, out Unit? unit))
        {
            return false;
        }

        if (holder is null)
        {
            return true;
        }

        if (!uint.TryParse(args.ExtractArg(), NumberStyles.None, C, out uint seconds) || !args.IsEmpty || seconds > MaxDurationSeconds)
        {
            return false;
        }

        if (!spells.GmSetAuraDuration(holder, (int)seconds * 1000))
        {
            context.Reply(string.Create(C, $"Aura {holder.Spell.Id} [{holder.Spell.Name}] is permanent; its duration was not changed."));
            return true;
        }

        context.Reply(string.Create(C,
            $"Aura {holder.Spell.Id} [{holder.Spell.Name}] on {InspectCommands.Name(unit!)}: {ExtraInspectCommands.Time(holder.Duration)} left of {ExtraInspectCommands.Time(holder.MaxDuration)}."));
        return true;
    }

    private static bool AuraStacks(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!TryHolder(context, args, out SpellSystem spells, out SpellAuraHolder? holder, out Unit? unit))
        {
            return false;
        }

        if (holder is null)
        {
            return true;
        }

        if (!int.TryParse(args.ExtractArg(), NumberStyles.None, C, out int stacks) || !args.IsEmpty || stacks < 1)
        {
            return false;
        }

        if (holder.Spell.StackAmount == 0)
        {
            context.Reply(string.Create(C, $"Aura {holder.Spell.Id} [{holder.Spell.Name}] does not stack."));
            return true;
        }

        int now = spells.GmSetAuraStacks(holder, stacks);
        context.Reply(string.Create(C,
            $"Aura {holder.Spell.Id} [{holder.Spell.Name}] on {InspectCommands.Name(unit!)}: {now} of {holder.Spell.StackAmount} stacks{(stacks > holder.Spell.StackAmount ? " (the spell's limit)" : string.Empty)}."));
        return true;
    }
}

/// <summary>
/// <c>.debug pathbench [#count] [#radius]</c> (time path queries from you to points around you on the world thread) and
/// <c>.debug gccollect</c> (administrator: force a full compacting collection and show what it freed). No vmangos equivalent.
/// </summary>
public sealed class ExtraDebugCommands : ICommandExtension
{
    /// <summary>Most path queries one <c>.debug pathbench</c> runs (they run on the world thread).</summary>
    public const int MaxPathQueries = 100;

    public string Path => "debug";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new("pathbench", AccountSecurity.GameMaster,
            "Syntax: .debug pathbench [#count] [#radius]\nTime #count path queries (default 20, at most 100) from you to evenly spread points #radius yards away (default 40, 5-200) on the world thread: min/p50/max/mean time, how many found a full, partial or no path, and the mean path length.",
            PathBench, RetailLevel: 3),
        new("gccollect", AccountSecurity.Administrator,
            "Syntax: .debug gccollect\nForce a full blocking, compacting garbage collection (including the large object heap) and show the heap before and after and how long it paused the server. Use sparingly on a live server.",
            GcCollect, RetailLevel: 6),
    ];

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    private static bool PathBench(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        int count = 20;
        float radius = 40f;
        if (args.ExtractArg() is { } first)
        {
            if (!int.TryParse(first, NumberStyles.None, C, out count)) return false;
            if (args.ExtractArg() is { } second && !float.TryParse(second, NumberStyles.Float, C, out radius)) return false;
        }

        if (!args.IsEmpty || !float.IsFinite(radius)) return false;
        count = Math.Clamp(count, 1, MaxPathQueries);
        radius = Math.Clamp(radius, 5f, 200f);
        Player me = context.Player;
        if (me.Map is not { } map)
        {
            context.Reply("You are not in a map.");
            return true;
        }

        var start = new Vector3(me.X, me.Y, me.Z);
        var times = new double[count];
        int full = 0, partial = 0, none = 0;
        double length = 0;
        var options = new PathOptions { Mover = PathMover.Player };
        long total = Stopwatch.GetTimestamp();
        for (int i = 0; i < count; i++)
        {
            float angle = MathF.Tau * i / count;
            var end = new Vector3(me.X + (MathF.Cos(angle) * radius), me.Y + (MathF.Sin(angle) * radius), me.Z);
            long t0 = Stopwatch.GetTimestamp();
            PathResult path = map.Collision.FindPath(start, end, options);
            times[i] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            if (!path.HasPath) none++;
            else if ((path.Type & PathType.Incomplete) != 0) partial++;
            else full++;
            if (path.HasPath) length += path.Length;
        }

        double elapsed = Stopwatch.GetElapsedTime(total).TotalMilliseconds;
        Array.Sort(times);
        int found = full + partial;
        context.Reply(string.Create(C,
            $"{count} path queries at {radius:F0} yd on map {map.MapId} in {elapsed:F1} ms: min {times[0]:F2}, p50 {times[count / 2]:F2}, max {times[^1]:F2}, mean {times.Average():F2} ms"));
        context.Reply(string.Create(C,
            $"  {full} full, {partial} partial, {none} no path; mean length {(found > 0 ? length / found : 0):F1} yd (straight line {radius:F0} yd)"));
        return true;
    }

    private static bool GcCollect(CommandContext context, string args)
    {
        if (args.Trim().Length != 0) return false;
        long before = GC.GetTotalMemory(false);
        long workingBefore = Environment.WorkingSet;
        int[] counts = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        long t0 = Stopwatch.GetTimestamp();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        double pause = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        long after = GC.GetTotalMemory(false);
        GCMemoryInfo info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        static string Mb(long bytes) => (bytes / 1048576.0).ToString("F1", C) + " MiB";
        context.Reply(string.Create(C,
            $"Full compacting collection took {pause:F1} ms: managed heap {Mb(before)} -> {Mb(after)} (freed {Mb(Math.Max(0, before - after))}), working set {Mb(workingBefore)} -> {Mb(Environment.WorkingSet)}"));
        context.Reply(string.Create(C,
            $"  collections since start: gen0 {GC.CollectionCount(0)} (+{GC.CollectionCount(0) - counts[0]}), gen1 {GC.CollectionCount(1)} (+{GC.CollectionCount(1) - counts[1]}), gen2 {GC.CollectionCount(2)} (+{GC.CollectionCount(2) - counts[2]}); fragmented {Mb(info.FragmentedBytes)}, committed {Mb(info.TotalCommittedBytes)}"));
        return true;
    }
}

using System.Globalization;
using System.Text;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.World.Ops.Diagnostics;

/// <summary>
/// The world daemon's contribution to a crash report: the tick number (counted from the existing
/// <see cref="WorldRuntime.WorldTick"/> hook, so it is the number of ticks that began), the ticks the
/// statistics ring completed, uptime, the world clock, the online count and whether the crashing thread
/// is the world thread. Everything read here is a volatile field or a lock-free property of
/// <see cref="WorldRuntime"/>, except the statistics snapshot, whose lock is held by its owner only to
/// copy the ring and never while calling out; nothing can wait on the world thread, so a crash with the
/// world thread wedged still reports.
/// <para>
/// Ownership: a singleton created by DI; <see cref="OnTick"/> runs on the world thread (the only
/// writer of the counter), <see cref="Describe"/> on whatever thread is crashing.
/// </para>
/// </summary>
public sealed class WorldCrashContext : ICrashContextProvider
{
    private readonly WorldRuntime _world;
    private long _ticksBegun;
    private uint _lastDiffMs;

    public WorldCrashContext(WorldRuntime world)
    {
        _world = world;
        world.WorldTick += OnTick;
    }

    public string Name => "world";

    /// <summary>Ticks that have begun since the world thread started.</summary>
    public long TickNumber => Volatile.Read(ref _ticksBegun);

    public void Describe(StringBuilder report)
    {
        report.Append("  tick number (begun): ").Append(TickNumber.ToString(CultureInfo.InvariantCulture)).Append('\n');
        report.Append("  last tick diff: ").Append(Volatile.Read(ref _lastDiffMs).ToString(CultureInfo.InvariantCulture)).Append(" ms\n");
        report.Append("  ticks completed (stats ring): ").Append(_world.Stats.Snapshot().TotalTicks.ToString(CultureInfo.InvariantCulture)).Append('\n');
        report.Append("  uptime: ").Append(_world.Uptime.ToString("c", CultureInfo.InvariantCulture)).Append('\n');
        report.Append("  world clock: ").Append(_world.NowMs.ToString(CultureInfo.InvariantCulture)).Append(" ms\n");
        report.Append("  online players: ").Append(_world.OnlinePlayerCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        report.Append("  crashing thread is the world thread: ").Append(_world.IsWorldThread ? "yes" : "no").Append('\n');
    }

    private void OnTick(uint diffMs)
    {
        Interlocked.Increment(ref _ticksBegun);
        Volatile.Write(ref _lastDiffMs, diffMs);
    }
}

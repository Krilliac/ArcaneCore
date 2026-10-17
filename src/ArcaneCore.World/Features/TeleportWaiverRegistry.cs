using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.WorldData;

namespace ArcaneCore.World.Features;

/// <summary>
/// The core's <see cref="ITeleportWaiverRegistry"/>. It is itself a (discovered, pinned) world feature and an
/// <see cref="IAreaTriggerWaiver"/>, so the teleport handler, which asks every feature that is a waiver, consults the
/// registered waivers with no change of its own. With nothing registered it waives nothing, so default behaviour is
/// unchanged. Registrations are a copy-on-write array: the world thread reads it without a lock.
/// </summary>
public sealed class TeleportWaiverRegistry : IWorldFeature, IAreaTriggerWaiver, ITeleportWaiverRegistry
{
    private readonly Lock _gate = new();
    private IAreaTriggerWaiver[] _waivers = [];

    /// <summary>The waivers registered now.</summary>
    public IReadOnlyList<IAreaTriggerWaiver> Waivers => Volatile.Read(ref _waivers);

    /// <summary>Nothing to attach: registrations come from the features that own the waivers.</summary>
    public void Attach(WorldRuntime world)
    {
    }

    public IDisposable Register(IAreaTriggerWaiver waiver)
    {
        ArgumentNullException.ThrowIfNull(waiver);
        if (ReferenceEquals(waiver, this))
        {
            throw new ArgumentException("the registry cannot register itself", nameof(waiver));
        }

        lock (_gate)
        {
            if (Array.IndexOf(_waivers, waiver) >= 0)
            {
                throw new InvalidOperationException("this waiver is already registered");
            }

            Volatile.Write(ref _waivers, [.. _waivers, waiver]);
        }

        return new Registration(this, waiver);
    }

    /// <summary>The union of what every registered waiver skips for <paramref name="player"/> on <paramref name="teleport"/> (world thread).</summary>
    public AreaTriggerWaivers Waives(Player player, AreaTriggerTeleport teleport)
    {
        AreaTriggerWaivers waived = AreaTriggerWaivers.None;
        foreach (IAreaTriggerWaiver waiver in Volatile.Read(ref _waivers))
        {
            waived |= waiver.Waives(player, teleport);
        }

        return waived;
    }

    private void Remove(IAreaTriggerWaiver waiver)
    {
        lock (_gate)
        {
            int index = Array.IndexOf(_waivers, waiver);
            if (index >= 0)
            {
                Volatile.Write(ref _waivers, [.. _waivers[..index], .. _waivers[(index + 1)..]]);
            }
        }
    }

    private sealed class Registration(TeleportWaiverRegistry owner, IAreaTriggerWaiver waiver) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Remove(waiver);
            }
        }
    }
}

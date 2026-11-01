namespace ArcaneCore.Gateway.Transfers;

/// <summary>What one gateway slot (a <c>Gateway:Workers:N</c> entry) is doing, as far as this gateway process knows.</summary>
internal enum GatewaySlotState
{
    /// <summary>Neither a handoff nor a player transfer involves the slot.</summary>
    Idle,

    /// <summary>One or more player transfers have the slot as their source or target.</summary>
    Transferring,

    /// <summary>A whole-World handoff of the slot is running (from its pre-flight until it ends).</summary>
    HandingOff,
}

/// <summary>
/// Lets the whole-World handoff of a slot be refused while player transfers involve it (design _wf/handoff-control-DESIGN.md section 5). Implemented by <see cref="GatewayTransferService"/>;
/// <c>GatewayHandoffService.HandoffAsync</c> enters it before its pre-flight and disposes the lease when the handoff ends.
/// </summary>
internal interface IGatewayHandoffGuard
{
    /// <summary>
    /// Marks <paramref name="slotNode"/> as handing off (from here on every transfer that involves it is refused) after checking that no transfer involving it is running or unresolved in the journal.
    /// Throws <see cref="ArcaneCore.World.Cluster.Handoff.WorldHandoffUnavailableException"/> when it refuses. Dispose the lease to leave the state.
    /// </summary>
    Task<IDisposable> EnterHandoffAsync(string slotNode, CancellationToken cancellationToken);
}

/// <summary>
/// The per-slot state machine that keeps whole-World handoffs and player transfers apart (design section 5): Idle, Transferring(n), HandingOff.
/// <list type="bullet">
/// <item><description>A transfer takes a lease on its source and its target slot (one lease, both slots; the same slot twice counts once). It is refused while either slot is HandingOff.</description></item>
/// <item><description>A handoff takes a lease on its slot. It is refused while the slot is Transferring or already HandingOff. From the moment it holds the lease, new transfers of the slot are refused, so the handoff's
/// pre-flight and the journal scan see a slot that only drains.</description></item>
/// </list>
/// State is in memory only: it is rebuilt by the work that is running, and what a restart leaves behind is the journal's business (the service checks the journal as well).
/// Thread-safe: one lock guards every slot; no member blocks or allocates beyond the lease object. A lease releases once; further disposals do nothing.
/// </summary>
internal sealed class GatewaySlotStateMachine
{
    private sealed class Slot
    {
        public int Transfers;
        public bool HandingOff;
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);

    /// <param name="slotNodes">The slots (<c>Gateway:Workers:N:NodeId</c>); every other name is refused as unknown.</param>
    public GatewaySlotStateMachine(IEnumerable<string> slotNodes)
    {
        ArgumentNullException.ThrowIfNull(slotNodes);
        foreach (string node in slotNodes)
        {
            _slots[node] = new Slot();
        }
    }

    /// <summary>The slot's state, or <see cref="GatewaySlotState.Idle"/> for an unknown slot.</summary>
    public GatewaySlotState StateOf(string slotNode)
    {
        lock (_gate)
        {
            if (!_slots.TryGetValue(slotNode, out Slot? slot))
            {
                return GatewaySlotState.Idle;
            }

            return slot.HandingOff ? GatewaySlotState.HandingOff : slot.Transfers > 0 ? GatewaySlotState.Transferring : GatewaySlotState.Idle;
        }
    }

    /// <summary>How many transfers involve the slot right now (the n of Transferring(n)).</summary>
    public int TransfersOf(string slotNode)
    {
        lock (_gate)
        {
            return _slots.TryGetValue(slotNode, out Slot? slot) ? slot.Transfers : 0;
        }
    }

    /// <summary>
    /// Takes the transfer lease on <paramref name="sourceNode"/> and <paramref name="targetNode"/>. False, with the reason, when either slot is unknown or HandingOff; nothing changes then.
    /// </summary>
    public bool TryBeginTransfer(string sourceNode, string targetNode, out IDisposable? lease, out string reason)
    {
        lock (_gate)
        {
            if (!_slots.TryGetValue(sourceNode, out Slot? source))
            {
                return Refuse($"worker '{sourceNode}' is not a gateway worker slot", out lease, out reason);
            }

            if (!_slots.TryGetValue(targetNode, out Slot? target))
            {
                return Refuse($"worker '{targetNode}' is not a gateway worker slot", out lease, out reason);
            }

            if (source.HandingOff)
            {
                return Refuse($"slot '{sourceNode}' is handing off", out lease, out reason);
            }

            if (target.HandingOff)
            {
                return Refuse($"slot '{targetNode}' is handing off", out lease, out reason);
            }

            source.Transfers++;
            if (!ReferenceEquals(source, target))
            {
                target.Transfers++;
            }

            lease = new Lease(() => EndTransfer(source, target));
            reason = string.Empty;
            return true;
        }
    }

    /// <summary>Takes the handoff lease on <paramref name="slotNode"/>. False, with the reason, when the slot is unknown, Transferring or already HandingOff; nothing changes then.</summary>
    public bool TryBeginHandoff(string slotNode, out IDisposable? lease, out string reason)
    {
        lock (_gate)
        {
            if (!_slots.TryGetValue(slotNode, out Slot? slot))
            {
                return Refuse($"worker '{slotNode}' is not a gateway worker slot", out lease, out reason);
            }

            if (slot.HandingOff)
            {
                return Refuse($"slot '{slotNode}' already has a handoff running", out lease, out reason);
            }

            if (slot.Transfers > 0)
            {
                return Refuse($"slot '{slotNode}' has {slot.Transfers} player transfer(s) running", out lease, out reason);
            }

            slot.HandingOff = true;
            lease = new Lease(() => EndHandoff(slot));
            reason = string.Empty;
            return true;
        }
    }

    private static bool Refuse(string why, out IDisposable? lease, out string reason)
    {
        lease = null;
        reason = why;
        return false;
    }

    private void EndTransfer(Slot source, Slot target)
    {
        lock (_gate)
        {
            source.Transfers--;
            if (!ReferenceEquals(source, target))
            {
                target.Transfers--;
            }
        }
    }

    private void EndHandoff(Slot slot)
    {
        lock (_gate)
        {
            slot.HandingOff = false;
        }
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

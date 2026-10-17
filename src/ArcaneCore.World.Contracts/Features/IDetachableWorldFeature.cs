using ArcaneCore.Game.Maps;

namespace ArcaneCore.World.Features;

/// <summary>How a feature may leave a running world (docs/design/hot-reload-tiers.md, section 4.2).</summary>
public enum DetachKind
{
    /// <summary>Never detached: attached once at start and stopped at shutdown. Every plain <see cref="IWorldFeature"/> is pinned.</summary>
    Pinned,

    /// <summary>Detachable with nothing to carry over: it is re-attached cold (<see cref="IDetachableWorldFeature.AttachFrom"/> with a null handover).</summary>
    Stateless,

    /// <summary>Detachable; <see cref="IDetachableWorldFeature.Detach"/> writes a <see cref="FeatureHandover"/> that the next attach reads.</summary>
    Handover,
}

/// <summary>
/// A world feature that can be detached from a running world and attached again (in process, or later as a new build of a
/// pack) without a restart. The host (<c>FeatureHost</c> in the world assembly) drives it; nothing else calls these members.
/// <list type="number">
/// <item><see cref="QuiesceAsync"/>, off the world thread: stop the feature's loops and threads, await its tasks, flush its
/// saves. The feature is still attached and its subscriptions still fire; it must keep the world consistent while quiesced.</item>
/// <item><see cref="Detach"/>, on the world thread at a tick boundary: clear static weak tables and <c>[ThreadStatic]</c>s,
/// write the state to carry over into the handover (core and contract types only). The host then disposes the
/// feature's <see cref="FeatureSubscriptions"/>, which removes every subscription the feature made through it.</item>
/// <item><see cref="AttachFrom"/>, on the world thread at a tick boundary: subscribe through the given bag (the only way
/// to subscribe to core events) and restore from the handover (null = cold start).</item>
/// </list>
/// All or nothing: if <see cref="Detach"/> or the unsubscribes throw, the host disposes the bag and re-attaches the
/// feature from the same handover in the same tick; if <see cref="AttachFrom"/> throws, the subscriptions it made are
/// disposed and the feature stays detached with its handover kept. At start the host calls <see cref="AttachFrom"/> with a
/// null handover instead of <see cref="IWorldFeature.Attach"/>.
/// </summary>
public interface IDetachableWorldFeature : IWorldFeature
{
    /// <summary>How this feature may be detached. <see cref="DetachKind.Pinned"/> makes the host refuse every detach.</summary>
    DetachKind DetachKind { get; }

    /// <summary>
    /// Attach without the host (a test, or a caller that never detaches): a cold start with a bag nobody disposes. The
    /// host never calls this; it calls <see cref="AttachFrom"/> with a bag it owns.
    /// </summary>
    void IWorldFeature.Attach(WorldRuntime world) => AttachFrom(world, null, new FeatureSubscriptions(GetType().Name));

    /// <summary>
    /// Stop background work before a detach (off the world thread). The token is cancelled when the host gives up waiting; a
    /// quiesce that fails or times out is undone by a <see cref="Detach"/> and <see cref="AttachFrom"/> in one tick, possibly
    /// while work the quiesce did not reach is still running (see those two).
    /// </summary>
    Task QuiesceAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Leave the world (world thread, tick boundary): release static state and write what to carry over into
    /// <paramref name="handover"/> (a <see cref="DetachKind.Stateless"/> feature may ignore it). Do not unsubscribe what
    /// went through the bag: the host disposes it right after. Throw, having changed nothing, when background work the quiesce
    /// did not drain still uses the state a detach would clear: the host rolls back (re-attaches from the empty handover).
    /// </summary>
    void Detach(FeatureHandover handover);

    /// <summary>
    /// Join the world (world thread, tick boundary; before the world thread starts at startup). Subscribe only through
    /// <paramref name="subscriptions"/>; restore from <paramref name="handover"/>, or start cold when it is null. Keep, do not
    /// start a second of, any background service still running (a failed quiesce did not stop it).
    /// </summary>
    void AttachFrom(WorldRuntime world, FeatureHandover? handover, FeatureSubscriptions subscriptions);
}

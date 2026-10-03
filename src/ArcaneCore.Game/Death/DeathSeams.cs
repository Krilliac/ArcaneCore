using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Death;

/// <summary>
/// Where a released spirit goes (vmangos <c>Player::RepopAtGraveyard</c>, Player.cpp:4988-5025). An
/// implementation picks the nearest graveyard and moves the player; it returns true when it did.
/// </summary>
public interface IGraveyardRepop
{
    /// <summary>
    /// The player is (or, for the undermap and fatigue paths, may be alive and) at a place the world
    /// sends it back from. Returns false when no graveyard applies and the player stays where it is.
    /// </summary>
    bool RepopAtGraveyard(Player player);
}

/// <summary>
/// The per-world registry of the death area's own extension points. <see cref="Combat.CombatHooks"/>
/// accepts one production registration (first wins, and its registered subclass is sealed) and
/// <see cref="DeathHooks"/> can be replaced wholesale, so neither can carry features that several
/// independent slices add. This registry is separate, per <see cref="WorldRuntime"/> (the same weak
/// side-table pattern as <see cref="DeathHooks"/>), and each seam is filled once by the feature that
/// owns it; a replacement of <see cref="DeathHooks"/> does not drop it.
/// <para>Thread affinity: written at startup before the world thread runs; read on the world thread.</para>
/// </summary>
public sealed class DeathSeams
{
    private static readonly ConditionalWeakTable<WorldRuntime, DeathSeams> s_registered = new();

    private IGraveyardRepop? _graveyards;

    private DeathSeams()
    {
    }

    /// <summary>The graveyard implementation, or null (a released spirit then stays on its body, the behaviour before the graveyard feature).</summary>
    public IGraveyardRepop? Graveyards => _graveyards;

    /// <summary>The seams of <paramref name="world"/>, created empty on first use (startup).</summary>
    public static DeathSeams Of(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return s_registered.GetValue(world, static _ => new DeathSeams());
    }

    /// <summary>The seams of <paramref name="world"/> if anything registered one; otherwise null (read-only lookup, creates nothing).</summary>
    public static DeathSeams? Find(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return s_registered.TryGetValue(world, out DeathSeams? seams) ? seams : null;
    }

    /// <summary>Register the graveyard implementation; the first registration wins and later ones return false.</summary>
    public bool TryRegisterGraveyards(IGraveyardRepop graveyards)
    {
        ArgumentNullException.ThrowIfNull(graveyards);
        return Interlocked.CompareExchange(ref _graveyards, graveyards, null) is null;
    }
}

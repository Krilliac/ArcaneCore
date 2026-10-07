using System.Runtime.CompilerServices;
using ArcaneCore.Game.Battlegrounds;
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

    /// <summary>
    /// The spirit of a player that is leaving the world was just released (vmangos WorldSession::LogoutPlayer,
    /// WorldSession.cpp:694-701): the place the character is saved at becomes the graveyard. No teleport happens, since
    /// the client is gone and would never acknowledge it. Returns false when no graveyard applies.
    /// </summary>
    bool RelocateLeavingPlayer(Player player);

    /// <summary>
    /// A ghost was resurrected by a spirit healer (vmangos WorldSession::SendSpiritResurrect, NPCHandler.cpp:430-471):
    /// when the graveyard nearest to its <paramref name="corpse"/> differs from the one nearest to where the player stands, it is
    /// teleported to the corpse's graveyard, facing the safe location's facing when it has one (else keeping its own);
    /// otherwise, or without a corpse, only its visibility is refreshed. Returns true when a teleport was started.
    /// </summary>
    bool TeleportToCorpseGraveyard(Player player, CorpsePlace? corpse);
}

/// <summary>
/// The ghost aura of a released spirit (vmangos Player::ApplyGhostForm / RemoveGhostForm, Player.cpp:4561-4577): the ghost
/// spell and, for a night elf, the wisp. Water walking is not part of it (vmangos orders it separately); combat does that.
/// </summary>
public interface IGhostForm
{
    /// <summary>Cast the ghost spell(s) on the player.</summary>
    void Apply(Player player);

    /// <summary>Remove the ghost spell(s) from the player.</summary>
    void Remove(Player player);
}

/// <summary>Where a corpse lies: its map and position.</summary>
public readonly record struct CorpsePlace(uint MapId, float X, float Y, float Z);

/// <summary>
/// The battleground match a player is bound to, as the death rules see it (vmangos <c>Player::GetBattleGround</c>; the
/// <see cref="BattlegroundManager"/> implements it).
/// </summary>
public interface IBattlegroundPresence
{
    /// <summary>The status of the match <paramref name="player"/> is bound to, or null when it is in none.</summary>
    BattlegroundStatus? MatchStatusOf(ObjectGuid player);
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
    private IGhostForm? _ghostForm;
    private IBattlegroundPresence? _battlegrounds;

    private DeathSeams()
    {
    }

    /// <summary>The graveyard implementation, or null (a released spirit then stays on its body, the behaviour before the graveyard feature).</summary>
    public IGraveyardRepop? Graveyards => _graveyards;

    /// <summary>The ghost form implementation, or null (combat then sets the ghost flag itself, as before the ghost aura existed).</summary>
    public IGhostForm? GhostForm => _ghostForm;

    /// <summary>The battleground matches, or null (no player is in a match: nothing is gated on one).</summary>
    public IBattlegroundPresence? Battlegrounds => _battlegrounds;

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

    /// <summary>Register the ghost form implementation; the first registration wins and later ones return false.</summary>
    public bool TryRegisterGhostForm(IGhostForm ghostForm)
    {
        ArgumentNullException.ThrowIfNull(ghostForm);
        return Interlocked.CompareExchange(ref _ghostForm, ghostForm, null) is null;
    }

    /// <summary>Register the graveyard implementation; the first registration wins and later ones return false.</summary>
    public bool TryRegisterGraveyards(IGraveyardRepop graveyards)
    {
        ArgumentNullException.ThrowIfNull(graveyards);
        return Interlocked.CompareExchange(ref _graveyards, graveyards, null) is null;
    }

    /// <summary>Register the battleground matches; the first registration wins and later ones return false.</summary>
    public bool TryRegisterBattlegrounds(IBattlegroundPresence battlegrounds)
    {
        ArgumentNullException.ThrowIfNull(battlegrounds);
        return Interlocked.CompareExchange(ref _battlegrounds, battlegrounds, null) is null;
    }
}

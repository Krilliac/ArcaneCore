using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Pets;

/// <summary>
/// Why a summon left the world through <see cref="SummonService.Unsummon(ArcaneCore.Game.Creatures.Creature, PetUnsummonReason)"/>
/// (the cause behind vmangos' <c>Pet::Unsummon(PetSaveMode)</c>, Pet.cpp:1040-1140). Only the log line reads it.
/// </summary>
public enum PetUnsummonReason
{
    /// <summary>A caller that did not say why.</summary>
    Unspecified,

    /// <summary>The owner dismissed it (Dismiss Pet, the dismiss command, a charm released).</summary>
    Dismissed,

    /// <summary>Another pet or guardian took its place (a new summon, a stable swap, a tame).</summary>
    Replaced,

    /// <summary>The owner left the map (far teleport, logout) or the pet was put away for a while.</summary>
    OwnerLeft,

    /// <summary>The owner died and the pet was out of combat.</summary>
    OwnerDied,

    /// <summary>The pet died and its corpse decayed.</summary>
    CorpseDecayed,

    /// <summary>A timed summon ran out.</summary>
    Expired,

    /// <summary>The pet's loyalty ran out and it ran away (SMSG_PET_BROKEN).</summary>
    RanAway,
}

/// <summary>Log policy for <see cref="PetUnsummonReason"/>.</summary>
internal static class PetUnsummonReasons
{
    /// <summary>The level a reason is logged at: a pet running away is worth a line in the normal log, every routine departure is Debug.</summary>
    internal static LogLevel LevelOf(PetUnsummonReason reason) => reason == PetUnsummonReason.RanAway ? LogLevel.Information : LogLevel.Debug;
}

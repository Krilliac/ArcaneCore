namespace ArcaneCore.Game.Creatures;

/// <summary>How proximity aggro is driven.</summary>
public enum AggroScanMode
{
    /// <summary>
    /// vmangos behaviour: a unit that moves (or joins the map) schedules an AI notify after
    /// <see cref="CreatureOptions.AiRelocationNotifyDelayMs"/>; the notify visits every creature (for a player) or every
    /// player (for a creature) within <see cref="CreatureOptions.MaxCreatureAttackRadius"/> times the aggro rate and calls
    /// <c>MoveInLineOfSight</c> (Objects/Unit.cpp:10082-10113, Maps/GridNotifiersImpl.h:57-120). Standing still
    /// triggers nothing.
    /// </summary>
    Relocation,

    /// <summary>Development: every creature checks every player in the map each tick (the original implementation).</summary>
    Poll,
}

public sealed partial class CreatureOptions
{
    /// <summary>How proximity aggro is driven (<c>Creatures:AggroScanMode</c>). <see cref="AggroScanMode.Relocation"/> is retail behaviour.</summary>
    public AggroScanMode AggroScanMode { get; set; } = AggroScanMode.Relocation;

    /// <summary>Visibility.AIRelocationNotifyDelay (ms): vmangos 1000 (World.cpp:829, mangosd.conf.dist.in:2605).</summary>
    public uint AiRelocationNotifyDelayMs { get; set; } = 1000;

    /// <summary>MaxCreaturesAttackRadius (yd): the relocation notify visits objects this far around the mover times the aggro rate; vmangos 40 (World.cpp:565, mangosd.conf.dist.in:1528).</summary>
    public float MaxCreatureAttackRadius { get; set; } = 40.0f;

    /// <summary>
    /// Milliseconds a creature cannot initiate attacks after it respawns (vmangos Creature::SetTempPacified(5000) on respawn,
    /// Objects/Creature.cpp:877-878). 0 disables.
    /// </summary>
    public uint RespawnPacifyMs { get; set; } = 5000;

    /// <summary>Send SMSG_AI_REACTION(hostile) when a creature starts attacking (vmangos Creature::SendAIReaction from Unit::Attack); the client plays the aggro sound from it.</summary>
    public bool SendAiReaction { get; set; } = true;

    /// <summary>
    /// Development: add both bounding radii to the aggro range and subtract them from the vertical test. Retail (false)
    /// measures the plain distance (<c>IsWithinDistInMap(..., SizeFactor::None)</c>, AI/BasicAI.cpp:61-67).
    /// </summary>
    public bool AggroUsesBoundingRadius { get; set; }
}

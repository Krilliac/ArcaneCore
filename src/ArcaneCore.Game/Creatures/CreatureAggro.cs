using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures;

/// <summary>vmangos <c>ReactStates</c> (Objects/UnitDefines.h:680-685): how a creature reacts to units around it.</summary>
public enum CreatureReactState : byte
{
    /// <summary>Never attacks and does not fight back.</summary>
    Passive = 0,

    /// <summary>Fights back and assists but does not attack on sight.</summary>
    Defensive = 1,

    /// <summary>Attacks hostile units that come into aggro range.</summary>
    Aggressive = 2,
}

/// <summary>SMSG_AI_REACTION reaction (gtker wow_messages smsg_ai_reaction.wowm: u32 after the guid).</summary>
public enum AiReaction : uint
{
    /// <summary>Pre-aggro (stealth alert).</summary>
    Alert = 0,

    Friendly = 1,

    /// <summary>Sent on every attack start; the client plays the creature's aggro sound.</summary>
    Hostile = 2,

    Afraid = 3,

    Destroy = 4,
}

/// <summary>Proximity aggro rules of a creature (vmangos Creature::GetAttackDistance, Creature::InitializeReactState).</summary>
public static class CreatureAggro
{
    /// <summary>vmangos CREATURE_Z_ATTACK_RANGE (Objects/CreatureDefines.h:622): no aggro on a unit more than 3 yd above or below.</summary>
    public const float MaxZDistance = 3.0f;

    /// <summary>vmangos MAX_LEVEL_DIFF_FOR_AGGRO_RANGE (Objects/CreatureDefines.h:655): at most 25 levels counted below.</summary>
    public const int MaxLevelDifferenceBelow = 25;

    /// <summary>The floor of the aggro radius for templates with a detection range of at least 5 (yd).</summary>
    public const float MinimumRadius = 5.0f;

    /// <summary>
    /// vmangos Creature::GetAttackDistance (Objects/Creature.cpp:2193-2240): the template's detection range
    /// (<paramref name="detectionRange"/>, 18 by default) minus the level difference (target above the creature shrinks it,
    /// below grows it, at most 25 levels counted below), never under <c>min(detectionRange, 5)</c>, times the aggro rate.
    /// A detection range under 1 or a rate of 0 means no proximity aggro. The detect-range auras are not applied (no aura
    /// type exists for them yet).
    /// </summary>
    public static float GetAttackDistance(float detectionRange, int creatureLevel, int targetLevel, float aggroRate)
    {
        if (aggroRate == 0)
        {
            return 0;
        }

        int levelDifference = Math.Max(targetLevel - creatureLevel, -MaxLevelDifferenceBelow);
        if (detectionRange < 1)
        {
            return 0;
        }

        float distance = detectionRange - levelDifference;
        float minimum = Math.Min(detectionRange, MinimumRadius);
        if (distance < minimum)
        {
            distance = minimum;
        }

        return distance * aggroRate;
    }

    /// <summary>
    /// vmangos Creature::InitializeReactState (Objects/Creature.cpp:714-722): totems, triggers (the invisible flag), creatures
    /// that cannot have a target and the IGNORE_COMBAT static flag are passive; NO_AGGRO makes a creature defensive; everything
    /// else is aggressive.
    /// </summary>
    public static CreatureReactState InitialReactState(CreatureTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        CreatureBehaviourFlags behaviour = template.Behaviour;
        const uint TotemType = 11;
        if (template.CreatureType == TotemType
            || (behaviour & (CreatureBehaviourFlags.Invisible | CreatureBehaviourFlags.NoTarget | CreatureBehaviourFlags.IgnoreCombat)) != 0)
        {
            return CreatureReactState.Passive;
        }

        return (behaviour & CreatureBehaviourFlags.NoAggro) != 0 ? CreatureReactState.Defensive : CreatureReactState.Aggressive;
    }
}

/// <summary>SMSG_AI_REACTION (0x13C): the creature guid and a u32 reaction (gtker wow_messages smsg_ai_reaction.wowm).</summary>
public static class CreatureAiReactionPackets
{
    public static byte[] Build(ObjectGuid creature, AiReaction reaction)
    {
        var writer = new PacketWriter(12);
        writer.WriteUInt64(creature.Value);
        writer.WriteUInt32((uint)reaction);
        return writer.ToArray();
    }
}

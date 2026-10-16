using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// The Scourge Cauldron (entry 11152, Western Plaguelands), the "Target:" quests of the four cauldrons (5216/5229 Felstone Field, 5219/5231
/// Dalson's Tears, 5225/5235 Gahrron's Withering, 5222/5233 Writhing Haunt): mangos-classic ScriptDev2 <c>npc_the_scourge_cauldron</c>
/// (eastern_kingdoms/western_plaguelands.cpp:41-101, registered :1001-1004). A player who comes into sight with one of the two quests of the
/// cauldron's area incomplete makes it summon its Cauldron Lord (TEMPSPAWN_TIMED_OOC_OR_DEAD_DESPAWN, 10 minutes), whose drop is the quest's
/// key, and die (DoDie :47-55). A cauldron that respawns in under ten minutes gets a ten-minute respawn delay for its later deaths.
/// </summary>
public sealed class ScourgeCauldronAI(Creature creature) : ScriptedAI(creature)
{
    public const uint Entry = 11152;
    public const uint AreaFelstone = 199, AreaDalson = 200, AreaGahrron = 201, AreaWrithing = 202;
    public const uint NpcBilemaw = 11075, NpcRazarch = 11076, NpcMalvinious = 11077, NpcSoulwrath = 11078;
    public const uint QuestFelstoneA = 5216, QuestFelstoneH = 5229, QuestDalsonA = 5219, QuestDalsonH = 5231;
    public const uint QuestGahrronA = 5225, QuestGahrronH = 5235, QuestWrithingA = 5222, QuestWrithingH = 5233;
    public const uint LordDespawnMs = 600_000, MinRespawnDelaySeconds = 600;

    /// <summary>CONTACT_DISTANCE: how far in front of the cauldron (beyond its bounding radius) the lord appears.</summary>
    public const float ContactDistance = 0.5f;

    /// <summary>The area's two quests and its lord (MoveInLineOfSight :64-98).</summary>
    public static (uint QuestA, uint QuestB, uint Lord)? AreaEvent(uint areaId) => areaId switch
    {
        AreaFelstone => (QuestFelstoneA, QuestFelstoneH, NpcBilemaw),
        AreaDalson => (QuestDalsonA, QuestDalsonH, NpcMalvinious),
        AreaGahrron => (QuestGahrronA, QuestGahrronH, NpcSoulwrath),
        AreaWrithing => (QuestWrithingA, QuestWrithingH, NpcRazarch),
        _ => null,
    };

    protected override void Reset()
    {
    }

    /// <summary>MoveInLineOfSight (:57-100); no aggro on sight, as the source does not call the base.</summary>
    public override void MoveInLineOfSight(Unit who)
    {
        ArgumentNullException.ThrowIfNull(who);
        if (who is not Player player || !Me.IsAlive || System is not { } system)
        {
            return;
        }

        if (AreaEvent(system.ZoneAndAreaOf(Me).AreaId) is not { } area
            || !(system.QuestIncomplete(player, area.QuestA) || system.QuestIncomplete(player, area.QuestB)))
        {
            return;
        }

        // SummonCreature(lord, 0, 0, 0, 0, ...): all-zero coordinates put the summon at contact distance in front of the summoner, facing 0
        // (Object.cpp:2050-2054).
        float reach = Me.BoundingRadius + ContactDistance;
        float x = Me.X + (reach * MathF.Cos(Me.Orientation)), y = Me.Y + (reach * MathF.Sin(Me.Orientation));
        system.SummonAt(Me, area.Lord, x, y, system.GroundZ(x, y, Me.Z) ?? Me.Z, 0f, null, LordDespawnMs);
        DoDie(system);
    }

    /// <summary>DoDie (:47-55): Suicide, then a respawn delay of at least 600 s for the deaths that follow.</summary>
    private void DoDie(CreatureMapSystem system)
    {
        system.KillCreature(Me);
        // cmangos draws this death's delay at JUST_DIED, before the script's SetRespawnDelay; GetRespawnDelay is that delay (or an earlier override).
        uint delay = Me.RespawnDelayOverrideSeconds ?? Me.RespawnDelaySeconds;
        if (delay < MinRespawnDelaySeconds)
        {
            Me.RespawnDelayOverrideSeconds = MinRespawnDelaySeconds;
        }
    }
}

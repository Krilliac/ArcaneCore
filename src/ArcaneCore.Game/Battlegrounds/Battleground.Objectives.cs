using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Honor;

namespace ArcaneCore.Game.Battlegrounds;

/// <summary>
/// A game object a participant used, as the battleground rules see it: its entry, the event pair of its <c>gameobject_battleground</c> row
/// (vmangos <c>GetGameObjectEventIndex(guid)</c>; <see cref="BattlegroundConstants.EventNone"/> for an object without one, such as a dropped
/// flag) and whether the player stands within 10 yards of it (vmangos <c>IsWithinDistInMap(go, 10)</c>, used by Warsong Gulch).
/// </summary>
public readonly record struct BattlegroundObjectUse(uint Entry, byte Event1, byte Event2, bool WithinTenYards);

/// <summary>
/// The objective entry points every battleground type shares (vmangos <c>BattleGround</c> virtuals that the types override): flag clicks, flag
/// drops, creature kills, the buff objects, the graveyard choice by position and the team helpers Arathi Basin and Alterac Valley use.
/// </summary>
public abstract partial class Battleground
{
    /// <summary>
    /// The flag carrier MSG_BATTLEGROUND_PLAYER_POSITIONS shows a viewer of <paramref name="viewerTeam"/>, or empty (vmangos
    /// HandleBattleGroundPlayerPositionsOpcode, BattleGroundHandler.cpp:296-321: only Warsong Gulch has one).
    /// </summary>
    public virtual ObjectGuid FlagCarrierShownTo(Team viewerTeam) => ObjectGuid.Empty;

    /// <summary>
    /// A participant lost what it carried (vmangos <c>BattleGround::EventPlayerDroppedFlag</c>): reached from the flag aura's removal (any cause:
    /// cancelled, stripped by an immunity, dispelled), from an accepted summon, from a death and from leaving. Nothing by default.
    /// </summary>
    public virtual void EventPlayerDroppedFlag(ObjectGuid player)
    {
    }

    /// <summary>A participant used a flag, banner or flag stand (vmangos <c>BattleGround::EventPlayerClickedOnFlag</c>). Nothing by default.</summary>
    public virtual void EventPlayerClickedOnFlag(ObjectGuid player, BattlegroundObjectUse target)
    {
    }

    /// <summary>
    /// A participant killed a creature of the match (vmangos <c>BattleGround::HandleKillUnit</c>); <paramref name="event1"/> is the first event of
    /// the creature's <c>creature_battleground</c> row, <see cref="BattlegroundConstants.EventNone"/> without one. Nothing by default.
    /// </summary>
    public virtual void HandleKillUnit(uint creatureEntry, byte event1, ObjectGuid killer)
    {
    }

    /// <summary>
    /// The graveyard a dead participant at (<paramref name="x"/>, <paramref name="y"/>) repops at (vmangos <c>GetClosestGraveYard(Player*)</c>);
    /// <paramref name="safeLoc"/> resolves a WorldSafeLocs id to its position (null for an unknown id). The default ignores the position.
    /// </summary>
    public virtual uint ClosestGraveyard(Team team, float x, float y, Func<uint, (float X, float Y)?> safeLoc) => ClosestGraveyard(team);

    /// <summary>Whether a used buff object changes its type (vmangos <c>m_buffChange</c>: Arathi Basin; Warsong Gulch buffs are static database spawns).</summary>
    public virtual bool BuffChange => false;

    /// <summary>
    /// A buff object of the match was used (vmangos <c>BattleGround::HandleTriggerBuff</c>, BattleGround.cpp:1713-1757, after the trap cast its
    /// spell). <paramref name="objectIndex"/> is the object's index among the match's own objects (<see cref="IBattlegroundHost.AddBattlegroundObject"/>),
    /// -1 for a database spawn. Returns false when the object is static (<see cref="BuffChange"/> off): the caller then deactivates it so it
    /// respawns on its own timer. Otherwise a random buff type is chosen; when it differs the used object is despawned and its sibling of the
    /// chosen type takes its place, which appears after <see cref="BattlegroundConstants.BuffRespawnTimeSeconds"/>.
    /// </summary>
    public bool HandleTriggerBuff(int objectIndex, uint entry)
    {
        if (!BuffChange)
        {
            return false;
        }

        if (objectIndex < 0)
        {
            return true;    // vmangos logs "has buff trigger ... but it hasn't that object in its internal data" and does nothing
        }

        int buff = Ports.Random.Next(0, 3);
        int index = objectIndex;
        if (entry != BattlegroundConstants.BuffEntries[buff])
        {
            Host.SpawnBattlegroundObject(index, BattlegroundConstants.RespawnNeverSeconds);
            for (int current = 0; current < BattlegroundConstants.BuffEntries.Count; current++)
            {
                if (entry == BattlegroundConstants.BuffEntries[current])
                {
                    index = index - current + buff;
                }
            }
        }

        Host.SpawnBattlegroundObject(index, BattlegroundConstants.BuffRespawnTimeSeconds);
        return true;
    }

    /// <summary>Cast a spell on every participant of a team (vmangos <c>CastSpellOnTeam</c>, BattleGround.cpp:546-564).</summary>
    protected void CastSpellOnTeam(uint spellId, Team team)
    {
        foreach ((ObjectGuid guid, Team playerTeam) in Participants())
        {
            if (playerTeam == team)
            {
                Ports.Spells.CastOnSelf(guid, spellId);
            }
        }
    }

    /// <summary>
    /// Bonus honor for a number of "kills" (vmangos <c>GetBonusHonorFromKill</c>, BattleGround.cpp:771-776): what a kill of a rank 1 target at the
    /// bracket's highest level is worth, times <paramref name="kills"/>.
    /// </summary>
    public uint BonusHonorFromKill(uint kills) => kills * (uint)HonorKillPoints.Honorable(MaxLevel, MaxLevel, victimVisualRank: 1, totalKills: 0, groupSize: 1);

    /// <summary>
    /// The bonus honor factor of a short match (vmangos <c>GetHonorModifier</c>, BattleGround.cpp:778-783): <c>60^(hours - 1)</c> under one hour
    /// of start time, 1 after.
    /// </summary>
    public float HonorModifier
    {
        get
        {
            float elapsed = StartTimeMs / 1000f / 3600f;
            return elapsed < 1.0f ? (float)Math.Pow(60, elapsed - 1) : 1.0f;
        }
    }
}

using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Rabid Thistle Bear (entry 2164, Darkshore), "Plagued Lands" (quest 2118): mangos-classic ScriptDev2 <c>npc_rabid_bear</c>
/// (kalimdor/darkshore.cpp:544-655, registered :920-923). In combat it casts Rabies (3150) on its top-threat unit every 12-18 s (UpdateAI
/// :623-649). A player's Bear Captured in Trap (9439) ends the fight (auras reset, combat stopped, loot tap dropped, timers reset), turns it
/// into the Captured Rabid Thistle Bear (11836) and, when the player still has the quest incomplete, gives the player the kill credit of 11836 and
/// makes the bear follow them; otherwise it despawns after a second (SpellHit :597-621). Either way it despawns three minutes later when it is
/// not in combat, and a captured bear that sees Tharnariun Treetender (3701) within 10 yd (flat) despawns after a second and stops (MoveInLineOfSight
/// :582-595, which is on only after the capture: the source sets its check timer to 0 there and to 1000 on every respawn).
/// </summary>
public sealed class RabidBearAI(Creature creature) : ScriptedAI(creature)
{
    public const uint Entry = 2164, CapturedEntry = 11836, NpcTharnariunTreetender = 3701, QuestPlaguedLands = 2118;
    public const uint SpellRabies = 3150, SpellBearCaptured = 9439;
    public const uint RabiesMinMs = 12_000, RabiesMaxMs = 18_000, DespawnAfterCaptureMs = 3 * 60_000, DespawnDelayMs = 1000;

    /// <summary>2 * INTERACTION_DISTANCE (5 yd): how near Tharnariun Treetender takes the captured bear off (:584-585).</summary>
    public const float TreetenderRange = 10f;

    private uint _rabiesMs;
    private uint _despawnMs;
    private bool _captured;
    private bool _leaving;

    /// <summary>Whether a player's trap has caught it (m_uiCheckTimer is 0 in the source).</summary>
    public bool Captured => _captured;

    /// <summary>m_uiDespawnTimer: the time left before the captured bear goes, 0 when it is not counting.</summary>
    public uint DespawnMs => _despawnMs;

    public uint RabiesMs => _rabiesMs;

    protected override void Reset() => _rabiesMs = RollRabies();

    /// <summary>JustRespawned (:576-580): the check timer is back to 1000 and no despawn is pending.</summary>
    public override void OnRespawn()
    {
        _captured = false;
        _leaving = false;
        _despawnMs = 0;
        base.OnRespawn();
    }

    private uint RollRabies() => (uint)(System?.RandomInt((int)RabiesMinMs, (int)RabiesMaxMs) ?? (int)RabiesMinMs);

    public override void MoveInLineOfSight(Unit who)
    {
        ArgumentNullException.ThrowIfNull(who);
        if (_captured && who is Creature { Entry: NpcTharnariunTreetender } && System is { } system)
        {
            float dx = Me.X - who.X, dy = Me.Y - who.Y; // IsWithinDist(..., false): the flat distance
            if ((dx * dx) + (dy * dy) <= TreetenderRange * TreetenderRange)
            {
                // Here the engine reports the Treetender on every update, not once as a movement event; ForcedDespawn restarts its delay on
                // each call (cmangos queues one more event, the first of which fires after a second), so the despawn is requested once.
                if (!_leaving)
                {
                    _leaving = true;
                    system.ForcedDespawn(Me, DespawnDelayMs); // "Possible related spell: 9455 9372" (:587-591)
                    system.MoveIdle(Me);
                }

                return;
            }
        }

        base.MoveInLineOfSight(who);
    }

    /// <summary>SpellHit (:597-621).</summary>
    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(spell);
        if (caster is not Player player || spell.Id != SpellBearCaptured || System is not { } system)
        {
            return;
        }

        system.ResetAurasOnEvade(Me);
        system.StopCombatInPlace(Me);
        Reset();
        system.UpdateEntry(Me, CapturedEntry);
        if (system.QuestIncomplete(player, QuestPlaguedLands))
        {
            system.KilledMonsterCredit(player, Me.Entry, Me.Guid);
            Me.Motion.MoveFollow(player, FollowerAI.FollowDistance, FollowerAI.FollowAngle);
        }
        else
        {
            system.ForcedDespawn(Me, DespawnDelayMs); // something unexpected happened
        }

        _captured = true;
        _despawnMs = DespawnAfterCaptureMs; // "delay guesswork"
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_despawnMs != 0 && !Me.Combat.IsInCombat)
        {
            if (_despawnMs <= diffMs)
            {
                System?.ForcedDespawn(Me, 0);
                return;
            }

            _despawnMs -= diffMs;
        }

        if (!UpdateVictim() || Victim is null)
        {
            return;
        }

        if (_rabiesMs < diffMs)
        {
            if (TopThreatTarget() is { } target)
            {
                DoCast(target, SpellRabies);
            }

            _rabiesMs = RollRabies();
        }
        else
        {
            _rabiesMs -= diffMs;
        }
    }

    /// <summary>
    /// SelectAttackingTarget(ATTACKING_TARGET_TOPAGGRO, 0, SPELL_RABIES) (darkshore.cpp:640): the living unit with the most threat. The source also
    /// skips entries that fail the spell's target checks; the engine has no such check, so the cast itself refuses (batch 5 doc, deviations).
    /// </summary>
    private Unit? TopThreatTarget()
        => Me.Combat.HasThreatList ? Me.Combat.Threat.Entries.Select(e => e.Target).FirstOrDefault(u => u.IsAlive) ?? Victim : Victim;
}

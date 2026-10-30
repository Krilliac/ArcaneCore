using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.World;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// A Captured Sprite Darter (entry 7997, Feralas), freed by the cage door for "Freedom for All Creatures" (quest 2969): mangos-classic
/// ScriptDev2 <c>npc_captured_sprite_darter</c> (kalimdor/feralas.cpp:343-397, registered :640-643). Each reset picks one of its four
/// <c>script_waypoint</c> flight paths (0-3); the cage door (<see cref="KalimdorWorldMapData.OnGameObjectUse"/>) starts the escort at a run.
/// At point 8 it tells the Kalimdor map script it is free (<c>TYPE_FREEDOM_CREATURES</c> = SPECIAL) and despawns. In a fight it Mana Burns
/// (11981) a random attacker with mana every 7-10 s after a first 3-6 s.
/// </summary>
public sealed class CapturedSpriteDarterAI(Creature creature) : EscortAI(creature)
{
    public const uint Entry = 7997, SpellManaBurn = 11981, PointFree = 8;
    public const uint FirstManaBurnMinMs = 3000, FirstManaBurnMaxMs = 6000, ManaBurnMinMs = 7000, ManaBurnMaxMs = 10000;

    private uint _manaBurnMs;

    public uint ManaBurnMs => _manaBurnMs;

    /// <summary>The flight path chosen at the last reset (SetEscortWaypoints(urand(0, 3))).</summary>
    public uint FlightPath => EscortWaypointPath;

    /// <summary>Reset (:357-362).</summary>
    protected override void Reset()
    {
        _manaBurnMs = (uint)(System?.RandomInt(3, 6) ?? 3) * 1000;
        EscortWaypointPath = (uint)(System?.RandomInt(0, 3) ?? 0);
    }

    /// <summary>The cage door's <c>escortAI->Start(true, player, nullptr)</c> (:423-424).</summary>
    public bool StartFlight(Player player) => Start(run: true, player: player);

    /// <summary>WaypointReached (:364-372).</summary>
    protected override void WaypointReached(uint pointId)
    {
        if (pointId != PointFree)
        {
            return;
        }

        System?.Map.FindUpdater<InstanceData>()?.SetData(KalimdorWorldMapData.TypeFreedomCreatures, EncounterState.Special);
        System?.ForcedDespawn(Me, 0);
    }

    /// <summary>UpdateEscortAI (:374-391).</summary>
    protected override void UpdateEscortAI(uint diffMs)
    {
        if (!UpdateVictim() || Victim is null || System is not { } system)
        {
            return;
        }

        if (_manaBurnMs < diffMs)
        {
            // SelectAttackingTarget(ATTACKING_TARGET_RANDOM, 0, SPELL_MANA_BURN, SELECT_FLAG_POWER_MANA): a random mana user of the threat list.
            Unit[] withMana = Me.Combat.HasThreatList
                ? [.. Me.Combat.Threat.Entries.Select(e => e.Target).Where(u => u.IsAlive && u.PowerType == PowerType.Mana)]
                : [];
            if (withMana.Length > 0 && DoCast(withMana[system.RandomInt(0, withMana.Length - 1)], SpellManaBurn) == CreatureCastResult.Ok)
            {
                _manaBurnMs = (uint)system.RandomInt(7, 10) * 1000;
            }
        }
        else
        {
            _manaBurnMs -= diffMs;
        }
    }
}

/// <summary>
/// Kindal Moonweaver (entry 7956, Feralas), "Freedom for All Creatures" (quest 2969): mangos-classic ScriptDev2 <c>npc_kindal_moonweaver</c>
/// (kalimdor/feralas.cpp:440-570, registered :645-649) on <see cref="FollowerAI"/>. Taking the quest makes her say her start line, stand,
/// lose her NPC immunity and follow the player (faction 231). Every second while following she asks the Kalimdor map script whether six
/// sprite darters got away (<c>TYPE_FREEDOM_CREATURES</c> = DONE); then she resets it, gives the group the quest event when it is incomplete,
/// says her thanks and five seconds later goes back (despawn, respawn, immune to NPCs). In a fight: one aggro line in four, Shoot (14443)
/// every 2-4 s and Multi-Shot (6660) at 5-30 yd, and she flees once at 15% health.
/// </summary>
public sealed class KindalMoonweaverAI(Creature creature) : FollowerAI(creature), IQuestScriptAI
{
    public const uint Entry = 7956, QuestFreedomForAllCreatures = 2969, FactionEscortAllianceNeutralActive = 231;
    public const uint SpellShoot = 14443, SpellMultiShot = 6660;
    public const int SayQuestStart = -1010023, SayInitialAggro = -1010024, SayAggro = -1010025, SayQuestComplete = -1010026;
    public const uint CheckCompletionMs = 1000, DespawnDelayMs = 5000, FleeMs = 7000;
    public const float FleeHealthPercent = 15f, ShootMinRange = 5f, ShootMaxRange = 30f;

    private uint _checkCompletionMs, _shootMs, _multiShotMs, _despawnMs;
    private bool _hasFled;

    public uint ShootMs => _shootMs;

    public uint MultiShotMs => _multiShotMs;

    public uint DespawnMs => _despawnMs;

    public bool HasFled => _hasFled;

    /// <summary>Reset (:455-462).</summary>
    protected override void Reset()
    {
        _checkCompletionMs = CheckCompletionMs;
        _despawnMs = 0;
        _shootMs = (uint)(System?.RandomInt(2, 4) ?? 2) * 1000;
        _multiShotMs = (uint)(System?.RandomInt(8, 11) ?? 8) * 1000;
        _hasFled = false;
    }

    /// <summary>QuestAccept_npc_kindal_moonweaver (:561-572).</summary>
    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId != QuestFreedomForAllCreatures)
        {
            return;
        }

        StartFollow(player, FactionEscortAllianceNeutralActive, questId);
        System?.SayText(Me, SayQuestStart);
        Me.StandState = StandState.Stand;
        Me.UnitFlags &= ~UnitFlags.ImmuneToNpc;
    }

    /// <summary>Aggro (:464-468).</summary>
    protected override void Aggro(Unit target)
    {
        if (System is { } system && system.RandomInt(0, 3) == 0)
        {
            system.SayText(Me, SayAggro, target);
        }
    }

    /// <summary>UpdateFollowerAI (:470-553).</summary>
    protected override void UpdateFollowerAI(uint diffMs)
    {
        if (HasFollowState(FollowState.InProgress))
        {
            if (_checkCompletionMs < diffMs)
            {
                // The source never re-arms this timer: once it has run out the check runs on every update.
                if (System?.Map.FindUpdater<InstanceData>() is { } map && map.GetData(KalimdorWorldMapData.TypeFreedomCreatures) == EncounterState.Done)
                {
                    map.SetData(KalimdorWorldMapData.TypeFreedomCreatures, EncounterState.NotStarted);
                    if (GetLeaderForFollower() is { } player)
                    {
                        if (System.QuestIncomplete(player, QuestFreedomForAllCreatures))
                        {
                            System.RewardGroupEventExplored(player, QuestFreedomForAllCreatures, Me);
                        }

                        System.SayText(Me, SayQuestComplete, player);
                    }

                    _despawnMs = DespawnDelayMs;
                    SetFollowComplete(withEndEvent: true);
                }
            }
            else
            {
                _checkCompletionMs -= diffMs;
            }
        }

        if (_despawnMs != 0)
        {
            // Deviation: <= rather than the source's <, so an update diff that brings the timer to exactly 0 still sends her back (the source
            // then stops counting at 0 and she stays until something resets her).
            if (_despawnMs <= diffMs)
            {
                _despawnMs = 0;
                Me.UnitFlags |= UnitFlags.ImmuneToNpc;
                if (System is { } system)
                {
                    system.ForcedDespawn(Me, 0);
                    system.ForceRespawn(Me);
                }

                return;
            }

            _despawnMs -= diffMs;
        }

        if (!UpdateVictim() || Victim is not { } victim)
        {
            return;
        }

        if (!_hasFled && Me.MaxHealth > 0 && Me.Health * 100f / Me.MaxHealth <= FleeHealthPercent)
        {
            Me.Motion.MoveFleeing(null, FleeMs); // DoFlee: SetInPanic for CONFIG_UINT32_CREATURE_FAMILY_FLEE_DELAY
            _hasFled = true;
        }

        if (_shootMs < diffMs)
        {
            if (InShootingRange(victim) && DoCast(victim, SpellShoot) == CreatureCastResult.Ok)
            {
                _shootMs = (uint)(System?.RandomInt(2, 4) ?? 2) * 1000;
                return;
            }
        }
        else
        {
            _shootMs -= diffMs;
        }

        if (_multiShotMs < diffMs)
        {
            if (InShootingRange(victim) && DoCast(victim, SpellMultiShot) == CreatureCastResult.Ok)
            {
                // mangos-classic npc_kindal_moonweaver assigns urand(8, 11) to m_shootTimer here, a typo for m_multiShotTimer that leaves
                // Multi-Shot ready on every tick and starves Shoot. The Multi-Shot timer is re-armed (one draw, as in the source).
                _multiShotMs = (uint)(System?.RandomInt(8, 11) ?? 8) * 1000;
            }
        }
        else
        {
            _multiShotMs -= diffMs;
        }
    }

    /// <summary>IsInRange(victim, 5, 30, true, true): 3D, the combat reaches of both added to the limits.</summary>
    private bool InShootingRange(Unit victim)
    {
        float dx = Me.X - victim.X, dy = Me.Y - victim.Y, dz = Me.Z - victim.Z;
        float distance = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz)) - Me.GetFloat(UpdateFields.UnitFieldCombatreach) - victim.GetFloat(UpdateFields.UnitFieldCombatreach);
        return distance >= ShootMinRange && distance <= ShootMaxRange;
    }

    /// <summary>The cage door's start of the fight: her line at the raider and the attack (feralas.cpp:430-434).</summary>
    public void AttackRaider(Creature raider)
    {
        ArgumentNullException.ThrowIfNull(raider);
        System?.SayText(Me, SayInitialAggro, raider);
        AttackStart(raider);
    }
}

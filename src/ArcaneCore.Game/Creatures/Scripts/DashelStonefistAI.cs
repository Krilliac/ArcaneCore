using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Pets.Control;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Dashel Stonefist (entry 4961, Stormwind), quest 1447 "The Missing Diplomat" part 8: mangos-classic ScriptDev2
/// <c>npc_dashel_stonefist</c> as it was before the 2023 spell-list rewrite (eastern_kingdoms/stormwind_city.cpp at 3e8597afe7), whose
/// script texts (-1001274..-1001276) classic-db z2815 carries. Accepting turns him hostile; three seconds later he and two Old Town Thugs
/// attack. Under 15% he gives up (clamped there by the invincibility threshold), evades, and five seconds later the quest is done.
/// </summary>
public sealed class DashelStonefistAI(Creature creature) : CreatureAI(creature), IQuestScriptAI
{
    public const uint Entry = 4961, QuestMissingDiploPt8 = 1447, FactionHostile = 168, NpcOldTownThug = 4969;
    public const int SayStonefist1 = -1001274, SayStonefist2 = -1001275, SayStonefist3 = -1001276;

    private ObjectGuid _playerGuid;
    private uint _startEventMs, _endEventMs;

    public List<Creature> Thugs { get; } = [];

    public override void OnRespawn()
    {
        Me.FactionTemplate = Me.Template.Faction;
        ResetFlags();
    }

    /// <summary>Reset: passive, immune to players, a quest giver again; TEMPFACTION_RESTORE_COMBAT_STOP gives the faction back.</summary>
    public override void OnEvade()
    {
        Me.FactionTemplate = Me.Template.Faction;
        ResetFlags();
    }

    private void ResetFlags()
    {
        Me.ReactState = CreatureReactState.Passive;
        Me.UnitFlags |= UnitFlags.ImmuneToPlayer;
        Me.NpcFlags |= (uint)NpcFlags.QuestGiver;
        Me.InvincibilityHpThreshold = 0;
    }

    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId != QuestMissingDiploPt8)
        {
            return;
        }

        Me.FactionTemplate = FactionHostile;
        Me.UnitFlags &= ~UnitFlags.ImmuneToPlayer;
        Me.NpcFlags &= ~(uint)NpcFlags.QuestGiver;
        Me.ReactState = CreatureReactState.Aggressive;
        Me.InvincibilityHpThreshold = Math.Max(1u, (uint)((ulong)Me.MaxHealth * 15 / 100));
        System?.SayText(Me, SayStonefist1, player);
        _playerGuid = player.Guid;
        _startEventMs = 3000;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_startEventMs != 0)
        {
            if (_startEventMs <= diffMs)
            {
                _startEventMs = 0;
                if (System is { } system && system.Map.FindPlayer(_playerGuid) is { } player)
                {
                    AttackStart(player);
                    // TEMPSPAWN_DEAD_DESPAWN: the thugs stay until they die; JustSummoned's AttackStart(pPlayer).
                    foreach ((float x, float y, float z, float o) in ((float, float, float, float)[])[(-8672.33f, 442.88f, 99.98f, 3.5f), (-8691.59f, 441.66f, 99.41f, 6.1f)])
                    {
                        if (system.SummonCorpseTimedDespawn(Me, NpcOldTownThug, x, y, z, o, player, 0) is { } thug)
                        {
                            Thugs.Add(thug);
                        }
                    }
                }
            }
            else
            {
                _startEventMs -= diffMs;
            }
        }

        // DamageTaken: under 15% the hit is clamped, he says his line, ends the fight, and the quest completes five seconds later.
        if (Me.InvincibilityHpThreshold != 0 && Me.Health <= Me.InvincibilityHpThreshold && Me.Combat.IsInCombat)
        {
            System?.SayText(Me, SayStonefist2);
            _endEventMs = 5000;
            EnterEvadeMode();
            return;
        }

        if (_endEventMs != 0)
        {
            if (_endEventMs <= diffMs)
            {
                _endEventMs = 0;
                System?.SayText(Me, SayStonefist3);
                if (System?.Map.FindPlayer(_playerGuid) is { } player)
                {
                    System.QuestEventHappened(player, QuestMissingDiploPt8);
                }
            }
            else
            {
                _endEventMs -= diffMs;
            }
        }

        UpdateVictim();
    }
}

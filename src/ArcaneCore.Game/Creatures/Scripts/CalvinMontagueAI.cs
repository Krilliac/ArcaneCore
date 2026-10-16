using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets.Control;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Calvin Montague (entry 6784, Tirisfal Glades), the beat-him-up quest 590 (ScriptDev2 QUEST_590): mangos-classic ScriptDev2 <c>npc_calvin_montague</c>
/// (eastern_kingdoms/tirisfal_glades.cpp:89-168, registered :181-199). He stands friendly and immune to players (Reset, :100-107); taking the
/// quest turns him hostile (faction 168, restored when the fight stops or he respawns) and he attacks the player (:170-179). A hit that would
/// leave him under 15% is clamped (DamageTaken, :109-123): the fight stops, he goes passive, and five seconds later he says his line (-1000356),
/// 7.5 s after that the player gets the event credit and he drinks (spell 2639, triggered), and 7.5 s later he evades, which resets him.
/// Differences: the combat engine's invincibility threshold (15% of maximum health) stands in for the clamp, so a hit that would pass 15% leaves
/// him on 15% rather than just under it; the player who gets the credit is his victim at that moment, where the source stores the dealer.
/// </summary>
public sealed class CalvinMontagueAI(Creature creature) : ScriptedAI(creature), IQuestScriptAI
{
    public const uint Entry = 6784, QuestCalvin = 590, SpellDrinkSd = 2639, FactionHostile = 168;
    public const int SayComplete = -1000356;
    public const uint FirstPhaseMs = 5000, PhaseStepMs = 7500;

    private int _phase;
    private uint _phaseMs;
    private ObjectGuid _playerGuid;

    /// <summary>The scripted wind-down: 0 fighting or idle, 1 about to speak, 2 about to credit and drink, 3 about to evade.</summary>
    public int Phase => _phase;

    protected override void Reset()
    {
        _phase = 0;
        _phaseMs = FirstPhaseMs;
        _playerGuid = default;
        Me.ReactState = CreatureReactState.Aggressive;
        Me.UnitFlags |= UnitFlags.ImmuneToPlayer;
        // DamageTaken's 15% rule applies to every hit, so the threshold stands from the start (a friendly, immune Calvin is never hit by players).
        Me.InvincibilityHpThreshold = Math.Max(1u, (uint)((ulong)Me.MaxHealth * 15 / 100));
    }

    /// <summary>QuestAccept_npc_calvin_montague (:170-179).</summary>
    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId != QuestCalvin)
        {
            return;
        }

        Me.UnitFlags &= ~UnitFlags.ImmuneToPlayer;
        Me.SetFactionTemporary(FactionHostile, TemporaryFactionFlags.RestoreCombatStop | TemporaryFactionFlags.RestoreRespawn);
        AttackStart(player);
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_phase == 0 && Me.InvincibilityHpThreshold != 0 && Me.Health <= Me.InvincibilityHpThreshold && Me.Combat.IsInCombat)
        {
            GiveUp();
        }

        if (_phase != 0)
        {
            // The source fires when the timer is below the step and otherwise counts it down and returns (:127-134).
            if (_phaseMs >= diffMs)
            {
                _phaseMs -= diffMs;
                return;
            }

            _phaseMs = PhaseStepMs;
            switch (_phase)
            {
                case 1:
                    System?.SayText(Me, SayComplete);
                    _phase++;
                    break;
                case 2:
                    if (System?.Map.FindPlayer(_playerGuid) is { } player)
                    {
                        System.QuestEventHappened(player, QuestCalvin);
                    }

                    System?.CastSpell(Me, SpellDrinkSd, Me, triggered: true);
                    _phase++;
                    break;
                case 3:
                    EnterEvadeMode();
                    break;
            }

            return;
        }

        UpdateVictim();
    }

    /// <summary>DamageTaken's clamp branch (:109-123): CombatStop(true) gives the faction back, he goes passive, the dealer is remembered.</summary>
    private void GiveUp()
    {
        Player? dealer = Victim as Player;
        System?.StopCombatInPlace(Me);
        Me.RestoreTemporaryFactionAt(TemporaryFactionFlags.RestoreCombatStop);
        Me.ReactState = CreatureReactState.Passive;
        _phase = 1;
        if (dealer is not null)
        {
            _playerGuid = dealer.Guid;
        }
    }
}

using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Tapoke "Slim" Jahn (entry 4962, Wetlands), "The Missing Diplomat" part 11 (quest 1249): mangos-classic ScriptDev2
/// <c>npc_tapoke_slim_jahn</c> (eastern_kingdoms/wetlands.cpp at 3e8597afe7). Accepting the quest from Mikhail sends him walking out of the
/// inn stealthed; at point 3 he turns hostile (faction 168), and his first aggro calls a friend (spell 16457). Below 20% he gives up: the escort
/// pauses, he evades, says his two lines 4 and 7 s apart, the group gets credit and he despawns (respawning 2 s later at the inn). Reaching
/// point 7 means he escaped and the quest fails.
/// Differences: the dialogue starts on the evade rather than when he gets back to the combat start point, and the friend despawn on defeat
/// (FindGuardianWithEntry) is done for any summoned 4971 nearby.
/// </summary>
public sealed class TapokeSlimJahnAI(Creature creature) : EscortAI(creature)
{
    public const uint Entry = 4962, QuestMissingDiplomat = 1249, FactionEnemy = 168, SpellStealth = 1785, SpellCallFriends = 16457, NpcSlimsFriend = 4971;
    public const int SayAggro = -1000977, SayDefeat = -1000978, SayFriendDefeat = -1000979, SayNotes = -1000980;

    private bool _friendSummoned, _eventComplete;
    private int _dialogueStep = -1;
    private uint _dialogueMs;

    public bool EventComplete => _eventComplete;

    protected override void Reset()
    {
        Me.UnitFlags |= UnitFlags.ImmuneToPlayer;
        Me.ReactState = CreatureReactState.Passive;
        if (!HasEscortState(EscortState.Escorting))
        {
            _friendSummoned = false;
            _eventComplete = false;
            _dialogueStep = -1;
            Me.InvincibilityHpThreshold = 0;
        }
    }

    /// <summary>QuestAccept_npc_mikhail: the AI_EVENT_START_ESCORT.</summary>
    public void StartFromMikhail(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        Start(run: false, player: player, questId: QuestMissingDiplomat);
        // DamageTaken: below 20% (or a killing blow) the damage is clamped and he gives up.
        Me.InvincibilityHpThreshold = Math.Max(1u, (uint)((ulong)Me.MaxHealth * 20 / 100));
    }

    protected override void WaypointReached(uint pointId)
    {
        switch (pointId)
        {
            case 3:
                SetRun(true);
                System?.RemoveAuras(Me, SpellStealth);
                Me.FactionTemplate = FactionEnemy;
                Me.UnitFlags &= ~UnitFlags.ImmuneToPlayer;
                Me.ReactState = CreatureReactState.Aggressive;
                break;
            case 7:
                // He escaped.
                if (GetPlayerForEscort() is { } player)
                {
                    System?.QuestFailed(player, QuestMissingDiplomat);
                }

                break;
        }
    }

    protected override void Aggro(Unit target)
    {
        if (HasEscortState(EscortState.Escorting) && !_friendSummoned && DoCast(Me, SpellCallFriends) == CreatureCastResult.Ok)
        {
            System?.SayText(Me, SayAggro);
            _friendSummoned = true;
        }
    }

    public override void OnJustSummoned(Creature summoned)
    {
        ArgumentNullException.ThrowIfNull(summoned);
        if (GetPlayerForEscort() is { } player)
        {
            System?.AttackStart(summoned, player);
        }

        base.OnJustSummoned(summoned);
    }

    private void GiveUp()
    {
        if (System is { } system)
        {
            foreach (Creature friend in system.CreaturesOfEntryInRange(Me, NpcSlimsFriend, 40f).Where(c => c.IsAlive))
            {
                system.SayText(friend, SayFriendDefeat);
                system.ForcedDespawn(friend, 1000);
            }
        }

        Me.InvincibilityHpThreshold = 0;
        _eventComplete = true;
        SetEscortPaused(true);
        EnterEvadeMode();
        if (GetPlayerForEscort() is { } player)
        {
            Me.Orientation = MathF.Atan2(player.Y - Me.Y, player.X - Me.X);
        }

        _dialogueStep = 0;
        System?.SayText(Me, SayDefeat);
        _dialogueMs = 4000;
    }

    protected override void UpdateEscortAI(uint diffMs)
    {
        if (!_eventComplete && HasEscortState(EscortState.Escorting) && Me.InvincibilityHpThreshold != 0 && Me.Health <= Me.InvincibilityHpThreshold)
        {
            GiveUp();
            return;
        }

        if (_dialogueStep >= 0)
        {
            if (_dialogueMs > diffMs)
            {
                _dialogueMs -= diffMs;
            }
            else if (_dialogueStep == 0)
            {
                System?.SayText(Me, SayNotes);
                _dialogueStep = 1;
                _dialogueMs = 7000;
            }
            else
            {
                _dialogueStep = -1;
                if (GetPlayerForEscort() is { } player)
                {
                    System?.RewardGroupEventExplored(player, QuestMissingDiplomat, Me);
                }

                Me.RespawnDelayOverrideSeconds = 2;
                System?.ForcedDespawn(Me, 1000);
            }

            return;
        }

        UpdateVictim();
    }
}

/// <summary>QuestAccept_npc_mikhail (entry 4963): stealths Slim within 25 yards and starts his escort.</summary>
public sealed class MikhailAI(Creature creature) : CreatureAI(creature), IQuestScriptAI
{
    public const uint Entry = 4963;

    public void OnQuestAccept(Player player, uint questId)
    {
        if (questId != TapokeSlimJahnAI.QuestMissingDiplomat || System is not { } system)
        {
            return;
        }

        if (system.CreaturesOfEntryInRange(Me, TapokeSlimJahnAI.Entry, 25f).FirstOrDefault(c => c.IsAlive) is { AI: TapokeSlimJahnAI slim } slimCreature)
        {
            system.CastSpell(slimCreature, TapokeSlimJahnAI.SpellStealth, slimCreature, triggered: true);
            slim.StartFromMikhail(player);
        }
    }
}

using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Npc;

namespace ArcaneCore.Game.Instances.Scripts.BlackrockDepths;

/// <summary>
/// npc_marshal_windsor (entry 9023, Blackrock Depths): the "Jail Break!" (quest 4322) escort, from the behaviour spec
/// 230-npc_marshal_windsor.md (the mangos-classic flow, which matches the fixture's script_waypoint path 0 of 58 points, quest and texts).
/// Accepting the quest gives him the escort faction and starts his walk linked to that player. At the cells of Dughal Stormwing (point 7),
/// Jaz and Ograbisi (30), Shill Dinger (35), Crest Killer (45) and Tobias Seecher (49) he pauses until the instance's
/// <see cref="BlackrockDepthsInstance.TypeQuestJailBreak"/> reads SPECIAL (a gossip release or a prisoner's death), at point 21 he becomes
/// Marshal Reginald Windsor (9682) in place, and at point 58 the quest is done for the player's group. The player-linked escort failure on his
/// death is the engine's (<see cref="EscortAI.OnDeath"/>). Owned by his creature, world thread.
/// </summary>
public sealed class MarshalWindsorAI(Creature creature, BlackrockDepthsInstance instance) : EscortAI(creature), IQuestScriptAI
{
    public const uint Entry = 9023;

    /// <summary>"Marshal Reginald Windsor": the template he takes at point 21 (spec section 1).</summary>
    public const uint EntryReginald = 9682;

    public const uint QuestJailBreak = 4322;

    /// <summary>FACTION_ESCORT_N_NEUTRAL_ACTIVE (spec section 2).</summary>
    public const uint FactionEscort = 231;

    /// <summary>The aura he gains at point 22 (spec section 5), applied triggered and only when absent.</summary>
    public const uint SpellReginaldAura = 15167;

    public const int SayAggroPrison1 = -1230011, SayAggroPrison2 = -1230012, SayAggroPrison3 = -1230013;
    public const int SayStart = -1230014, SayDughal1 = -1230015, SayDughal2 = -1230016;
    public const int SayEquipment1 = -1230017, SayEquipment2 = -1230018, SayEquipment3 = -1230019, SayEquipment4 = -1230020;
    public const int SayJaz1 = -1230021, SayJaz2 = -1230022, SayShill1 = -1230023, SayShill2 = -1230024, SayShill3 = -1230025;
    public const int SayCrest1 = -1230026, SayCrest2 = -1230027, SayTobias1 = -1230028, SayTobias2 = -1230029;
    public const int SayFree1 = -1230030, SayFree2 = -1230031;

    private int _counter;

    /// <summary>
    /// The event-phase counter (spec section 4): +1 at every pause point, reset to 0 by Reset only while he is not escorting. 1 is Dughal's cell,
    /// 2 Jaz's (3 once the first release came), 4 Shill's, 5 Crest's, 6 Tobias's.
    /// </summary>
    public int Counter => _counter;

    /// <summary>Spec section 7: "Reset outside an escort: the counter goes to 0."</summary>
    protected override void Reset()
    {
        if (!HasEscortState(EscortState.Escorting))
        {
            _counter = 0;
        }
    }

    /// <summary>
    /// Spec section 3: on aggro one of three texts at random, all equally likely. -1230011 and -1230013 name the attacker, -1230012 does not.
    /// </summary>
    protected override void Aggro(Unit target)
    {
        switch (System?.RandomInt(0, 2) ?? 0)
        {
            case 0:
                System?.SayText(Me, SayAggroPrison1, target);
                break;
            case 1:
                System?.SayText(Me, SayAggroPrison2);
                break;
            default:
                System?.SayText(Me, SayAggroPrison3, target);
                break;
        }
    }

    /// <summary>
    /// QuestAccept (spec section 2): the temporary escort faction (restored on respawn by <see cref="EscortAI"/>), then the escort on his
    /// script_waypoint path, walking, linked to the player and quest 4322.
    /// </summary>
    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId != QuestJailBreak)
        {
            return;
        }

        Me.FactionTemplate = FactionEscort;
        Start(run: false, player: player, questId: QuestJailBreak);
    }

    /// <summary>
    /// UpdateEscortAI (spec section 4): when the Jail Break reads SPECIAL, the counter says what to do (1, 3, 4, 5 and 6 resume the escort, 2 is
    /// the Jaz cell that needs a second release and becomes 3), and the data goes back to IN_PROGRESS whatever the counter was. Then he fights
    /// what he is fighting.
    /// </summary>
    /// <remarks>
    /// Ambiguity (spec section 11, item 2): a prisoner killed before Windsor reaches its cell still sets SPECIAL, and this update consumes it
    /// without resuming anything; the later pause at that cell then waits for a SPECIAL that never comes. That is the reference behaviour (a
    /// latent bug there); the spec recommends keeping it, so it is kept and documented by a test.
    /// </remarks>
    protected override void UpdateEscortAI(uint diffMs)
    {
        if (instance.GetData(BlackrockDepthsInstance.TypeQuestJailBreak) == EncounterState.Special)
        {
            switch (_counter)
            {
                case 1:
                case 3:
                case 4:
                case 5:
                case 6:
                    SetEscortPaused(false);
                    break;
                case 2:
                    _counter = 3;
                    break;
            }

            instance.SetData(BlackrockDepthsInstance.TypeQuestJailBreak, EncounterState.InProgress);
        }

        base.UpdateEscortAI(diffMs);
    }

    protected override void WaypointReached(uint pointId)
    {
        switch (pointId)
        {
            case 1:
                instance.SetData(BlackrockDepthsInstance.TypeQuestJailBreak, EncounterState.InProgress);
                Say(SayStart);
                break;
            case 7:
                Say(SayDughal1, GetPlayerForEscort());
                OfferGossip(BlackrockDepthsInstance.NpcDughalStormwing);
                Pause();
                break;
            case 9:
                Say(SayDughal2, GetPlayerForEscort());
                break;
            case 14:
                Say(SayEquipment1, GetPlayerForEscort());
                break;
            case 16:
                instance.UseJailBreakObject(BlackrockDepthsInstance.GoSupplyRoomDoor);
                break;
            case 18:
                Say(SayEquipment2);
                break;
            case 20:
                instance.UseSupplyCrate(Me);
                break;
            case 21:
                // The AI and the escort state survive the change of template (CreatureMapSystem.UpdateEntry keeps the AI).
                System?.UpdateEntry(Me, EntryReginald);
                break;
            case 22:
                if (GetPlayerForEscort() is { } player)
                {
                    if (System?.HasAura(Me, SpellReginaldAura) != true)
                    {
                        DoCast(Me, SpellReginaldAura, triggered: true);
                    }

                    Say(SayEquipment3, player);
                    Face(player);
                }

                break;
            case 23:
                Say(SayEquipment4);
                if (GetPlayerForEscort() is { } escorted)
                {
                    Face(escorted);
                }

                break;
            case 30:
                Face(instance.JailBreakCreature(BlackrockDepthsInstance.NpcJaz));
                Say(SayJaz1);
                Pause();
                break;
            case 32:
                Say(SayJaz2);
                break;
            case 35:
                Face(instance.JailBreakCreature(BlackrockDepthsInstance.NpcShillDinger));
                Say(SayShill1);
                Pause();
                break;
            case 37:
                Say(SayShill2);
                break;
            case 38:
                Say(SayShill3);
                break;
            case 45:
                Face(instance.JailBreakCreature(BlackrockDepthsInstance.NpcCrestKiller));
                Say(SayCrest1);
                Pause();
                break;
            case 47:
                Say(SayCrest2);
                break;
            case 49:
                Say(SayTobias1);
                OfferGossip(BlackrockDepthsInstance.NpcTobiasSeecher);
                Pause();
                break;
            case 51:
                Say(SayTobias2, GetPlayerForEscort());
                break;
            case 57:
                Say(SayFree1);
                if (GetPlayerForEscort() is { } finisher)
                {
                    Face(finisher);
                }

                break;
            case 58:
                Say(SayFree2);
                instance.SetData(BlackrockDepthsInstance.TypeQuestJailBreak, EncounterState.Done);
                if (GetPlayerForEscort() is { } rewarded)
                {
                    // RewardPlayerAndGroupAtEventExplored: the player and every grouped member in range (spec section 11, item 4).
                    System?.RewardGroupEventExplored(rewarded, QuestJailBreak, Me);
                }

                break;
        }
    }

    /// <summary>The "counter + 1, pause" of the table (points 7, 30, 35, 45 and 49): the escort waits for the next release.</summary>
    private void Pause()
    {
        _counter++;
        SetEscortPaused(true);
    }

    /// <summary>"Give X the gossip NPC flag" (points 7 and 49): the prisoner can now be talked to.</summary>
    private void OfferGossip(uint prisoner)
    {
        if (instance.JailBreakCreature(prisoner) is { } who)
        {
            who.NpcFlags |= (uint)NpcFlags.Gossip;
            Face(who);
        }
    }

    private void Say(int textId, Unit? target = null) => System?.SayText(Me, textId, target);

    private void Face(WorldObject? at)
    {
        if (at is not null)
        {
            System?.SetFacingTo(Me, MathF.Atan2(at.Y - Me.Y, at.X - Me.X));
        }
    }
}

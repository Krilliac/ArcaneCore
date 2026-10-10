using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// "Stinky" Ignatz (entry 4880, Dustwallow Marsh), "Stinky's Escape" (quest 1222 Alliance / 1270 Horde): mangos-classic ScriptDev2
/// <c>npc_stinky_ignatz</c> (kalimdor/dustwallow_marsh.cpp at 3e8597afe7). A defensive escort on faction 113 with lines at 6, 11, 25-27,
/// 31 and 41. He faces the nearest Bogbean Plant at 26, works it from 30 and uses it as he leaves (WaypointStart 31). Point 40 credits the
/// player's team quest.
/// </summary>
public sealed class StinkyIgnatzAI(Creature creature) : EscortAI(creature), IQuestScriptAI
{
    public const uint Entry = 4880, QuestAlliance = 1222, QuestHorde = 1270, FactionEscortNeutralPassive = 113, GoBogbeanPlant = 20939;
    public const int SayBegin = -1000958, SayFirstStop = -1000959, SaySecondStop = -1001141, SayThirdStop1 = -1001142,
        SayThirdStop2 = -1001143, SayThirdStop3 = -1001144, SayPlantGathered = -1001145, SayEnd = -1000962, SayEndEmote = -1010032;
    private static readonly int[] s_aggro = [-1000960, -1000961, -1001146, -1001147];
    private const uint EmoteOneshotNone = 0, EmoteStateUseStanding = 69;

    private GameObject? _plant;

    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId is not (QuestAlliance or QuestHorde))
        {
            return;
        }

        Me.ReactState = CreatureReactState.Defensive;
        System?.SayText(Me, SayBegin);
        Start(run: false, player: player, questId: questId);
        Me.FactionTemplate = FactionEscortNeutralPassive;
        Me.StandState = StandState.Stand;
    }

    protected override void Aggro(Unit target)
    {
        if (System is not { } system)
        {
            return;
        }

        int pick = system.RandomInt(0, 3);
        system.SayText(Me, s_aggro[pick], pick == 3 ? target : null);
    }

    protected override void WaypointReached(uint pointId)
    {
        if (System is not { } system)
        {
            return;
        }

        switch (pointId)
        {
            case 6:
                system.SayText(Me, SayFirstStop);
                break;
            case 11:
                system.SayText(Me, SaySecondStop);
                break;
            case 25:
                system.SayText(Me, SayThirdStop1);
                break;
            case 26:
                system.SayText(Me, SayThirdStop2);
                _plant = Me.Map?.FindUpdater<GameObjectMapSystem>()?.GameObjects.Where(g => g.Entry == GoBogbeanPlant)
                    .OrderBy(g => ((g.X - Me.X) * (g.X - Me.X)) + ((g.Y - Me.Y) * (g.Y - Me.Y))).FirstOrDefault();
                if (_plant is not null)
                {
                    Me.Orientation = MathF.Atan2(_plant.Y - Me.Y, _plant.X - Me.X);
                }

                break;
            case 27:
                if (GetPlayerForEscort() is { } player27)
                {
                    system.SayText(Me, SayThirdStop3, player27);
                }

                break;
            case 30:
                system.PlayEmote(Me, EmoteStateUseStanding);
                // WaypointStart(31): he uses the plant as he sets off.
                if (_plant is not null)
                {
                    Me.Map?.FindUpdater<GameObjectMapSystem>()?.UseByUnit(Me, _plant);
                    _plant = null;
                }

                break;
            case 31:
                system.PlayEmote(Me, EmoteOneshotNone);
                system.SayText(Me, SayPlantGathered);
                break;
            case 40:
                if (GetPlayerForEscort() is { } player)
                {
                    system.RewardGroupEventExplored(player, player.Team == Team.Alliance ? QuestAlliance : QuestHorde, Me);
                    system.SayText(Me, SayEnd, player);
                }

                break;
            case 41:
                system.SayText(Me, SayEndEmote);
                break;
        }
    }
}

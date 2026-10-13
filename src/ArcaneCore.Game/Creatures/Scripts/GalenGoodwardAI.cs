using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Galen Goodward (entry 5391, Swamp of Sorrows), "Galen's Escape" (quest 1393): mangos-classic ScriptDev2 <c>npc_galen_goodward</c>
/// (eastern_kingdoms/swamp_of_sorrows.cpp). Caged, he calls for help every 6 s. Taking the quest starts the escort (escort faction 232, immune
/// to NPCs cleared) and opens his cage (37118), which closes again at point 1. He runs from 20, gives the credit and whispers at 21, fades at
/// 22 and despawns at 23. When attacked during the escort he says one of two lines.
/// </summary>
public sealed class GalenGoodwardAI(Creature creature) : EscortAI(creature), IQuestScriptAI
{
    public const uint Entry = 5391, QuestGalensEscape = 1393, GoGalensCage = 37118, FactionEscortNeutralActive = 232;
    public const int SayPeriodic = -1000582, SayQuestAccepted = -1000583, SayAttacked1 = -1000584, SayAttacked2 = -1000585,
        SayQuestComplete = -1000586, EmoteWhisper = -1000587, EmoteDisappear = -1000588;
    public const uint PeriodicSayMs = 6_000;
    private const float InteractionDistance = 5f;

    private uint _periodicMs = PeriodicSayMs;
    private GameObject? _cage;

    protected override void Reset() => _periodicMs = PeriodicSayMs;

    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId != QuestGalensEscape)
        {
            return;
        }

        Start(run: false, player: player, questId: questId);
        Me.FactionTemplate = FactionEscortNeutralActive;
        Me.UnitFlags &= ~UnitFlags.ImmuneToNpc;
        System?.SayText(Me, SayQuestAccepted);
    }

    protected override void WaypointStart(uint pointId)
    {
        if (pointId != 1 || System?.Map.FindUpdater<GameObjectMapSystem>() is not { } objects)
        {
            return;
        }

        _cage ??= objects.GameObjects.Where(g => g.Entry == GoGalensCage && Dist(g.X, g.Y, g.Z) <= InteractionDistance * InteractionDistance)
            .OrderBy(g => Dist(g.X, g.Y, g.Z)).FirstOrDefault();
        if (_cage is not null)
        {
            objects.ToggleDoorOrButton(_cage);
        }
    }

    protected override void WaypointReached(uint pointId)
    {
        switch (pointId)
        {
            case 1:
                if (_cage is not null && System?.Map.FindUpdater<GameObjectMapSystem>() is { } objects)
                {
                    objects.ToggleDoorOrButton(_cage); // ResetDoorOrButton
                }

                break;
            case 20:
                SetRun(true);
                break;
            case 21:
                if (GetPlayerForEscort() is { } player && System is { } system)
                {
                    system.SayText(Me, SayQuestComplete, player);
                    system.SayText(Me, EmoteWhisper, player);
                    system.RewardGroupEventExplored(player, QuestGalensEscape, Me);
                }

                break;
            case 22:
                System?.SayText(Me, EmoteDisappear);
                break;
            case 23:
                SetEscortPaused(true);
                System?.ForcedDespawn(Me, 1_000);
                break;
        }
    }

    protected override void Aggro(Unit target)
    {
        if (HasEscortState(EscortState.Escorting) && System is { } system)
        {
            system.SayText(Me, system.RandomInt(0, 1) == 0 ? SayAttacked1 : SayAttacked2, target);
        }
    }

    protected override void UpdateEscortAI(uint diffMs)
    {
        if (_periodicMs < diffMs)
        {
            if (!HasEscortState(EscortState.Escorting))
            {
                System?.SayText(Me, SayPeriodic);
            }

            _periodicMs = PeriodicSayMs;
        }
        else
        {
            _periodicMs -= diffMs;
        }

        UpdateVictim();
    }

    private float Dist(float x, float y, float z)
    {
        float dx = Me.X - x, dy = Me.Y - y, dz = Me.Z - z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }
}

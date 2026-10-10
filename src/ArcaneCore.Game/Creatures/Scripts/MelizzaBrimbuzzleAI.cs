using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Melizza Brimbuzzle (entry 12277, Desolace), the escort of quest 6132 "Get Me Out of Here!": mangos-classic ScriptDev2
/// <c>npc_melizza_brimbuzzle</c> before the 2022 caravan rework (kalimdor/desolace.cpp at 46a0597873), on the z2815 path (24 points).
/// Her cage (177706) opens as she starts; four Marauders come at 4 (two within 7 yd of each of two points), three Bonepaws and three
/// Wranglers at 9 (within 10 yd); at 12 she pauses one second, thanks the player and gives the credit, then runs on; at 19 she says her
/// three lines, walks on, and Hornizz (6019, the nearest on the map) says goodbye.
/// </summary>
public sealed class MelizzaBrimbuzzleAI(Creature creature) : EscortAI(creature), IQuestScriptAI
{
    public const uint Entry = 12277, QuestGetMeOutOfHere = 6132, GoMelizzasCage = 177706, NpcHornizz = 6019;
    public const uint NpcMarauder = 4659, NpcBonepaw = 4660, NpcWrangler = 4655, FactionEscortNeutralPassive = 113;
    public const int SayStart = -1000784, SayFinish = -1000785, Say1 = -1000786, Say2 = -1000787, Say3 = -1000788,
        SayHornizz1 = -1010030, SayHornizz2 = -1010031;
    private const float InteractionDistance = 5f;
    private static readonly (float X, float Y, float Z)[] s_marauderSpawn = [(-1291.492f, 2644.650f, 111.556f), (-1306.730f, 2675.163f, 111.561f)];
    private static readonly (float X, float Y, float Z) s_wranglerSpawn = (-1393.194f, 2429.465f, 88.689f);

    // aIntroDialogue steps: (what, delay to the next). 0-1 the quest completion, 2-8 the event end.
    private static readonly uint[] s_delays = [1000, 0, 2000, 4000, 5000, 4000, 6000, 10000, 0];
    private int _step = -1;
    private uint _stepMs;

    public List<Creature> Summoned { get; } = [];

    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId == QuestGetMeOutOfHere)
        {
            Start(run: false, player: player, questId: questId);
        }
    }

    protected override void JustStartedEscort()
    {
        if (System?.Map.FindUpdater<GameObjectMapSystem>() is { } objects
            && objects.GameObjects.Where(g => g.Entry == GoMelizzasCage && Dist(g) <= InteractionDistance).OrderBy(Dist).FirstOrDefault() is { } cage)
        {
            objects.ToggleDoorOrButton(cage);
        }
    }

    private float Dist(WorldObject o)
    {
        float dx = Me.X - o.X, dy = Me.Y - o.Y, dz = Me.Z - o.Z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    /// <summary>WorldObject::GetRandomPoint: a uniform point within <paramref name="radius"/> of the centre.</summary>
    private (float X, float Y, float Z) RandomPoint((float X, float Y, float Z) centre, float radius, CreatureMapSystem system)
    {
        float angle = system.RandomInt(0, 35_999) * (MathF.PI / 18_000f);
        float distance = radius * MathF.Sqrt(system.RandomInt(0, 10_000) / 10_000f);
        return (centre.X + (distance * MathF.Cos(angle)), centre.Y + (distance * MathF.Sin(angle)), centre.Z);
    }

    private void Summon(uint entry, (float X, float Y, float Z) at)
    {
        // TEMPSPAWN_DEAD_DESPAWN: they stay until they die.
        if (System?.SummonCorpseTimedDespawn(Me, entry, at.X, at.Y, at.Z, 0f, null, 0) is { } summoned)
        {
            Summoned.Add(summoned);
        }
    }

    protected override void WaypointReached(uint pointId)
    {
        if (System is not { } system)
        {
            return;
        }

        switch (pointId)
        {
            case 1:
                if (GetPlayerForEscort() is { } player)
                {
                    system.SayText(Me, SayStart, player);
                }

                Me.FactionTemplate = FactionEscortNeutralPassive; // TEMPFACTION_RESTORE_RESPAWN
                break;
            case 4:
                foreach ((float X, float Y, float Z) centre in s_marauderSpawn)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        Summon(NpcMarauder, RandomPoint(centre, 7f, system));
                    }
                }

                break;
            case 9:
                for (int i = 0; i < 3; i++)
                {
                    Summon(NpcBonepaw, RandomPoint(s_wranglerSpawn, 10f, system));
                    Summon(NpcWrangler, RandomPoint(s_wranglerSpawn, 10f, system));
                }

                break;
            case 12:
                DoStep(0);
                break;
            case 19:
                DoStep(2);
                break;
        }
    }

    private void DoStep(int step)
    {
        _step = step;
        _stepMs = s_delays[step];
        if (System is not { } system)
        {
            return;
        }

        Creature? hornizz = system.Creatures.Where(c => c.IsAlive && c.Entry == NpcHornizz).OrderBy(Dist).FirstOrDefault();
        switch (step)
        {
            case 0: // POINT_ID_QUEST_COMPLETE
                SetEscortPaused(true);
                break;
            case 1: // QUEST_GET_ME_OUT_OF_HERE
                if (GetPlayerForEscort() is { } player)
                {
                    system.SayText(Me, SayFinish, player);
                    system.RewardGroupEventExplored(player, QuestGetMeOutOfHere, Me);
                }

                Me.FactionTemplate = Me.Template.Faction; // ClearTemporaryFaction
                SetRun(true);
                SetEscortPaused(false);
                _step = -1; // its delay is 0: the dialogue ends here
                break;
            case 2: // POINT_ID_EVENT_COMPLETE
                SetEscortPaused(true);
                system.SetFacingTo(Me, 4.71f);
                break;
            case 3:
                system.SayText(Me, Say1);
                break;
            case 4:
                system.SayText(Me, Say2);
                break;
            case 5:
                system.SayText(Me, Say3);
                break;
            case 6: // NPC_MELIZZA
                SetEscortPaused(false);
                break;
            case 7:
                if (hornizz is not null)
                {
                    system.SayText(hornizz, SayHornizz1);
                }

                break;
            case 8:
                if (hornizz is not null)
                {
                    system.SayText(hornizz, SayHornizz2);
                }

                _step = -1;
                break;
        }
    }

    protected override void UpdateEscortAI(uint diffMs)
    {
        if (_step >= 0)
        {
            if (_stepMs <= diffMs)
            {
                DoStep(_step + 1);
            }
            else
            {
                _stepMs -= diffMs;
            }
        }

        UpdateVictim();
    }
}

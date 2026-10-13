using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Miran (entry 1379, Loch Modan), "Protecting the Shipment" (quest 309): mangos-classic ScriptDev2 <c>npc_miran</c>
/// (eastern_kingdoms/loch_modan.cpp) on script path 1379. At 16 he warns of the ambush. At 17 he holds while two Dark Iron Raiders (2149,
/// corpse despawn 25 s) attack him, each with its line, and walks on with a line once both are dead. At 21 he gives the credit, stops and
/// despawns after 15 s.
/// </summary>
public sealed class MiranAI(Creature creature) : EscortAI(creature), IQuestScriptAI
{
    public const uint Entry = 1379, QuestProtectingTheShipment = 309, NpcDarkIronRaider = 2149, EscortPath = 1379;
    public const int SayMiran1 = 510, SayMiran2 = 511, SayMiran3 = 498, SayDarkIronRaider = 512;

    private static readonly (float X, float Y, float Z, float O)[] s_ambush =
    [
        (-5705.012f, -3736.6575f, 318.56738f, 0.57595f),
        (-5696.1943f, -3736.78f, 318.58145f, 2.40855f),
    ];

    private int _dwarves;

    /// <summary>The raiders still alive from the ambush.</summary>
    public int Dwarves => _dwarves;

    protected override void Reset()
    {
        if (!HasEscortState(EscortState.Escorting))
        {
            _dwarves = 0;
        }
    }

    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId == QuestProtectingTheShipment)
        {
            Start(run: false, player: player, questId: questId, waypointPath: EscortPath);
        }
    }

    protected override void WaypointReached(uint pointId)
    {
        switch (pointId)
        {
            case 16:
                System?.SayText(Me, SayMiran1);
                break;
            case 17:
                SetEscortPaused(true);
                if (System is { } system)
                {
                    foreach ((float x, float y, float z, float o) in s_ambush)
                    {
                        if (system.SummonCorpseTimedDespawn(Me, NpcDarkIronRaider, x, y, z, o, Me, 25_000) is { } raider)
                        {
                            _dwarves++;
                            system.SayText(raider, SayDarkIronRaider);
                        }
                    }
                }

                if (_dwarves == 0)
                {
                    SetEscortPaused(false); // nothing came: do not hold the escort forever
                }

                break;
            case 21:
                System?.SayText(Me, SayMiran3);
                if (GetPlayerForEscort() is { } player)
                {
                    System?.RewardGroupEventExplored(player, QuestProtectingTheShipment, Me);
                }

                SetEscortPaused(true);
                System?.ForcedDespawn(Me, 15_000);
                break;
        }
    }

    public override void OnSummonedCreatureJustDied(Creature summoned)
    {
        ArgumentNullException.ThrowIfNull(summoned);
        if (summoned.Entry == NpcDarkIronRaider && _dwarves > 0 && --_dwarves == 0)
        {
            System?.SayText(Me, SayMiran2);
            SetEscortPaused(false);
        }
    }
}

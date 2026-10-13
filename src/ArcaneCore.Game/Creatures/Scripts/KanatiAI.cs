using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Kanati (entry 10638, Thousand Needles), "Protect Kanati Greycloud" (quest 4966): mangos-classic ScriptDev2 <c>npc_kanati</c>
/// (kalimdor/thousand_needles.cpp) on script path 10638. On accept he takes the escort faction (232) and walks. At 1 he can be hit by
/// NPCs again, says his line and three Galak Assassins (10720) come for him; at 2 he is immune to NPCs again, gives the group credit and
/// takes back his own faction.
/// Difference from SD2: the assassins use the out-of-combat-or-corpse 25 s despawn (SD2: out of combat only).
/// </summary>
public sealed class KanatiAI(Creature creature) : EscortAI(creature), IQuestScriptAI
{
    public const uint Entry = 10638, QuestProtectKanati = 4966, NpcGalakAssassin = 10720, FactionEscort = 232, EscortPath = 10638;
    public const int SayStart = 6149;
    public const float GalakX = -4867.387695f, GalakY = -1357.353760f, GalakZ = -48.226f;

    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId == QuestProtectKanati)
        {
            Me.FactionTemplate = FactionEscort;
            Start(run: false, player: player, questId: questId, waypointPath: EscortPath);
        }
    }

    protected override void WaypointReached(uint pointId)
    {
        switch (pointId)
        {
            case 1:
                Me.UnitFlags &= ~UnitFlags.ImmuneToNpc;
                System?.SayText(Me, SayStart);
                SpawnGalak();
                break;
            case 2:
                Me.UnitFlags |= UnitFlags.ImmuneToNpc;
                if (GetPlayerForEscort() is { } player)
                {
                    System?.RewardGroupEventExplored(player, QuestProtectKanati, Me);
                }

                Me.FactionTemplate = Me.Template.Faction;
                break;
        }
    }

    /// <summary>DoSpawnGalak: three assassins at the ambush point, each attacking Kanati (JustSummoned → AttackStart).</summary>
    public int SpawnGalak()
    {
        int spawned = 0;
        if (System is { } system)
        {
            for (int i = 0; i < 3; i++)
            {
                if (system.SummonAt(Me, NpcGalakAssassin, GalakX, GalakY, GalakZ, 0f, Me, 25_000, oocOrCorpse: true) is not null)
                {
                    spawned++;
                }
            }
        }

        return spawned;
    }
}

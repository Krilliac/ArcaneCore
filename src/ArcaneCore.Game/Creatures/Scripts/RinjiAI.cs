using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Rinji (entry 7780, The Hinterlands), the escort of quest 2742 "Rin'ji is Trapped!": mangos-classic ScriptDev2 <c>npc_rinji</c>
/// (AI/ScriptDevAI/scripts/eastern_kingdoms/hinterlands.cpp) on the classic-db z2815 script_waypoint path. Taking the quest opens her cage
/// (142036); a Highvale Ranger and two Outrunners ambush at points 8 and 14, credit at 18, then two closing lines three seconds apart.
/// </summary>
public sealed class RinjiAI(Creature creature) : EscortAI(creature), IQuestScriptAI
{
    public const uint Entry = 7780, QuestRinjiTrapped = 2742, NpcRanger = 2694, NpcOutrunner = 2691, GoRinjiCage = 142036;
    public const int SayFree = -1000403, SayByOutrunner = -1000404, SayHelp1 = -1000405, SayHelp2 = -1000406, SayComplete = -1000407,
        SayProgress1 = -1000408, SayProgress2 = -1000409;
    private const float InteractionDistance = 5f;

    private static readonly (float X, float Y, float Z)[] s_ambushSpawn = [(191.29620f, -2839.329346f, 107.388f), (70.972466f, -2848.674805f, 109.459f)];
    private static readonly (float X, float Y, float Z)[] s_ambushMoveTo = [(166.63038f, -2824.780273f, 108.153f), (70.886589f, -2874.335449f, 116.675f)];

    private bool _byOutrunner;
    private int _spawnId;
    private uint _postEventCount, _postEventMs = 3000;

    public List<Creature> Summoned { get; } = [];

    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId != QuestRinjiTrapped)
        {
            return;
        }

        // GetClosestGameObjectWithEntry(pCreature, GO_RINJI_CAGE, INTERACTION_DISTANCE)->UseDoorOrButton()
        if (System?.Map.FindUpdater<GameObjectMapSystem>() is { } objects)
        {
            GameObject? cage = objects.GameObjects.Where(g => g.Entry == GoRinjiCage && Distance(g.X, g.Y, g.Z) <= InteractionDistance)
                .OrderBy(g => Distance(g.X, g.Y, g.Z)).FirstOrDefault();
            if (cage is not null)
            {
                objects.ToggleDoorOrButton(cage);
            }
        }

        Start(run: false, player: player, questId: questId);
        Me.UnitFlags &= ~UnitFlags.ImmuneToNpc;
    }

    private float Distance(float x, float y, float z)
    {
        float dx = Me.X - x, dy = Me.Y - y, dz = Me.Z - z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    protected override void Reset()
    {
        _postEventCount = 0;
        _postEventMs = 3000;
    }

    protected override void JustRespawned()
    {
        _byOutrunner = false;
        _spawnId = 0;
        base.JustRespawned();
    }

    protected override void Aggro(Unit target)
    {
        if (!HasEscortState(EscortState.Escorting) || System is not { } system)
        {
            return;
        }

        if (target is Creature { Entry: NpcOutrunner } outrunner && !_byOutrunner)
        {
            system.SayText(outrunner, SayByOutrunner);
            _byOutrunner = true;
        }

        if (system.RandomInt(0, 3) != 0)
        {
            return;
        }

        system.SayText(Me, system.RandomInt(0, 1) != 0 ? SayHelp1 : SayHelp2);
    }

    private void SpawnAmbush(bool first)
    {
        if (!first)
        {
            _spawnId = 1;
        }

        if (System is not { } system)
        {
            return;
        }

        (float x, float y, float z) = s_ambushSpawn[_spawnId];
        foreach (uint entry in (uint[])[NpcRanger, NpcOutrunner, NpcOutrunner])
        {
            // TEMPSPAWN_TIMED_OOC_OR_CORPSE_DESPAWN, 60000; JustSummoned: SetWalk(false), MovePoint to the ambush target.
            if (system.SummonAt(Me, entry, x, y, z, 0f, null, 60_000, oocOrCorpse: true) is { } summoned)
            {
                Summoned.Add(summoned);
                SetRun(true);
                (float mx, float my, float mz) = s_ambushMoveTo[_spawnId];
                system.MoveTo(summoned, mx, my, mz, run: false, finalOrientation: null);
            }
        }
    }

    protected override void WaypointReached(uint pointId)
    {
        if (GetPlayerForEscort() is not { } player)
        {
            return;
        }

        switch (pointId)
        {
            case 2:
                System?.SayText(Me, SayFree, player);
                break;
            case 8:
                SpawnAmbush(true);
                break;
            case 14:
                SpawnAmbush(false);
                break;
            case 18:
                System?.SayText(Me, SayComplete, player);
                System?.RewardGroupEventExplored(player, QuestRinjiTrapped, Me);
                SetRun(true);
                _postEventCount = 1;
                break;
        }
    }

    protected override void UpdateEscortAI(uint diffMs)
    {
        if (UpdateVictim() && Victim is not null)
        {
            return;
        }

        if (!HasEscortState(EscortState.Escorting) || _postEventCount == 0)
        {
            return;
        }

        if (_postEventMs >= diffMs)
        {
            _postEventMs -= diffMs;
            return;
        }

        _postEventMs = 3000;
        if (GetPlayerForEscort() is not { } player)
        {
            System?.ForcedDespawn(Me, 0);
            return;
        }

        System?.SayText(Me, _postEventCount == 1 ? SayProgress1 : SayProgress2, player);
        _postEventCount = _postEventCount == 1 ? 2u : 0u;
    }
}

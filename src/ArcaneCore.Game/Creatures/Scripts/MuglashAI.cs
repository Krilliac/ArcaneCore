using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Muglash (entry 12717, Ashenvale), the escort of quest 6641 "Vorsha the Lasher": mangos-classic ScriptDev2 <c>npc_muglash</c> and
/// <c>go_naga_brazier</c> as they were before the 2025 waypoint_path rewrite (kalimdor/ashenvale.cpp at e27966cec7), which is the version
/// whose script texts and script_waypoint path (30 points) classic-db z2815 carries. At point 25 he waits at the Naga Brazier (178247);
/// once a player puts it out, two naga waves and Vorsha come ten seconds apart (out of combat), then he walks on to the credit at 26.
/// </summary>
public sealed class MuglashAI(Creature creature) : EscortAI(creature), IQuestScriptAI
{
    public const uint Entry = 12717, QuestVorsha = 6641, GoNagaBrazier = 178247, NpcVorsha = 12940;
    public const uint FactionEscortHordePassive = 775;
    public const int SayStart1 = -1000501, SayStart2 = -1000502, SayBrazier = -1000503, SayBrazierWait = -1000504, SayOnGuard = -1000505,
        SayDone = -1000507, SayGratitude = -1000508, SayPatrol = -1000509, SayReturn = -1000510;
    private const float InteractionDistance = 5f;

    private static readonly (uint Entry, float X, float Y, float Z)[] s_firstWave =
        [(3713, 3603.504150f, 1122.631104f, 1.635f), (3717, 3589.293945f, 1148.664063f, 5.565f), (3712, 3609.925537f, 1168.759521f, -1.168f)];

    // The source's coordinate table is witch, priest, myrmidon, but it summons priestess, myrmidon, seawitch at those three points.
    private static readonly (uint Entry, float X, float Y, float Z)[] s_secondWave =
        [(3944, 3609.925537f, 1168.759521f, -1.168f), (3711, 3645.652100f, 1139.425415f, 1.322f), (3715, 3583.602051f, 1128.405762f, 2.347f)];

    private uint _waveId, _eventMs = 10_000;

    /// <summary>m_bIsBrazierExtinguished.</summary>
    public bool BrazierExtinguished { get; private set; }

    public List<Creature> Summoned { get; } = [];

    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId != QuestVorsha)
        {
            return;
        }

        System?.SayText(Me, SayStart1);
        Me.FactionTemplate = FactionEscortHordePassive;
        Me.UnitFlags &= ~UnitFlags.ImmuneToNpc;
        Start(run: false, player: player, questId: questId);
    }

    /// <summary>The brazier script lives with the escort: it is registered on the map's objects when Muglash is placed.</summary>
    protected override void JustSpawned() => RegisterBrazier();

    private void RegisterBrazier() => System?.Map.FindUpdater<GameObjectMapSystem>()?.RegisterAi(GoNagaBrazier, new NagaBrazierAi());

    protected override void Reset()
    {
        _eventMs = 10_000;
        if (!HasEscortState(EscortState.Escorting))
        {
            _waveId = 0;
            BrazierExtinguished = false;
        }
    }

    protected override void Aggro(Unit target)
    {
        if (!HasEscortState(EscortState.Paused) || System is not { } system || system.RandomInt(0, 1) != 0)
        {
            return;
        }

        if (GetPlayerForEscort() is { } player)
        {
            system.SayText(Me, SayOnGuard, player);
        }
    }

    protected override void WaypointReached(uint pointId)
    {
        switch (pointId)
        {
            case 1:
                if (GetPlayerForEscort() is { } starter)
                {
                    System?.SayText(Me, SayStart2, starter);
                }

                break;
            case 25:
                if (GetPlayerForEscort() is { } player)
                {
                    System?.SayText(Me, SayBrazier, player);
                }

                RegisterBrazier(); // the map's objects may have come up after him
                if (ClosestBrazier() is { } brazier)
                {
                    brazier.Flags &= ~GameObjectFlags.NoInteract;
                    SetEscortPaused(true);
                }

                break;
            case 26:
                System?.SayText(Me, SayGratitude);
                if (GetPlayerForEscort() is { } rewarded)
                {
                    System?.RewardGroupEventExplored(rewarded, QuestVorsha, Me);
                }

                break;
            case 27:
                System?.SayText(Me, SayPatrol);
                break;
            case 28:
                System?.SayText(Me, SayReturn);
                break;
        }
    }

    private GameObject? ClosestBrazier() => System?.Map.FindUpdater<GameObjectMapSystem>()?.GameObjects
        .Where(g => g.Entry == GoNagaBrazier && Dist(g) <= InteractionDistance * 2).OrderBy(Dist).FirstOrDefault();

    private float Dist(WorldObject o)
    {
        float dx = Me.X - o.X, dy = Me.Y - o.Y, dz = Me.Z - o.Z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    /// <summary>GOUse_go_naga_brazier: Muglash within 10 yd says his line and the waves begin; the use then goes on as usual.</summary>
    internal bool OnBrazierUsed()
    {
        System?.SayText(Me, SayBrazierWait);
        BrazierExtinguished = true;
        return true;
    }

    private void DoWaveSummon()
    {
        switch (_waveId)
        {
            case 1:
                Summon(s_firstWave);
                break;
            case 2:
                Summon(s_secondWave);
                break;
            case 3:
                Summon([(NpcVorsha, 3633.056885f, 1172.924072f, -5.388f)]);
                break;
            case 4:
                SetEscortPaused(false);
                System?.SayText(Me, SayDone);
                break;
        }
    }

    private void Summon((uint Entry, float X, float Y, float Z)[] wave)
    {
        if (System is not { } system)
        {
            return;
        }

        foreach ((uint entry, float x, float y, float z) in wave)
        {
            // TEMPSPAWN_TIMED_OOC_DESPAWN, 60000; JustSummoned: AttackStart(m_creature).
            if (system.SummonAt(Me, entry, x, y, z, 0f, Me, 60_000) is { } summoned)
            {
                Summoned.Add(summoned);
            }
        }
    }

    protected override void UpdateEscortAI(uint diffMs)
    {
        if (UpdateVictim() && Victim is not null)
        {
            return;
        }

        if (!HasEscortState(EscortState.Paused) || !BrazierExtinguished)
        {
            return;
        }

        if (_eventMs < diffMs)
        {
            ++_waveId;
            DoWaveSummon();
            _eventMs = 10_000;
        }
        else
        {
            _eventMs -= diffMs;
        }
    }

    private sealed class NagaBrazierAi : IGameObjectAi
    {
        public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;

        public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
        {
        }

        public bool OnUse(GameObjectMapSystem objects, GameObject go, Unit user)
        {
            // GetClosestCreatureWithEntry(pGo, NPC_MUGLASH, INTERACTION_DISTANCE * 2): return false (the use goes on) when he answers.
            MuglashAI? muglash = go.Map?.FindUpdater<CreatureMapSystem>()?.Creatures
                .Where(c => c.IsAlive && c.Entry == Entry && Within(go, c, InteractionDistance * 2))
                .Select(c => c.AI).OfType<MuglashAI>().FirstOrDefault();
            return muglash is null || !muglash.OnBrazierUsed();
        }

        private static bool Within(WorldObject a, WorldObject b, float range)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return (dx * dx) + (dy * dy) + (dz * dz) <= range * range;
        }
    }
}

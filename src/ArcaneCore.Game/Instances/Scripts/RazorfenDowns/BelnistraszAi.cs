using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.Game.Instances.Scripts.RazorfenDowns;

/// <summary>ScriptDev2 npc_belnistraszAI (mangos-classic razorfen_downs/razorfen_downs.cpp:
/// QuestAccept_npc_belnistrasz, WaypointReached, UpdateEscortAI, DoSummonSpawner, SpawnerSummon).</summary>
public sealed class BelnistraszAi(Creature creature) : EscortAI(creature)
{
    public const uint Entry = 8516, Quest = 3525, Spawner = 8611;
    public const uint Brazier = 152097;
    private static readonly uint[] IdolFires = [151951, 151952, 151973];
    private static readonly (float X, float Y, float Z, float O)[] SpawnPoints =
        [(2582.79f, 954.392f, 52.4821f, 3.78736f), (2569.42f, 956.380f, 52.2732f, 5.42797f),
         (2570.62f, 942.393f, 53.7433f, 0.71558f)];
    private Player? _escortPlayer;
    private int _ritualPhase;
    private uint _ritualMs = 1000, _fireballMs = 1000, _novaMs = 6000;
    private bool _aggroText;

    public int RitualPhase => _ritualPhase;

    public bool AcceptQuest(Player player)
    {
        if (!Start(run: true)) return false;
        Me.FactionTemplate = 250; // SetFactionTemporary(FACTION_ESCORT_N_NEUTRAL_ACTIVE, RESTORE_RESPAWN), razorfen_downs.cpp:301
        _escortPlayer = player;
        System?.SayText(Me, -1129005, player);
        return true;
    }

    protected override void Reset()
    {
        _fireballMs = 1000;
        _novaMs = 6000;
    }

    public override void OnAttackedBy(Unit attacker)
    {
        if (HasEscortState(EscortState.Paused))
        {
            if (!_aggroText)
            {
                System?.SayText(Me, Random.Shared.Next(2) == 0 ? -1129007 : -1129008, attacker);
                _aggroText = true;
            }
            return;
        }
        base.OnAttackedBy(attacker);
    }

    protected override void WaypointReached(uint pointId)
    {
        if (pointId != 24) return;
        System?.SayText(Me, -1129006);
        SetEscortPaused(true);
    }

    protected override void UpdateEscortAI(uint diffMs)
    {
        if (HasEscortState(EscortState.Paused))
        {
            if (_ritualMs >= diffMs) { _ritualMs -= diffMs; return; }
            switch (_ritualPhase++)
            {
                case 0: DoCast(Me, 12774); _ritualMs = 1000; break;
                case 1: SummonSpawner(); _ritualMs = 39000; break;
                case 2: SummonSpawner(); _ritualMs = 20000; break;
                case 3: System?.SayText(Me, -1129009, Me); _ritualMs = 20000; break;
                case 4: SummonSpawner(); _ritualMs = 40000; break;
                case 5: SummonSpawner(); System?.SayText(Me, -1129010, Me); _ritualMs = 40000; break;
                case 6: SummonSpawner(); _ritualMs = 20000; break;
                case 7: System?.SayText(Me, -1129011, Me); _ritualMs = 40000; break;
                case 8: SummonSpawner(); _ritualMs = 20000; break;
                case 9: System?.SayText(Me, -1129012, Me); _ritualMs = 3000; break;
                case 10: FinishRitual(); break;
            }
            return;
        }

        if (!UpdateVictim()) return;
        if (_fireballMs >= diffMs) _fireballMs -= diffMs;
        else { DoCast(Victim, 9053); _fireballMs = (uint)Random.Shared.Next(2000, 3001); }
        if (_novaMs >= diffMs) _novaMs -= diffMs;
        else { DoCast(Victim, 11831); _novaMs = (uint)Random.Shared.Next(10000, 15001); }
    }

    private void SummonSpawner()
    {
        if (System is not { } system || system.Content.FindTemplate(Spawner) is not { } template) return;
        var point = SpawnPoints[Random.Shared.Next(3)];
        Creature spawner = system.SpawnTemporary(template, point.X, point.Y, point.Z, point.O, Me);
        system.ForcedDespawn(spawner, 10_000);
    }

    public override void OnJustSummoned(Creature summoned)
    {
        if (summoned.Template.Entry != Spawner || System is not { } system) return;
        if (_ritualPhase > 7)
        {
            SummonOne(system, summoned, 7356, summoned.X, summoned.Y, summoned.Z);
            return;
        }

        uint[] entries = [7333, 7333, 7329, 7335];
        foreach (uint entry in entries)
        {
            double angle = Random.Shared.NextDouble() * Math.Tau;
            SummonOne(system, summoned, entry, summoned.X + (float)(2 * Math.Cos(angle)),
                summoned.Y + (float)(2 * Math.Sin(angle)), summoned.Z);
        }
    }

    private static void SummonOne(CreatureMapSystem system, Creature spawner, uint entry, float x, float y, float z)
    {
        if (system.Content.FindTemplate(entry) is not { } template) return;
        Creature mob = system.SpawnTemporary(template, x, y, z, 0, spawner);
        system.MarkTimedOutOfCombatDespawn(mob, 60_000);
    }

    private void FinishRitual()
    {
        if (_escortPlayer is { } player)
            System?.AiServices.QuestEvents?.EventHappened(player, Quest, Me, rewardGroup: true);
        if (Me.Map?.FindUpdater<GameObjectMapSystem>() is { } objects)
        {
            GameObject? brazier = objects.GameObjects.FirstOrDefault(go => go.Entry == Brazier && DistanceSq(go, Me) <= 100);
            if (brazier is { IsSpawned: false }) objects.ForceRespawn(brazier);
            foreach (GameObject fire in objects.GameObjects.Where(go => IdolFires.Contains(go.Entry) && DistanceSq(go, Me) <= 1600))
                objects.DespawnForRespawn(fire);
        }
        System?.RemoveAuras(Me, 12774);
        SetEscortPaused(false);
        DoCast(Me, 12816);
    }

    private static float DistanceSq(WorldObject a, WorldObject b)
        => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z);
}

using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Raids;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Instances.Scripts.BlackwingLair;

/// <summary>
/// mangos-classic blackwing_lair/boss_victor_nefarius.cpp boss_victor_nefariusAI
/// DoStartIntro, JustDidDialogueStep and ExecuteAction; vmangos counterpart
/// UpdateAI supplies the six-second brood and 35-second chromatic spawn cadence.
/// </summary>
public sealed class VictorNefariusAI : RaidBossAI
{
    /// <summary>vmangos blackwing_lair.h FACTION_MONSTER.</summary>
    public const uint FactionMonster = 14;

    private static readonly uint[] Spawners = [14307, 14309, 14310, 14311, 14312];
    private uint _introMs, _broodMs, _chromaticMs;
    private int _introStep;

    public VictorNefariusAI(Creature creature) : base(creature, 7)
    {
        CombatMovement = false;
        AddAction(3000, () => Cast(22677, RandomPlayer()), () => RandomDelay(2000, 4000));
        AddAction(8000, () => Cast(22678, RandomPlayer()), () => RandomDelay(10000, 20000));
        AddAction(13000, () => Cast(22665), () => RandomDelay(19000, 28000));
        AddAction(23000, () => Cast(22666, RandomPlayer()), () => RandomDelay(14000, 23000));
        AddAction(30000, () => Cast(22667, RandomPlayer()), () => RandomDelay(24000, 30000));
        AddAction(40000, () => Cast(22664), () => RandomDelay(30000, 40000));
        var raid = Instance;
        if (raid is not null)
        {
            int first = Array.IndexOf(Spawners, raid.GetData(11));
            int second = Array.IndexOf(Spawners, raid.GetData(12));
            if (first < 0)
            {
                first = PickOther(second);
                raid.SetData(11, Spawners[first]);
            }
            if (second < 0)
            {
                second = PickOther(first);
                raid.SetData(12, Spawners[second]);
            }
        }
        _broodMs = 6000;
        _chromaticMs = RandomDelay(7000, 9000);
    }

    private int PickOther(int excluded)
    {
        int index = System?.RandomInt(0, Spawners.Length - (excluded < 0 ? 1 : 2)) ?? 0;
        return excluded >= 0 && index >= excluded ? index + 1 : index;
    }

    private Player? RandomPlayer()
    {
        Player[] players = [.. Me.Combat.Threat.Entries.Select(e => e.Target).OfType<Player>().Where(p => p.IsAlive)];
        return players.Length == 0 ? null : players[System?.RandomInt(0, players.Length - 1) ?? 0];
    }

    public bool BeginIntro()
    {
        if (Instance?.GetData(7) is EncounterState.InProgress or EncounterState.Special or EncounterState.Done) return false;
        _introStep = 0; _introMs = 1000;
        return true;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_introMs > 0 && (_introMs = _introMs > diffMs ? _introMs - diffMs : 0) == 0)
        {
            switch (_introStep++)
            {
                case 0: System?.SayText(Me, 9907); _introMs = 7000; break;
                case 1: System?.SayText(Me, 9845); _introMs = 4000; break;
                case 2:
                    // vmangos UpdateAI step 3: SetFactionTemplateId(FACTION_MONSTER = 14) before the barrier and the attack.
                    Me.FactionTemplate = FactionMonster;
                    Cast(22663, triggered: true);
                    Cast(22664);
                    if (Me.Map?.Players.FirstOrDefault(p => p.IsAlive) is { } player) AttackStart(player);
                    break;
            }
        }
        base.OnUpdate(diffMs);
    }

    protected override void UpdateCombat(uint diffMs)
    {
        if (Instance?.GetData(7) != EncounterState.InProgress) return;
        base.UpdateCombat(diffMs);
        if (_broodMs > diffMs) _broodMs -= diffMs;
        else
        {
            _broodMs = RandomDelay(6000, 7000);
            SpawnBrood();
        }
        if (_chromaticMs > diffMs) _chromaticMs -= diffMs;
        else
        {
            _chromaticMs = 35000;
            // vmangos m_uiAddChromaSpawnTimer: one chromatic drakonid at each tunnel.
            SpawnDrakonid(14302, -7599.32f, -1191.72f, 475.545f);
            SpawnDrakonid(14302, -7526.27f, -1135.04f, 473.445f);
        }
    }

    private void SpawnBrood()
    {
        uint left = SpawnerToDrakonid(Instance?.GetData(11) ?? 0);
        uint right = SpawnerToDrakonid(Instance?.GetData(12) ?? 0);
        SpawnDrakonid(left, -7599.32f, -1191.72f, 475.545f);
        SpawnDrakonid(right, -7526.27f, -1135.04f, 473.445f);
    }

    private static uint SpawnerToDrakonid(uint spawner) => spawner switch
    {
        14307 => 14265, 14309 => 14264, 14310 => 14262, 14311 => 14263, 14312 => 14261, _ => 0,
    };

    private void SpawnDrakonid(uint entry, float x, float y, float z)
    {
        if (entry == 0 || System?.Content.FindTemplate(entry) is not { } template) return;
        Creature add = System.SpawnTemporary(template, x, y, z, 5);
        if (RandomPlayer() is { } target) add.AI?.AttackStart(target);
    }

    protected override void ResetActions()
    {
        base.ResetActions();
        _introMs = 0; _introStep = 0;
        _broodMs = 6000; _chromaticMs = RandomDelay(7000, 9000);
    }

    public override void OnDeath(Unit? killer) => Instance?.SetData(7, EncounterState.Fail);

    /// <summary>SD2 TEMPFACTION_RESTORE_REACH_HOME: back home after a wipe he is friendly again and the gossip can restart the event.</summary>
    public override void OnReachedHome()
    {
        Me.FactionTemplate = Me.Template.Faction;
        base.OnReachedHome();
    }
}

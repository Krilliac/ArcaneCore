using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Raids;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Instances.Scripts.BlackwingLair;

/// <summary>
/// mangos-classic blackwing_lair/boss_vaelastrasz.cpp boss_vaelastraszAI:
/// BeginIntro/HandleIntro, BeginSpeech/HandleSpeech, Aggro and ExecuteAction.
/// The area trigger and gossip select enter the two noncombat sequences.
/// Text ids are vmangos boss_vaelastrasz.cpp broadcast_text ids.
/// </summary>
public sealed class VaelastraszAI : RaidBossAI
{
    /// <summary>SD2 FACTION_HOSTILE, set by HandleSpeech with TEMPFACTION_RESTORE_RESPAWN (a respawn re-reads the template).</summary>
    public const uint FactionHostile = 14;

    private uint _introMs, _speechMs;
    private int _introStep, _speechStep;
    private bool _lowHealthLine;
    private Creature? _nefarius;

    public VaelastraszAI(Creature creature) : base(creature, 1)
    {
        creature.Health = Math.Max(1u, creature.MaxHealth * 3 / 10);
        creature.StandState = StandState.Dead;
        AddAction(45000, () => Cast(18173, Victim), () => 45000);
        AddAction(15000, () => Cast(23620, RandomManaPlayer()), () => 15000);
        AddAction(11000, () => Cast(23461, Victim), () => RandomDelay(4000, 8000));
        AddAction(5000, () => Cast(23462), () => 5000);
        AddAction(8000, () => Cast(19983, Victim), () => 15000);
        AddAction(20000, () => Cast(15847), () => 20000);
    }

    private Player? RandomManaPlayer()
    {
        Player[] targets = [.. Me.Combat.Threat.Entries.Select(e => e.Target)
            .OfType<Player>().Where(p => p.IsAlive && p.PowerType == PowerType.Mana)];
        return targets.Length == 0 ? null : targets[System?.RandomInt(0, targets.Length - 1) ?? 0];
    }

    public bool BeginIntro()
    {
        if (Instance?.GetData(1) != EncounterState.NotStarted) return false;
        Instance.SetData(1, EncounterState.Special);
        _introStep = 0; _introMs = 1000;
        return true;
    }

    public bool BeginSpeech()
    {
        if (Instance?.GetData(1) is not (EncounterState.Special or EncounterState.Fail)
            || _introMs != 0 || _speechMs != 0) return false;
        Me.StandState = StandState.Stand;
        System?.SayText(Me, 9886); // SAY_LINE_1
        _speechStep = 0; _speechMs = 10000;
        return true;
    }

    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        Cast(23513);
    }

    public override void OnRespawn()
    {
        base.OnRespawn();
        Me.Health = Math.Max(1u, Me.MaxHealth * 3 / 10);
        Me.StandState = StandState.Dead;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_introMs != 0 && (_introMs = _introMs > diffMs ? _introMs - diffMs : 0) == 0) AdvanceIntro();
        if (_speechMs != 0 && (_speechMs = _speechMs > diffMs ? _speechMs - diffMs : 0) == 0) AdvanceSpeech();
        base.OnUpdate(diffMs);
    }

    private Creature? Nefarius => _nefarius is { IsAlive: true, IsInWorld: true } nefarius ? nefarius : null;

    private void AdvanceIntro()
    {
        switch (_introStep++)
        {
            case 0:
                // SummonCreature(NPC_LORD_VICTOR_NEFARIUS, aNefariusSpawnLoc, TEMPSPAWN_TIMED_DESPAWN, 25000).
                if (System?.Content.FindTemplate(10162) is { } template)
                {
                    _nefarius = System.SpawnTemporary(template, -7466.16f, -1040.80f, 412.053f, 2.14675f, summoner: Me);
                    System.ForcedDespawn(_nefarius, 25000);
                }
                _introMs = 1000;
                break;
            case 1:
                if (Nefarius is { } corrupting)
                {
                    System?.CastSpell(corrupting, 23642, Me, triggered: true); // SPELL_NEFARIUS_CORRUPTION
                    System?.SayText(corrupting, 9794); // SAY_NEFARIUS_CORRUPT_1
                }
                _introMs = 14000;
                break;
            case 2:
                if (Nefarius is { } taunting) System?.SayText(taunting, 9844); // SAY_NEFARIUS_CORRUPT_2
                _introMs = 2000;
                break;
            case 3:
                if (Nefarius is { } lightning) System?.CastSpell(lightning, 19484, Me, triggered: false); // SPELL_RED_LIGHTNING
                System?.RemoveAuras(Me, 23642);
                break;
        }
    }

    private void AdvanceSpeech()
    {
        switch (_speechStep++)
        {
            case 0: System?.SayText(Me, 9887); _speechMs = 16000; break;
            case 1: System?.SayText(Me, 9888); _speechMs = 10000; break;
            case 2:
                Me.FactionTemplate = FactionHostile;
                if (Me.Map?.Players.FirstOrDefault(p => p.IsAlive) is { } player) AttackStart(player);
                break;
        }
    }

    protected override void UpdateCombat(uint diffMs)
    {
        base.UpdateCombat(diffMs);
        if (!_lowHealthLine && Me.Health * 100 < Me.MaxHealth * 15)
        {
            System?.SayText(Me, 9965);
            _lowHealthLine = true;
        }
    }

    protected override void ResetActions()
    {
        base.ResetActions();
        _introMs = _speechMs = 0;
        _introStep = _speechStep = 0;
        _lowHealthLine = false;
        Me.Health = Math.Max(1u, Me.MaxHealth * 3 / 10);
    }
}

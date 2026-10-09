using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;

namespace ArcaneCore.Game.Instances.Scripts.RuinsOfAhnQiraj;

/// <summary>mangos-classic ruins_of_ahnqiraj/boss_rajaxx.cpp
/// npc_general_andorovAI::JustRespawned, MovementInform, ExecuteAction and
/// GossipHello/Select_npc_general_andorov. Reaching the attack point starts the
/// dialogue, whose last line sends the first Rajaxx wave.</summary>
public sealed class AndorovAI(Creature creature) : CreatureAI(creature), INpcGossipScript
{
    private static readonly (float X, float Y, float Z)[] Path =
    [
        (-8701.51f, 1561.80f, 32.092f), (-8718.66f, 1577.69f, 21.612f),
        (-8876.97f, 1651.96f, 21.57f), (-8882.15f, 1602.77f, 21.386f),
        (-8940.45f, 1550.69f, 21.616f)
    ];
    private static readonly (int Text, uint Delay)[] Dialogue =
    [(-1509004, 7000), (-1509031, 0), (-1509003, 4000), (-1509029, 6000), (-1509030, 0)];
    private int _point;
    private int _dialogueStep = -1;
    private uint _dialogueMs;
    private uint _commandMs = 1000, _bashMs = 8000, _strikeMs = 2000;
    private RuinsOfAhnQirajInstance? Raid => Me.Map?.FindUpdater<InstanceData>() as RuinsOfAhnQirajInstance;

    public override void OnRespawn()
    {
        _point = 0;
        _dialogueStep = -1;
        _commandMs = 1000;
        _bashMs = 8000;
        _strikeMs = 2000;
        Me.Motion.MovePoint(0, Path[0].X, Path[0].Y, Path[0].Z, run: false);
    }
    public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
    {
        if (npc.Entry != 15471 || Raid?.GetData(0) != EncounterState.Done) return null;
        ScriptedGossipItem[] items = Raid.GetData(1) is EncounterState.NotStarted or EncounterState.Fail
            ? [new(0, "Let's find out.", 1, 1001)] : [];
        return new(false, 7883, items);
    }
    public uint Select(Player player, NpcInfo npc, uint sender, uint action) => 0;
    public ScriptedGossipReply SelectReply(Player player, NpcInfo npc, uint sender, uint action)
    {
        if (npc.Entry == 15471 && sender == 1 && action == 1001 &&
            Raid?.GetData(1) is EncounterState.NotStarted or EncounterState.Fail)
        {
            _point = 3;
            Me.Motion.MovePoint(3, Path[3].X, Path[3].Y, Path[3].Z, run: true);
        }
        return new(0, Close: true);
    }
    public override void OnMovementInform(MovementGeneratorType type, uint pointId)
    {
        if (type != MovementGeneratorType.Point) return;
        if (pointId is 0 or 1 or 3)
        {
            int next = (int)pointId + 1;
            _point = next;
            Me.Motion.MovePoint((uint)next, Path[next].X, Path[next].Y, Path[next].Z, run: next >= 3);
        }
        else if (pointId == 4 && _point == 4)
        {
            _dialogueStep = 0;
            _dialogueMs = 1;
        }
    }
    public override void OnUpdate(uint diffMs)
    {
        if (_dialogueStep >= 0)
        {
            _dialogueMs = _dialogueMs > diffMs ? _dialogueMs - diffMs : 0;
            while (_dialogueMs == 0 && _dialogueStep < Dialogue.Length)
            {
                var line = Dialogue[_dialogueStep++];
                System?.SayText(Me, line.Text);
                if (_dialogueStep == Dialogue.Length)
                {
                    Raid?.SetData(1, EncounterState.InProgress);
                    _dialogueStep = -1;
                    break;
                }
                _dialogueMs = line.Delay;
            }
        }
        if (!Me.Combat.IsInCombat || !UpdateVictim()) return;
        _commandMs = _commandMs > diffMs ? _commandMs - diffMs : 0;
        _bashMs = _bashMs > diffMs ? _bashMs - diffMs : 0;
        _strikeMs = _strikeMs > diffMs ? _strikeMs - diffMs : 0;
        if (_commandMs == 0 && DoCast(Me, 25516) == CreatureCastResult.Ok)
            _commandMs = (uint)(System?.RandomInt(30000, 45000) ?? 30000);
        if (_bashMs == 0 && DoCast(Victim, 25515) == CreatureCastResult.Ok)
            _bashMs = (uint)(System?.RandomInt(12000, 15000) ?? 12000);
        if (_strikeMs == 0 && DoCast(Victim, 22591) == CreatureCastResult.Ok)
            _strikeMs = (uint)(System?.RandomInt(4000, 6000) ?? 4000);
    }
}

/// <summary>mangos-classic boss_rajaxx.cpp npc_kaldorei_eliteAI::ExecuteAction.</summary>
public sealed class KaldoreiEliteAI(Creature creature) : CreatureAI(creature)
{
    private uint _cleaveMs = 2000, _strikeMs = 8000;
    public override void OnRespawn() { _cleaveMs = 2000; _strikeMs = 8000; }
    public override void OnUpdate(uint diffMs)
    {
        if (!Me.Combat.IsInCombat || !UpdateVictim()) return;
        _cleaveMs = _cleaveMs > diffMs ? _cleaveMs - diffMs : 0;
        _strikeMs = _strikeMs > diffMs ? _strikeMs - diffMs : 0;
        if (_cleaveMs == 0 && DoCast(Victim, 26350) == CreatureCastResult.Ok)
            _cleaveMs = (uint)(System?.RandomInt(5000, 7000) ?? 5000);
        if (_strikeMs == 0 && DoCast(Victim, 16856) == CreatureCastResult.Ok)
            _strikeMs = (uint)(System?.RandomInt(9000, 13000) ?? 9000);
    }
}

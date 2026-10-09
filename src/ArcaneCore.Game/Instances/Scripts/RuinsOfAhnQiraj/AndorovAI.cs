using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;

namespace ArcaneCore.Game.Instances.Scripts.RuinsOfAhnQiraj;

/// <summary>mangos-classic ruins_of_ahnqiraj/boss_rajaxx.cpp npc_general_andorovAI (JustRespawned, MoveInLineOfSight, JustDied,
/// JustDidDialogueStep, MovementInform, EnterEvadeMode, DoInitializeFollowers, DoMoveToEventLocation, ReceiveAIEvent, HandleDespawn,
/// HandleMove, ExecuteAction) and GossipHello/GossipSelect_npc_general_andorov.
/// <para>
/// He runs to the intro point five seconds after he appears, with the Kaldorei following. Choosing the gossip line sends him on, makes him
/// and the Kaldorei able to fight creatures (UNIT_FLAG_IMMUNE_TO_NPC off) and starts SAY_ANDOROV_INTRO_1-2; arriving at the attack point
/// runs SAY_ANDOROV_INTRO_3-4 and SAY_ANDOROV_ATTACK_START, whose end starts the Rajaxx event. When Rajaxx dies he becomes a vendor and,
/// 135 seconds later, says SAY_ANDOROV_DESPAWN and leaves.
/// Deviation: EnterEvadeMode's MovePoint back to the intro or attack point is the evade's home here (the point is made his home position
/// on arrival), so the host's own evade brings him there.
/// </para></summary>
public sealed class AndorovAI(Creature creature) : CreatureAI(creature), INpcGossipScript
{
    /// <summary>cmangos AI_EVENT_CUSTOM_A, sent by the instance when Rajaxx is DONE.</summary>
    public const uint AiEventRajaxxDefeated = 1000;
    public const uint Entry = 15471, KaldoreiElite = 15473;
    private const uint PointIntro = 2, PointAttack = 4;
    private const uint GossipStart = 1001, GossipTrade = 1; // GOSSIP_ACTION_INFO_DEF + 1, GOSSIP_ACTION_TRADE
    private const int SayDespawn = -1509032, SayKillsAndorov = -1509016;

    // ruins_of_ahnqiraj.h aAndorovMoveLocs.
    private static readonly (float X, float Y, float Z, float O)[] Path =
    [
        (-8701.51f, 1561.80f, 32.092f, 0), (-8718.66f, 1577.69f, 21.612f, 0),
        (-8876.97f, 1651.96f, 21.57f, 5.52f), (-8882.15f, 1602.77f, 21.386f, 0),
        (-8940.45f, 1550.69f, 21.616f, 0)
    ];

    // aIntroDialogue: a 0 delay ends a chain; StartNextDialogueText(SAY_ANDOROV_INTRO_1) runs the first two lines,
    // StartNextDialogueText(SAY_ANDOROV_INTRO_3) the last three.
    private static readonly (int Text, uint Delay)[] Dialogue =
    [(-1509004, 7000), (-1509031, 0), (-1509003, 4000), (-1509029, 6000), (-1509030, 0)];
    private const int IntroStart = 0, AttackDialogue = 2, AttackStartLine = 4;

    private uint _point;
    private int _dialogueStep = -1;
    private uint _dialogueMs;
    private uint _moveMs, _despawnMs;
    private uint _commandMs, _bashMs, _strikeMs;
    private RuinsOfAhnQirajInstance? Raid => Me.Map?.FindUpdater<InstanceData>() as RuinsOfAhnQirajInstance;

    public override void OnRespawn()
    {
        _point = 0;
        _dialogueStep = -1;
        _despawnMs = 0;
        _moveMs = 5000; // JustRespawned: ResetTimer(ANDOROV_MOVE, 5000)
        ResetCombatTimers();
    }

    private void ResetCombatTimers()
    {
        _commandMs = Roll(1000, 3000);
        _bashMs = Roll(8000, 11000);
        _strikeMs = Roll(2000, 5000);
    }

    private uint Roll(int min, int max) => (uint)(System?.RandomInt(min, max) ?? min);

    private IEnumerable<Creature> Kaldorei()
        => System?.Creatures.Where(c => c.Entry == KaldoreiElite && c.IsAlive) ?? [];

    /// <summary>HandleMove and DoInitializeFollowers: run to the next point, the Kaldorei following at their current offset.</summary>
    private void HandleMove()
    {
        MoveTo(_point);
        foreach (Creature elite in Kaldorei()) FollowAndorov(elite, Me);
    }

    internal static void FollowAndorov(Creature elite, Creature andorov)
    {
        float dx = andorov.X - elite.X, dy = andorov.Y - elite.Y;
        float angle = MathF.Atan2(-dy, -dx) - andorov.Orientation;
        elite.Motion.MoveFollow(andorov, MathF.Sqrt((dx * dx) + (dy * dy)), angle);
    }

    private void MoveTo(uint point) => Me.Motion.MovePoint(point, Path[point].X, Path[point].Y, Path[point].Z, run: true);

    public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
    {
        if (npc.Entry != Entry || Raid is not { } raid) return null;
        uint rajaxx = raid.GetData(1);
        if (rajaxx == EncounterState.InProgress) return ScriptedGossipMenu.Nothing;
        List<ScriptedGossipItem> items = [];
        if (rajaxx is EncounterState.NotStarted or EncounterState.Fail)
            items.Add(new(0, "Let's find out.", 1, GossipStart)); // GOSSIP_ITEM_START -3509000
        if (((NpcFlags)Me.NpcFlags & NpcFlags.Vendor) != 0)
            items.Add(new(1, "Let's see what you have.", 1, GossipTrade)); // GOSSIP_ITEM_TRADE -3509001
        return new(false, 7883, items);
    }

    public uint Select(Player player, NpcInfo npc, uint sender, uint action) => 0;

    public ScriptedGossipReply SelectReply(Player player, NpcInfo npc, uint sender, uint action)
    {
        if (npc.Entry != Entry) return default;
        if (action == GossipTrade) return new(0, Vendor: true);
        if (action == GossipStart && Raid?.GetData(1) is EncounterState.NotStarted or EncounterState.Fail)
            MoveToEventLocation();
        return new(0, Close: true);
    }

    /// <summary>DoMoveToEventLocation.</summary>
    private void MoveToEventLocation()
    {
        // After a failed attempt m_pointId is past the last point (the reference indexes aAndorovMoveLocs[5]); here he goes back to the
        // attack point, whose arrival runs the attack dialogue again.
        if (_point > PointAttack) _point = PointAttack;
        MoveTo(_point);
        Me.NpcFlags &= ~(uint)NpcFlags.Gossip;
        Me.UnitFlags &= ~UnitFlags.ImmuneToNpc;
        foreach (Creature elite in Kaldorei()) elite.UnitFlags &= ~UnitFlags.ImmuneToNpc;
        StartDialogue(IntroStart);
    }

    private void StartDialogue(int step)
    {
        _dialogueStep = step;
        _dialogueMs = 0;
        AdvanceDialogue();
    }

    private void AdvanceDialogue()
    {
        while (_dialogueStep >= 0 && _dialogueMs == 0)
        {
            (int text, uint delay) = Dialogue[_dialogueStep];
            System?.SayText(Me, text);
            if (_dialogueStep == AttackStartLine) Raid?.SetData(1, EncounterState.InProgress); // JustDidDialogueStep
            _dialogueStep = delay == 0 ? -1 : _dialogueStep + 1;
            _dialogueMs = delay;
        }
    }

    public override void OnMovementInform(MovementGeneratorType type, uint pointId)
    {
        if (type != MovementGeneratorType.Point) return;
        switch (pointId)
        {
            case 0:
            case 1:
            case 3:
                if (pointId != _point) return;
                MoveTo(++_point);
                break;
            case PointIntro:
                if (_point != PointIntro) return;
                Me.NpcFlags |= (uint)NpcFlags.Gossip;
                System?.SetFacingTo(Me, Path[PointIntro].O);
                System?.SetHomePosition(Me, Path[PointIntro].X, Path[PointIntro].Y, Path[PointIntro].Z, Path[PointIntro].O);
                _point++;
                break;
            case PointAttack:
                System?.SetHomePosition(Me, Path[PointAttack].X, Path[PointAttack].Y, Path[PointAttack].Z, Me.Orientation);
                // Start the dialogue only the first time he reaches the point.
                if (_point == PointAttack)
                {
                    StartDialogue(AttackDialogue);
                    _point++;
                }
                break;
        }
    }

    /// <summary>MoveInLineOfSight: Rajaxx within 50 yards is attacked.</summary>
    public override void MoveInLineOfSight(Unit who)
    {
        if (who is Creature { Entry: 15341, IsAlive: true } rajaxx && Victim is null &&
            (rajaxx.X - Me.X) * (rajaxx.X - Me.X) + (rajaxx.Y - Me.Y) * (rajaxx.Y - Me.Y) + (rajaxx.Z - Me.Z) * (rajaxx.Z - Me.Z) <= 2500)
            AttackStart(rajaxx);
        base.MoveInLineOfSight(who);
    }

    /// <summary>JustDied: killed by Rajaxx, Rajaxx says SAY_KILLS_ANDOROV.</summary>
    public override void OnDeath(Unit? killer)
    {
        if (killer is Creature { Entry: 15341 } rajaxx) System?.SayText(rajaxx, SayKillsAndorov);
    }

    public override void OnEvade() => ResetCombatTimers();

    /// <summary>ReceiveAIEvent(AI_EVENT_CUSTOM_A): Rajaxx is dead; he becomes a vendor and leaves 135 seconds later.</summary>
    public override void OnReceiveAiEvent(uint eventType, Unit sender, Unit? invoker, uint miscValue)
    {
        if (eventType != AiEventRajaxxDefeated) return;
        Me.NpcFlags |= (uint)(NpcFlags.Gossip | NpcFlags.Vendor);
        _despawnMs = 135000;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_moveMs != 0 && (_moveMs = _moveMs > diffMs ? _moveMs - diffMs : 0) == 0) HandleMove();
        if (_despawnMs != 0 && (_despawnMs = _despawnMs > diffMs ? _despawnMs - diffMs : 0) == 0)
        {
            // HandleDespawn.
            System?.SayText(Me, SayDespawn);
            System?.ForcedDespawn(Me, 2500);
        }

        if (_dialogueStep >= 0)
        {
            _dialogueMs = _dialogueMs > diffMs ? _dialogueMs - diffMs : 0;
            AdvanceDialogue();
        }

        if (!Me.Combat.IsInCombat || !UpdateVictim()) return;
        _commandMs = _commandMs > diffMs ? _commandMs - diffMs : 0;
        _bashMs = _bashMs > diffMs ? _bashMs - diffMs : 0;
        _strikeMs = _strikeMs > diffMs ? _strikeMs - diffMs : 0;
        if (_commandMs == 0 && DoCast(Me, 25516) == CreatureCastResult.Ok) _commandMs = Roll(30000, 45000);
        if (_bashMs == 0 && DoCast(Victim, 25515) == CreatureCastResult.Ok) _bashMs = Roll(12000, 15000);
        if (_strikeMs == 0 && DoCast(Victim, 22591) == CreatureCastResult.Ok) _strikeMs = Roll(4000, 6000);
    }
}

/// <summary>mangos-classic boss_rajaxx.cpp npc_kaldorei_eliteAI::EnterEvadeMode and ExecuteAction: after a fight the elite goes back to
/// following Andorov.</summary>
public sealed class KaldoreiEliteAI(Creature creature) : CreatureAI(creature)
{
    private uint _cleaveMs, _strikeMs;

    private uint Roll(int min, int max) => (uint)(System?.RandomInt(min, max) ?? min);

    public override void OnRespawn() => ResetCombatTimers();

    private void ResetCombatTimers()
    {
        _cleaveMs = Roll(2000, 4000);
        _strikeMs = Roll(8000, 11000);
    }

    public override void OnEvade() => ResetCombatTimers();

    public override void OnReachedHome()
    {
        if ((Me.Map?.FindUpdater<InstanceData>() as RuinsOfAhnQirajInstance)?.FindAndorov() is { IsAlive: true } andorov)
            AndorovAI.FollowAndorov(Me, andorov);
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!Me.Combat.IsInCombat || !UpdateVictim()) return;
        _cleaveMs = _cleaveMs > diffMs ? _cleaveMs - diffMs : 0;
        _strikeMs = _strikeMs > diffMs ? _strikeMs - diffMs : 0;
        if (_cleaveMs == 0 && DoCast(Victim, 26350) == CreatureCastResult.Ok) _cleaveMs = Roll(5000, 7000);
        if (_strikeMs == 0 && DoCast(Victim, 16856) == CreatureCastResult.Ok) _strikeMs = Roll(9000, 13000);
    }
}

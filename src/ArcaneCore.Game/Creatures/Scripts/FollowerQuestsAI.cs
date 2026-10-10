using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>Shared helpers for the small ScriptDev2 follower quests.</summary>
public abstract class QuestFollowerAI(Creature creature) : FollowerAI(creature)
{
    /// <summary>IsWithinDistInMap(who, range): 3D with the bounding radii.</summary>
    protected bool Within(Unit who, float range)
    {
        float dx = Me.X - who.X, dy = Me.Y - who.Y, dz = Me.Z - who.Z;
        float reach = range + Me.BoundingRadius + who.BoundingRadius;
        return (dx * dx) + (dy * dy) + (dz * dz) <= reach * reach;
    }
}

/// <summary>
/// Mist (entry 3568, Teldrassil), quest 938 "Mist": mangos-classic ScriptDev2 <c>npc_mist</c> (kalimdor/teldrassil.cpp at 8ec338a).
/// She follows the player (faction 79); within 10 yards of Sentinel Arynia Cloudsbreak the quest is done.
/// </summary>
public sealed class MistAI(Creature creature) : QuestFollowerAI(creature), IQuestScriptAI
{
    public const uint Entry = 3568, QuestMist = 938, NpcArynia = 3519, FactionDarnassus = 79;
    public const int SayAtHome = -1000323, EmoteAtHome = -1000324;

    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId == QuestMist)
        {
            StartFollow(player, FactionDarnassus, questId);
        }
    }

    public override void MoveInLineOfSight(Unit who)
    {
        base.MoveInLineOfSight(who);
        if (Me.Combat.Victim is null && !HasFollowState(FollowState.Complete) && who is Creature { Entry: NpcArynia } arynia && Within(arynia, 10f))
        {
            System?.SayText(arynia, SayAtHome);
            System?.SayText(Me, EmoteAtHome);
            if (GetLeaderForFollower() is { } player)
            {
                System?.RewardGroupEventExplored(player, QuestMist, Me);
            }

            SetFollowComplete();
        }
    }
}

/// <summary>
/// Shay Leafrunner (entry 7774, Feralas), quest 2845 "Wandering Shay": mangos-classic <c>npc_shay_leafrunner</c> (kalimdor/feralas.cpp at
/// 8ec338a). She follows the player but wanders off after 30 s (then every 60 s once recalled) until Shay's Bell (spell 11402) calls her back;
/// within 20 yards of Rockbiter the quest is done and she walks to him, despawning 30 s later.
/// </summary>
public sealed class ShayLeafrunnerAI(Creature creature) : QuestFollowerAI(creature), IQuestScriptAI
{
    public const uint Entry = 7774, QuestWanderingShay = 2845, NpcRockbiter = 7765, SpellShaysBell = 11402;
    public const int SayEscortStart = -1001106, EmoteWander = -1001114, SayEventComplete1 = -1001115, SayEventComplete2 = -1001116;
    private static readonly int[] s_wander = [-1001107, -1001108, -1001109, -1001110];
    private static readonly int[] s_wanderDone = [-1001111, -1001112, -1001113];
    private const float InteractionDistance = 5f;

    private uint _wanderMs;
    private bool _recalled, _complete;

    public bool Wandering => HasFollowState(FollowState.Paused);
    public bool Complete => _complete;

    protected override void Reset()
    {
        _recalled = false;
        _complete = false;
    }

    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId != QuestWanderingShay)
        {
            return;
        }

        System?.SayText(Me, SayEscortStart);
        Me.UnitFlags &= ~UnitFlags.ImmuneToNpc;
        StartFollow(player, 0, questId);
        _wanderMs = 30_000;
    }

    /// <summary>Shay's Bell: she comes back and follows again.</summary>
    public override void OnSpellHit(Unit caster, Spells.SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        if (spell.Id == SpellShaysBell && caster is Player)
        {
            Recall();
        }
    }

    /// <summary>AI_EVENT_CUSTOM_A from the bell.</summary>
    public void Recall()
    {
        _recalled = true;
        SetFollowPaused(false);
    }

    public override void MoveInLineOfSight(Unit who)
    {
        base.MoveInLineOfSight(who);
        if (System is not { } system)
        {
            return;
        }

        if (!_complete && who is Creature { Entry: NpcRockbiter } rockbiter && Within(rockbiter, 20f))
        {
            if (GetLeaderForFollower() is not { } player)
            {
                return;
            }

            system.SayText(Me, SayEventComplete1);
            system.SayText(rockbiter, SayEventComplete2);
            system.RewardGroupEventExplored(player, QuestWanderingShay, Me);
            SetFollowComplete(withEndEvent: true);
            system.ForcedDespawn(Me, 30_000);
            _complete = true;
            _wanderMs = 0;
            float a = MathF.Atan2(Me.Y - rockbiter.Y, Me.X - rockbiter.X);
            Me.Motion.MovePoint(0, rockbiter.X + (InteractionDistance * MathF.Cos(a)), rockbiter.Y + (InteractionDistance * MathF.Sin(a)), rockbiter.Z, run: false);
        }
        else if (_recalled && who is Player player2 && Within(player2, InteractionDistance))
        {
            // The source tests pWho against itself here (always true); a recalled Shay settles as soon as she sees a player.
            _wanderMs = 60_000;
            _recalled = false;
            system.SayText(Me, s_wanderDone[system.RandomInt(0, 2)]);
        }
    }

    protected override void UpdateFollowerAI(uint diffMs)
    {
        if ((UpdateVictim() && Victim is not null) || _wanderMs == 0)
        {
            return;
        }

        if (_wanderMs > diffMs)
        {
            _wanderMs -= diffMs;
            return;
        }

        _wanderMs = 0;
        if (System is not { } system)
        {
            return;
        }

        // Off she goes, around a point 25-40 yards away.
        SetFollowPaused(true);
        system.SayText(Me, EmoteWander);
        system.SayText(Me, s_wander[system.RandomInt(0, 3)]);
        float distance = system.RandomInt(25_000, 40_000) / 1000f;
        float angle = system.RandomInt(0, 6283) / 1000f;
        var center = new CreatureHome(Me.X + (distance * MathF.Cos(angle)), Me.Y + (distance * MathF.Sin(angle)), Me.Z, Me.Orientation);
        Me.Motion.MoveRandom(new RandomMovementGenerator(20f, center, run: false));
    }
}

/// <summary>
/// The Threshwackonator 4100 (entry 6669, Darkshore), quest 2078 "Gyromast's Revenge": mangos-classic <c>npc_threshwackonator</c>
/// (kalimdor/darkshore.cpp at 8ec338a). Turning its key (gossip) makes it follow the player; within 10 yards of Gelkak Gyromast it turns
/// hostile and attacks its holder.
/// </summary>
public sealed class ThreshwackonatorAI(Creature creature) : QuestFollowerAI(creature)
{
    public const uint Entry = 6669, QuestGyromastRevenge = 2078, NpcGelkak = 6667, FactionHostile = 14;
    public const int EmoteStart = -1000325, SayAtClose = -1000326;

    public void TurnKey(Player player)
    {
        System?.SayText(Me, EmoteStart);
        StartFollow(player);
    }

    public override void MoveInLineOfSight(Unit who)
    {
        base.MoveInLineOfSight(who);
        if (Me.Combat.Victim is null && !HasFollowState(FollowState.Complete) && who is Creature { Entry: NpcGelkak } gelkak && Within(gelkak, 10f))
        {
            System?.SayText(gelkak, SayAtClose);
            Me.FactionTemplate = FactionHostile;
            if (GetLeaderForFollower() is { } holder)
            {
                System?.AttackStart(Me, holder);
            }

            SetFollowComplete();
        }
    }
}

/// <summary>GossipHello/GossipSelect_npc_threshwackonator: "Turn the key to start the machine." while 2078 is incomplete (texts 758 / 718).</summary>
public sealed class ThreshwackonatorGossip(Func<Player, PlayerQuestLog?> quests) : INpcGossipScript
{
    public const uint TextDefault = 718, TextKeyReady = 758, ActionTurnKey = 1001;
    public const string OptionTurnKey = "Turn the key to start the machine.";

    public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
        => quests(player)?.GetStatus(ThreshwackonatorAI.QuestGyromastRevenge) == QuestStatus.Incomplete
            ? new ScriptedGossipMenu(false, TextKeyReady, [new ScriptedGossipItem(0, OptionTurnKey, 1, ActionTurnKey)])
            : new ScriptedGossipMenu(false, TextDefault, []);

    public uint Select(Player player, NpcInfo npc, uint sender, uint action) => 0;

    public ScriptedGossipReply SelectReply(Player player, NpcInfo npc, uint sender, uint action)
    {
        if (action == ActionTurnKey
            && player.Map?.FindUpdater<CreatureMapSystem>()?.Creatures.FirstOrDefault(c => c.Guid == npc.Guid)?.AI is ThreshwackonatorAI machine)
        {
            machine.TurnKey(player);
        }

        return new ScriptedGossipReply(0, Close: true);
    }
}

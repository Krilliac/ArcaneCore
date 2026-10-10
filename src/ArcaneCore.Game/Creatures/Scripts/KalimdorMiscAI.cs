using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Magrami Spectre (entry 11560, Desolace): mangos-classic ScriptDev2 <c>npc_magrami_spectre</c> (kalimdor/desolace.cpp at 3e8597afe7).
/// On respawn it casts Ghost Spawn In, the blue aura and Chilling Touch, and says one of two emotes. When its point move 1 ends (the DB
/// script walks it), it wanders 5 yards around that point, swaps to the green aura and turns hostile (faction 16). In combat it casts Curse
/// of the Magrami once, 4 s in.
/// </summary>
public sealed class MagramiSpectreAI(Creature creature) : CreatureAI(creature)
{
    public const uint Entry = 11560, SpellCurseOfMagrami = 18159, SpellGhostSpawnIn = 17321, SpellBlueAura = 17327, SpellGreenAura = 18951,
        SpellChillingTouch = 18146, FactionHostile = 16;
    public const int SayEmote1 = -1001226, SayEmote2 = -1001227;

    private uint _curseMs = 4000;

    public override void OnEvade()
    {
        _curseMs = 4000;
        base.OnEvade();
    }

    public override void OnRespawn()
    {
        DoCast(null, SpellGhostSpawnIn);
        DoCast(null, SpellBlueAura);
        DoCast(null, SpellChillingTouch);
        if (System is { } system)
        {
            system.SayText(Me, system.RandomInt(0, 1) == 0 ? SayEmote1 : SayEmote2);
        }

        base.OnRespawn();
    }

    public override void OnMovementInform(MovementGeneratorType type, uint pointId)
    {
        if (type != MovementGeneratorType.Point || pointId != 1 || System is not { } system)
        {
            return;
        }

        Me.Motion.MoveRandom(new RandomMovementGenerator(5f, new CreatureHome(Me.X, Me.Y, Me.Z, Me.Orientation), run: false));
        system.RemoveAuras(Me, SpellBlueAura);
        DoCast(Me, SpellGreenAura);
        Me.FactionTemplate = FactionHostile;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!UpdateVictim() || Victim is not { } victim || _curseMs == 0)
        {
            return;
        }

        if (_curseMs <= diffMs)
        {
            _curseMs = 0;
            DoCast(victim, SpellCurseOfMagrami);
        }
        else
        {
            _curseMs -= diffMs;
        }
    }
}

/// <summary>
/// "Plucky" Johnson (entry 6626, Thousand Needles), "The Scoop" (quest 1950): mangos-classic ScriptDev2 <c>npc_plucky_johnson</c>
/// (kalimdor/thousand_needles.cpp at 3e8597afe7). He stays a chicken (spell 9220) until a player beckons (with the quest incomplete) or
/// does /chicken at him. Then he turns human (9192), friendly (35) and gossip-enabled for 2 minutes; /chicken also makes him wave.
/// Difference: the beckon is not gated on the quest here (the AI cannot read quest status); his gossip option still is.
/// </summary>
public sealed class PluckyJohnsonAI(Creature creature) : CreatureAI(creature)
{
    public const uint Entry = 6626, QuestScoop = 1950, FactionFriendly = 35, SpellPluckyHuman = 9192, SpellPluckyChicken = 9220;
    public const uint TextEmoteBeckon = 7, TextEmoteChicken = 22, EmoteOneshotWave = 3;

    private uint _resetMs = 120_000;
    private uint _homeFaction;
    private bool _started;

    public bool Human => (Me.NpcFlags & (uint)NpcFlags.Gossip) != 0;

    private void Reset()
    {
        if (!_started)
        {
            _homeFaction = Me.FactionTemplate;
            _started = true;
        }

        _resetMs = 120_000;
        Me.NpcFlags &= ~(uint)NpcFlags.Gossip;
        Me.FactionTemplate = _homeFaction;
        DoCast(Me, SpellPluckyChicken);
    }

    public override void OnEvade() => Reset();

    public override void OnReceiveEmote(Player player, uint textEmote)
    {
        if (textEmote == TextEmoteBeckon)
        {
            BecomeHuman();
        }
        else if (textEmote == TextEmoteChicken && !Human)
        {
            BecomeHuman();
            System?.PlayEmote(Me, EmoteOneshotWave);
        }
    }

    private void BecomeHuman()
    {
        if (!_started)
        {
            Reset();
        }

        Me.FactionTemplate = FactionFriendly;
        Me.NpcFlags |= (uint)NpcFlags.Gossip;
        DoCast(Me, SpellPluckyHuman);
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!_started)
        {
            Reset();
        }

        if (Human)
        {
            if (_resetMs <= diffMs)
            {
                if (Me.Combat.Victim is null)
                {
                    EnterEvadeMode();
                    Reset();
                }
                else
                {
                    Me.NpcFlags &= ~(uint)NpcFlags.Gossip;
                }

                return;
            }

            _resetMs -= diffMs;
        }

        UpdateVictim();
    }
}

/// <summary>GossipHello/GossipSelect_npc_plucky_johnson: "Please tell me the Phrase.." while 1950 is incomplete (text 720); the answer (738) credits it.</summary>
public sealed class PluckyJohnsonGossip(Func<Player, PlayerQuestLog?> quests, Action<Player, uint> eventHappens) : INpcGossipScript
{
    public const uint TextHello = 720, TextPhrase = 738, ActionPhrase = 1000;
    public const string OptionPhrase = "Please tell me the Phrase..";

    public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
        => new(false, TextHello, quests(player)?.GetStatus(PluckyJohnsonAI.QuestScoop) == QuestStatus.Incomplete
            ? [new ScriptedGossipItem(0, OptionPhrase, 1, ActionPhrase)] : []);

    public uint Select(Player player, NpcInfo npc, uint sender, uint action) => 0;

    public ScriptedGossipReply SelectReply(Player player, NpcInfo npc, uint sender, uint action)
    {
        if (action != ActionPhrase)
        {
            return new ScriptedGossipReply(0, Close: true);
        }

        eventHappens(player, PluckyJohnsonAI.QuestScoop);
        return new ScriptedGossipReply(TextPhrase);
    }
}

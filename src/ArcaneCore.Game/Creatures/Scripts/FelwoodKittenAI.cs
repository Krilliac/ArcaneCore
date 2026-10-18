using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Pets;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Winna's kitten for "Corrupted Sabers" (quest 4506), Felwood: mangos-classic ScriptDev2 <c>npc_kitten</c> (9937), the Corrupted Saber Visual
/// spell script (16510) and <c>npc_corrupt_saber</c>'s gossip (10042), all in kalimdor/felwood.cpp:40-158, on <see cref="FollowerAI"/>.
/// A kitten with a player owner jumps into the moonwell: it emotes, takes UNIT_FLAG_SPAWNING and follows its owner, paused. 7.5 s later it
/// casts Corrupted Saber Visual and now shows as the Corrupted Saber, says its follow emote to the owner and follows. Close to Winna
/// Hazzard (9996) it gains the gossip flag. Its owner, on the quest and within interaction distance of Winna, can release it to her,
/// which completes the quest and ends the follow.
/// <para>
/// The source runs the start in the constructor; here it runs on the first update that finds a player owner. The transform is the spell
/// script's <c>SetEntry</c> (<see cref="CreatureMapSystem.ShowAsEntry"/>), done when the cast succeeds. The gossip is the AI's own, so it
/// answers whichever entry the creature shows. Registered by <see cref="Instances.Scripts.World.KalimdorWorldMapData"/> as a map entry
/// script, so a guardian keeps it.
/// </para>
/// </summary>
public sealed class FelwoodKittenAI(Creature creature) : FollowerAI(creature), INpcGossipScript
{
    public const uint Entry = 9937, NpcCorruptSaber = 10042, NpcWinna = 9996, QuestCorruptSaber = 4506, SpellCorruptSaberVisual = 16510;
    public const int EmoteSabJump = -1000541, EmoteSabFollow = -1000542;
    public const uint MoonwellCooldownMs = 7500, ActionRelease = 1001, SenderMain = 1;
    public const float InteractionDistance = 5f;
    public const string GossipItemRelease = "I want to release the corrupted saber to Winna.";

    private bool _started;
    private uint _moonwellMs = MoonwellCooldownMs;

    /// <summary>Whether it has turned into the Corrupted Saber.</summary>
    public bool Transformed { get; private set; }

    public bool Paused => HasFollowState(FollowState.Paused);

    protected override void Reset()
    {
    }

    private void Start()
    {
        if (Me.GetCharmerOrOwner() is not Player owner)
        {
            return; // no owner (yet): asked again on the next update
        }

        _started = true;

        StartFollow(owner);
        SetFollowPaused(true);
        System?.SayText(Me, EmoteSabJump);
        Me.UnitFlags |= UnitFlags.Spawning;
    }

    /// <summary>MoveInLineOfSight: following, out of a fight and near Winna, it can be spoken to.</summary>
    public override void MoveInLineOfSight(Unit who)
    {
        ArgumentNullException.ThrowIfNull(who);
        if (Me.Combat.Victim is not null || (Me.NpcFlags & (uint)NpcFlags.Gossip) != 0 || !HasFollowState(FollowState.InProgress)
            || who is not Creature { Entry: NpcWinna } || !Near(who))
        {
            return;
        }

        Me.NpcFlags |= (uint)NpcFlags.Gossip;
    }

    protected override void UpdateFollowerAI(uint diffMs)
    {
        if (!_started)
        {
            Start();
        }

        if (!UpdateVictim())
        {
            if (Paused)
            {
                if (_moonwellMs < diffMs)
                {
                    if (DoCast(Me, SpellCorruptSaberVisual) == CreatureCastResult.Ok)
                    {
                        Transform();
                    }

                    SetFollowPaused(false);
                }
                else
                {
                    _moonwellMs -= diffMs;
                }
            }
        }
    }

    /// <summary>CorruptedSaberVisual::OnEffectExecute.</summary>
    private void Transform()
    {
        if (System?.ShowAsEntry(Me, NpcCorruptSaber) != true)
        {
            return;
        }

        Transformed = true;
        Me.UnitFlags &= ~UnitFlags.Spawning;
        if (Me.GetCharmerOrOwner() is { } owner)
        {
            System.SayText(Me, EmoteSabFollow, owner);
        }
    }

    private bool Near(WorldObject other)
    {
        float dx = Me.X - other.X, dy = Me.Y - other.Y, dz = Me.Z - other.Z;
        float reach = InteractionDistance + Me.BoundingRadius + (other is Unit unit ? unit.BoundingRadius : 0f);
        return (dx * dx) + (dy * dy) + (dz * dz) <= reach * reach;
    }

    /// <summary>GossipHello_npc_corrupt_saber: the saber's own menu; the kitten has no script gossip.</summary>
    public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (Me.Entry != NpcCorruptSaber)
        {
            return null;
        }

        bool offer = System is { } system && system.QuestIncomplete(player, QuestCorruptSaber)
            && system.ClosestOfEntry(Me, NpcWinna, InteractionDistance, aliveOnly: true) is not null;
        return new ScriptedGossipMenu(false, 0, offer ? [new ScriptedGossipItem(0, GossipItemRelease, SenderMain, ActionRelease)] : []);
    }

    public uint Select(Player player, NpcInfo npc, uint sender, uint action) => 0;

    /// <summary>GossipSelect_npc_corrupt_saber: released to Winna, the follow ends and the quest is done.</summary>
    public ScriptedGossipReply SelectReply(Player player, NpcInfo npc, uint sender, uint action)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (action != ActionRelease)
        {
            return default;
        }

        SetFollowComplete();
        System?.QuestEventHappened(player, QuestCorruptSaber);
        return new ScriptedGossipReply(0, Close: true);
    }
}

using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Kerlonian Evershade (entry 11218, Darkshore), "The Sleeper Has Awakened" (quest 5321): mangos-classic ScriptDev2 <c>npc_kerlonian</c>
/// (kalimdor/darkshore.cpp at 3e8597afe7) on <see cref="FollowerAI"/>. Accepting wakes him out of bear form and he follows the player; every
/// 25-90 s he falls asleep and holds until the player uses the Horn of Awakening on him (spell 17536). Near Liladris Moonriver the quest is done.
/// </summary>
public sealed class KerlonianAI(Creature creature) : FollowerAI(creature), IQuestScriptAI
{
    public const uint Entry = 11218, QuestSleeperAwakened = 5321, NpcLiladris = 11219, FactionEscortNeutralFriendPassive = 290;
    public const uint SpellBearForm = 18309, SpellSleepVisual = 25148, SpellAwaken = 17536, SoundAggro = 6701;
    public const int SayStart = -1000434, SayEnd = -1000444, EmoteAwaken = -1000445;
    private static readonly int[] s_sleepEmotes = [-1000435, -1000436, -1000437];
    private static readonly int[] s_sleepSays = [-1000438, -1000439, -1000440, -1000441];
    private const float InteractionDistance = 5f;

    private uint _fallAsleepMs;

    public bool Sleeping => HasFollowState(FollowState.Paused);

    protected override void Reset()
    {
        _fallAsleepMs = (uint)(System?.RandomInt(10_000, 45_000) ?? 10_000);
        if (!HasFollowState(FollowState.InProgress))
        {
            DoCast(Me, SpellBearForm);
        }
    }

    protected override void Aggro(Unit target) => System?.PlayDirectSound(Me, SoundAggro);

    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId != QuestSleeperAwakened)
        {
            return;
        }

        System?.RemoveAuras(Me, SpellBearForm);
        Me.StandState = StandState.Stand;
        Me.UnitFlags &= ~UnitFlags.ImmuneToNpc;
        System?.SayText(Me, SayStart, player);
        StartFollow(player, FactionEscortNeutralFriendPassive, questId);
    }

    public override void MoveInLineOfSight(Unit who)
    {
        base.MoveInLineOfSight(who);
        if (Me.Combat.Victim is not null || HasFollowState(FollowState.Complete) || who is not Creature { Entry: NpcLiladris })
        {
            return;
        }

        float dx = Me.X - who.X, dy = Me.Y - who.Y, dz = Me.Z - who.Z;
        float reach = (InteractionDistance * 5) + Me.BoundingRadius + who.BoundingRadius;
        if ((dx * dx) + (dy * dy) + (dz * dz) > reach * reach)
        {
            return;
        }

        if (GetLeaderForFollower() is { } player)
        {
            System?.RewardGroupEventExplored(player, QuestSleeperAwakened, Me);
            System?.SayText(Me, SayEnd);
        }

        SetFollowComplete();
    }

    /// <summary>EffectDummyCreature_awaken_kerlonian: the Horn of Awakening wakes him (AI_EVENT_CUSTOM_A from a player).</summary>
    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        if (spell.Id == SpellAwaken && caster is Player && HasFollowState(FollowState.InProgress | FollowState.Paused))
        {
            ClearSleeping();
        }
    }

    private void SetSleeping()
    {
        SetFollowPaused(true);
        if (System is { } system)
        {
            system.SayText(Me, s_sleepEmotes[system.RandomInt(0, 2)]);
            system.SayText(Me, s_sleepSays[system.RandomInt(0, 3)]);
        }

        if (DoCast(Me, SpellSleepVisual) == CreatureCastResult.Ok)
        {
            Me.StandState = StandState.Sleep;
        }
    }

    /// <summary>Wakes him and he follows again.</summary>
    public void ClearSleeping()
    {
        System?.RemoveAuras(Me, SpellSleepVisual);
        Me.StandState = StandState.Stand;
        System?.SayText(Me, EmoteAwaken);
        SetFollowPaused(false);
    }

    protected override void UpdateFollowerAI(uint diffMs)
    {
        if (UpdateVictim() && Victim is not null)
        {
            return;
        }

        if (!HasFollowState(FollowState.InProgress) || HasFollowState(FollowState.Paused))
        {
            return;
        }

        if (_fallAsleepMs < diffMs)
        {
            SetSleeping();
            _fallAsleepMs = (uint)(System?.RandomInt(25_000, 90_000) ?? 25_000);
        }
        else
        {
            _fallAsleepMs -= diffMs;
        }
    }
}

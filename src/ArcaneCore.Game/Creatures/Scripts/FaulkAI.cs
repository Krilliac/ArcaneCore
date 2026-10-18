using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Henze Faulk (6172, Elwynn Forest) and Narm Faulk (6177, Dun Morogh), ClassicDB ScriptName <c>npc_redemption_target</c> (mangos-classic
/// ScriptDevAI world/npcs_special.cpp, npc_redemption_targetAI and the 8593 PaladinQuestReviveSelf spell script): the target of the paladin
/// quests "The Tome of Divinity" (1783, 1786) lies dead (UNIT_DYNFLAG_DEAD, stand state dead) and is no quest giver until a player's Symbol of
/// Life (8593) hits him. He then casts Quest - Self Healing (25155) and stands; 3 s later he faces the paladin, at 4 s he becomes a quest giver
/// and thanks them (broadcast text 2283), at 7 s he kneels, and 2 minutes after the revival he evades, which lays him down again.
/// </summary>
public sealed class FaulkAI(Creature creature) : ScriptedAI(creature)
{
    public const uint HenzeEntry = 6172, NarmEntry = 6177, SymbolOfLife = 8593, QuestSelfHealing = 25155;
    public const uint DynFlagDead = 0x20, NpcFlagQuestGiver = 0x2, EmoteOneshotKneel = 16;
    public const int SayHeal = 2283;
    public const uint EvadeMs = 120_000;

    private uint _evadeMs, _faceMs, _textMs, _emoteMs;
    private ObjectGuid _player;

    public bool IsRevived => _evadeMs != 0;

    protected override void Reset()
    {
        _evadeMs = _faceMs = _textMs = _emoteMs = 0;
        Me.NpcFlags &= ~NpcFlagQuestGiver;
        Me.SetUInt32(UpdateFields.UnitDynamicFlags, Me.GetUInt32(UpdateFields.UnitDynamicFlags) | DynFlagDead);
        Me.StandState = StandState.Dead;
    }

    public override void MoveInLineOfSight(Unit who)
    {
    }

    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        if (spell.Id == SymbolOfLife && caster is not null)
        {
            ReviveSelf(caster.Guid);
        }
    }

    /// <summary>npc_redemption_targetAI::DoReviveSelf: nothing while a revival is still running.</summary>
    public void ReviveSelf(ObjectGuid player)
    {
        if (_evadeMs != 0)
        {
            return;
        }

        DoCast(Me, QuestSelfHealing, triggered: false);
        Me.SetUInt32(UpdateFields.UnitDynamicFlags, Me.GetUInt32(UpdateFields.UnitDynamicFlags) & ~DynFlagDead);
        Me.StandState = StandState.Stand;
        _evadeMs = EvadeMs;
        _player = player;
        _faceMs = 3000;
        _textMs = 4000;
        _emoteMs = 7000;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (Tick(ref _faceMs, diffMs) && Me.Map?.FindObject(_player) is Player facing)
        {
            System?.SetFacingTo(Me, MathF.Atan2(facing.Y - Me.Y, facing.X - Me.X));
        }

        if (Tick(ref _textMs, diffMs))
        {
            Me.NpcFlags |= NpcFlagQuestGiver;
            System?.SayText(Me, SayHeal, Me.Map?.FindObject(_player) as Player);
        }

        if (Tick(ref _emoteMs, diffMs))
        {
            System?.PlayEmote(Me, EmoteOneshotKneel);
        }

        if (Tick(ref _evadeMs, diffMs))
        {
            EnterEvadeMode();
        }
    }

    /// <summary>A running timer counts down; true once, on the update it runs out.</summary>
    private static bool Tick(ref uint timer, uint diffMs)
    {
        if (timer == 0)
        {
            return false;
        }

        if (timer <= diffMs)
        {
            timer = 0;
            return true;
        }

        timer -= diffMs;
        return false;
    }
}
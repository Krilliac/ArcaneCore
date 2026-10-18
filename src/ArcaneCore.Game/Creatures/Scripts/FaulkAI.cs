using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Henze Faulk (6172, Elwynn Forest) and Narm Faulk (6177, Dun Morogh), the paladin quests "The Tome of Divinity" (vmangos scripts
/// elwynn_forest.cpp npc_henze_faulk and dun_morogh.cpp npc_narm_faulk): they lie as if dead (UNIT_DYNFLAG_DEAD, stand state dead) until a
/// Symbol of Life (8593) hits them; then they stand, thank the paladin (broadcast text 2283 / 2281) and lie down again two minutes later
/// (evade, which resets them).
/// </summary>
public sealed class FaulkAI(Creature creature) : ScriptedAI(creature)
{
    public const uint HenzeEntry = 6172, NarmEntry = 6177, SymbolOfLife = 8593;
    public const uint DynFlagDead = 0x20;
    public const uint LifeMs = 120_000;

    private uint _lifeMs;
    private bool _spellHit;

    public bool IsRevived => _spellHit;

    private int SayHeal => Me.Entry == NarmEntry ? 2281 : 2283;

    protected override void Reset()
    {
        _lifeMs = LifeMs;
        Me.SetUInt32(UpdateFields.UnitDynamicFlags, DynFlagDead);
        Me.StandState = StandState.Dead;
        _spellHit = false;
    }

    public override void MoveInLineOfSight(Unit who)
    {
    }

    public override void OnUpdate(uint diffMs)
    {
        if (Me.StandState != StandState.Stand)
        {
            return;
        }

        if (_lifeMs < diffMs)
        {
            EnterEvadeMode();
        }
        else
        {
            _lifeMs -= diffMs;
        }
    }

    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        if (spell.Id != SymbolOfLife || _spellHit)
        {
            return;
        }

        Me.StandState = StandState.Stand;
        Me.SetUInt32(UpdateFields.UnitDynamicFlags, 0);
        System?.SayText(Me, SayHeal, caster);
        _spellHit = true;
    }
}

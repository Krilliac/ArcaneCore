using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Yenniku (entry 2530, Stranglethorn), "Saving Yenniku" (quest 592): mangos-classic ScriptDev2 <c>mob_yenniku</c>
/// (eastern_kingdoms/stranglethorn_vale.cpp). Yenniku's Release (3607) from a player calms him for 60 s: he stops fighting where he stands,
/// loses his auras and loot tap, stands stunned and turns Horde-friendly (faction 83). When the minute is up he takes his own faction back
/// and evades home.
/// Difference from SD2: the creature AI cannot read the caster's quest log, so the "quest 592 incomplete" check is left to the spell, which
/// only the quest item casts. His faction comes back when the timer ends rather than when he reaches home.
/// </summary>
public sealed class YennikuAI(Creature creature) : ScriptedAI(creature)
{
    public const uint Entry = 2530, SpellYennikusRelease = 3607, QuestSavingYenniku = 592, FactionHordeGeneric = 83;
    public const uint CalmMs = 60_000, EmoteStateStun = 64;

    private uint _resetMs;

    public bool IsCalmed => _resetMs > 0;

    protected override void Reset()
    {
        // The per-fight reset runs from StopCombatInPlace too, so the calm timer is not cleared here.
    }

    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        if (spell.Id == SpellYennikusRelease && caster is Player && _resetMs == 0)
        {
            _resetMs = CalmMs;
            Calm();
        }
    }

    public override bool OnEnterEvadeMode()
    {
        if (_resetMs == 0)
        {
            return false;
        }

        Calm();
        return true;
    }

    private void Calm()
    {
        if (System is { } system)
        {
            system.RemoveAllAuras(Me);
            system.StopCombatInPlace(Me);
        }

        Me.SetUInt32(UpdateFields.UnitNpcEmotestate, EmoteStateStun);
        Me.FactionTemplate = FactionHordeGeneric;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_resetMs > 0)
        {
            if (_resetMs <= diffMs)
            {
                _resetMs = 0;
                Me.SetUInt32(UpdateFields.UnitNpcEmotestate, 0);
                Me.FactionTemplate = Me.Template.Faction;
                System?.EnterEvadeMode(Me);
            }
            else
            {
                _resetMs -= diffMs;
            }
        }

        UpdateVictim();
    }
}

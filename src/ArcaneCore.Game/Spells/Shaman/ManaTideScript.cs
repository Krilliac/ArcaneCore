using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Shaman;

/// <summary>
/// Mana Tide (16191; vmangos scripts/spells/spell_shaman.cpp:49-67, <c>ShamanManaTideAuraScript::OnPeriodicTrigger</c>; mangos-classic
/// SpellAuras.cpp:1209-1213). When the aura is a PERIODIC_TRIGGER_SPELL, each tick the aura's target casts the effect's trigger spell on itself with the
/// aura's amount, dithered, as the trigger's base points, instead of the trigger's own value; without a trigger spell the tick casts nothing.
/// <para>
/// The build 5875 row of 16191 (classic-db z2815 spell_template) is not a trigger: it is APPLY_AREA_AURA_PARTY of PERIODIC_ENERGIZE, 170 mana every
/// 3 seconds within 20 yards, the aura the Mana Tide Totem (16190, creature 10467) carries. That row restores mana through the area aura and the
/// periodic energize tick, and never reaches this script; the script keeps vmangos' behaviour for a trigger-shaped row.
/// </para>
/// </summary>
public sealed class ManaTideModule : ISpellHandlerModule
{
    public const uint ManaTide = 16191;

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterPeriodicTriggerScript(ManaTide, Tick);
    }

    /// <summary>One periodic trigger of Mana Tide: the target casts the trigger spell at itself with the dithered aura amount.</summary>
    public static void Tick(SpellSystem system, SpellAuraHolder holder, SpellAura aura)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(aura);
        uint trigger = holder.Spell.Effects[aura.EffectIndex].TriggerSpell;
        if (trigger == 0)
        {
            return;
        }

        Unit target = holder.Target;
        int dithered = (int)MathF.Floor(aura.Amount + system.Random.NextSingle()); // vmangos rand_dither (an integer amount stays as it is)
        system.CastCustomSpell(target, trigger, SpellCastTargets.ForUnit(target.Guid), dithered);
    }
}

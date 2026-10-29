using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.Generic;

/// <summary>Wolfshead Helm Energy 17770 (vmangos spell_item.cpp:600-613, WolfsheadHelmScript): the caster casts Wolfshead Helm Energy 29940 on itself.</summary>
[SpellScript(17770)]
public sealed class WolfsheadHelmScript : ISpellScript
{
    public const uint HelmEnergy = 29940;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        GadgetCasts.Cast(context, context.Caster, context.Caster, HelmEnergy);
    }
}

/// <summary>Warrior's Wrath 21977 (vmangos spell_warrior.cpp:113-124): on effect 0 the caster casts the Warrior's Wrath spell mod 21887 at the unit target.</summary>
[SpellScript(21977)]
public sealed class WarriorsWrathScript : ISpellScript
{
    public const uint WrathMod = 21887;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.EffectIndex == 0)
        {
            GadgetCasts.Cast(context, context.Caster, context.Target, WrathMod);
        }
    }
}

/// <summary>Bending Shinbone 8856 (vmangos SpellEffects.cpp:3693-3701): a player's test of the shinbone casts 8854 one time in five, else 8855, on itself.</summary>
[SpellScript(8856)]
public sealed class BendingShinboneScript : ISpellScript
{
    public const uint Break = 8854, Hold = 8855;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Caster is not Player)
        {
            return;
        }

        GadgetCasts.Cast(context, context.Caster, context.Caster, GadgetCasts.Roll(context, 5) == 0 ? Break : Hold);
    }
}

/// <summary>Nefarian Class Call Shaman Corrupted Totems 23424 (vmangos boss_nefarian.cpp:564-576): on effect 0 the caster casts one of four corrupted totem summons on itself.</summary>
[SpellScript(23424)]
public sealed class NefarianCorruptedTotemsScript : ISpellScript
{
    internal static readonly uint[] Totems = [23419, 23420, 23422, 23423];

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.EffectIndex == 0)
        {
            GadgetCasts.Cast(context, context.Caster, context.Caster, Totems[GadgetCasts.Roll(context, Totems.Length)]);
        }
    }
}

/// <summary>
/// Arcane Vacuum 21147 (vmangos SpellEffects.cpp:986-998, Azuregos): the target's threat on the caster is zeroed (modifyThreatPercent -100) and
/// the caster casts Summon Player 21150 on it.
/// </summary>
[SpellScript(21147)]
public sealed class ArcaneVacuumScript : ISpellScript
{
    public const uint SummonPlayer = 21150;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Unit caster = context.Caster;
        if (caster.Combat.HasThreatList && caster.Combat.Threat.GetThreat(context.Target) != 0f)
        {
            caster.Combat.Threat.ModifyThreatPercent(context.Target, -100);
        }

        GadgetCasts.Cast(context, caster, context.Target, SummonPlayer);
    }
}

/// <summary>Rotate Trigger 26137 (vmangos SpellEffects.cpp:4024-4031, C'Thun's eye): the target casts Rotate 360 (26009) or Rotate -360 (26136) on itself, even odds.</summary>
[SpellScript(26137)]
public sealed class RotateTriggerScript : ISpellScript
{
    public const uint RotateForward = 26009, RotateBack = 26136;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        GadgetCasts.Cast(context, context.Target, context.Target, GadgetCasts.Roll(context, 2) != 0 ? RotateForward : RotateBack);
    }
}

/// <summary>
/// Dream Fog 24781 (vmangos SpellEffects.cpp:1166-1173): a creature caster starts attacking the target (vmangos notes that it should fixate on it;
/// AttackStart is what it does). A non-creature caster does nothing.
/// </summary>
[SpellScript(24781)]
public sealed class DreamFogScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Caster is Creature { System: { } creatures } caster)
        {
            creatures.AttackStart(caster, context.Target);
        }
    }
}

/// <summary>
/// Elune's Candle 26374 (vmangos spell_item.cpp:553-574, which acts in OnCast; the DUMMY effect runs right after power is taken, with the same
/// unit target): the caster casts 26636 at the target, except at Omen (15467), which takes a random one of 26622, 26623, 26624, 26625 and 26649,
/// and at a Minion of Omen (15466), which takes 26624.
/// </summary>
[SpellScript(26374)]
public sealed class ElunesCandleScript : ISpellScript
{
    public const uint Firework = 26636, NpcOmen = 15467, NpcMinionOfOmen = 15466;
    internal static readonly uint[] OmenSpells = [26622, 26623, 26624, 26625, 26649];

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        uint spellId = context.Target switch
        {
            Creature { Entry: NpcOmen } => OmenSpells[GadgetCasts.Roll(context, OmenSpells.Length)],
            Creature { Entry: NpcMinionOfOmen } => 26624,
            _ => Firework,
        };

        GadgetCasts.Cast(context, context.Caster, context.Target, spellId);
    }
}

/// <summary>
/// The Winter Veil minipet kits (vmangos SpellEffects.cpp:4329-4357, EffectScriptEffect): Summon Helper 26532 (Green Helper Box, minipet 15698),
/// Summon Helper 26541 (Red Helper Box, 15705), Summon Snowman 26469 (15710) and Summon Reindeer 26528 (15706). A player who already has that
/// minipet out sends it away and nothing is used up; otherwise the spell's DB script (vmangos spell_scripts rows 26532, 26541, 26469 and 26528,
/// command 15) has the player cast the real summon (26533, 26536, 26045, 26529) on itself, and that spell takes the Snowball.
/// </summary>
[SpellScript(26532, 26541, 26469, 26528)]
public sealed class WinterVeilMinipetKitScript : ISpellScript
{
    internal static (uint Entry, uint Summon) For(uint spellId) => spellId switch
    {
        26532 => (15698, 26533),
        26541 => (15705, 26536),
        26469 => (15710, 26045),
        _ => (15706, 26529),
    };

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Caster is not Player player)
        {
            return;
        }

        (uint entry, uint summon) = For(context.Spell.Id);
        if (player.Map?.FindUpdater<PetMapSystem>()?.MiniPetOf(player) is { } current && current.Entry == entry
            && context.System.Summons is SummonService service)
        {
            service.Unsummon(current);
            return;
        }

        context.System.CastSpell(player, summon, SpellCastTargets.ForSelf(), triggered: false);
    }
}

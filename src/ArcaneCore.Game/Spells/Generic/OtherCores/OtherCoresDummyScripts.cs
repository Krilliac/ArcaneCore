using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.Generic.OtherCores;

// DUMMY and SCRIPT_EFFECT spells that vmangos leaves empty but a 1.12 core implements: mangos-classic (cmangos) Spell::EffectDummy /
// EffectScriptEffect (src/game/Spells/SpellEffects.cpp) and its ScriptDevAI SpellScript classes, or mangoszero (src/game/WorldHandlers). cmangos
// classic outranks mangoszero where they differ. The behaviour is re-expressed here, not copied. State: scripts are stateless singletons; every
// hook runs on the world thread of the spell's map.

internal static class OtherCoresCasts
{
    /// <summary><c>caster->CastSpell(target, id, TRIGGERED_OLD_TRIGGERED)</c>; a self target is the nullptr-target form.</summary>
    public static void Cast(SpellEffectContext context, Unit caster, Unit target, uint spellId) => GadgetCasts.Cast(context, caster, target, spellId);

    /// <summary>The creature behind <paramref name="unit"/> when its entry is one of <paramref name="entries"/> (empty = any creature).</summary>
    public static Creature? CreatureOf(Unit unit, params uint[] entries)
        => unit is Creature creature && (entries.Length == 0 || entries.Contains(creature.Entry)) ? creature : null;
}

/// <summary>Ice Lock 5249 (cmangos SpellEffects.cpp:3671-3677): the target casts Ice Lock 22856 on itself. (mangoszero SpellEffectScript.cpp:106 has the caster cast it; cmangos classic wins.)</summary>
[SpellScript(5249)]
public sealed class IceLockScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        OtherCoresCasts.Cast(context, context.Target, context.Target, 22856);
    }
}

/// <summary>Dragon Orb 19869 (cmangos SpellEffects.cpp:772-778, mangoszero SpellEffectDummy.cpp:601): a player without Mind Exhaustion 23958 casts Possess 19832 on itself.</summary>
[SpellScript(19869)]
public sealed class DragonOrbScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target is Player player && !context.System.HasAura(player, 23958))
        {
            OtherCoresCasts.Cast(context, player, player, 19832);
        }
    }
}

/// <summary>Axe Flurry 24019 (cmangos SpellEffects.cpp:1080-1086): a target in line of sight of the caster is hit by Axe Flurry 24020 from the caster.</summary>
[SpellScript(24019)]
public sealed class AxeFlurryScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        bool visible = context.Caster.Map?.FindUpdater<CreatureMapSystem>()?.IsInLineOfSight(context.Caster, context.Target) ?? true;
        if (visible)
        {
            OtherCoresCasts.Cast(context, context.Caster, context.Target, 24020);
        }
    }
}

/// <summary>Summon Demented Druid Spirit 24796 (cmangos SpellEffects.cpp:1096-1101): the caster casts 24795 on a player target.</summary>
[SpellScript(24796)]
public sealed class DementedDruidSpiritScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target is Player)
        {
            OtherCoresCasts.Cast(context, context.Caster, context.Target, 24795);
        }
    }
}

/// <summary>
/// The Silithus Ancients' dummy spells (cmangos SpellEffects.cpp:1103-1124): Merithra's Wake 25145 to 25172, Arygos's Vengeance 25149 to 25168, Molten Rain
/// 25150 to 25169 and 25170, each cast by the target on itself; Time Stop 25158 makes the target immune to NPCs, strips all its auras and casts 25171.
/// </summary>
[SpellScript(25145, 25149, 25150, 25158)]
public sealed class SilithusAncientsScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Unit target = context.Target;
        switch (context.Spell.Id)
        {
            case 25145:
                OtherCoresCasts.Cast(context, target, target, 25172);
                break;
            case 25149:
                OtherCoresCasts.Cast(context, target, target, 25168);
                break;
            case 25150:
                OtherCoresCasts.Cast(context, target, target, 25169);
                OtherCoresCasts.Cast(context, target, target, 25170);
                break;
            default:
                target.UnitFlags |= UnitFlags.ImmuneToNpc;
                foreach (SpellAuraHolder holder in context.System.GetAuras(target).ToArray())
                {
                    context.System.RemoveAuras(target, holder.Spell.Id);
                }

                OtherCoresCasts.Cast(context, target, target, 25171);
                break;
        }
    }
}

/// <summary>
/// Creatures that go away when a script spell hits them (ForcedDespawn): Despawn Spectral Combatants 17678 after 3 s (cmangos SpellEffects.cpp:650-660),
/// Despawn 26264 (:3980-3988) and Despawn Toxic Slimes 26585 (boss_viscidus.cpp ViscidusDespawnAdds) any creature at once, Despawn Shades of Taerar 24886
/// (:1166-1177, entry 15302), Despawn Brood 25792 (:1126-1132, Yauj brood 15621), Dispel Drakonids 23484 (:1062-1078, Nefarian's drakonids 14261-14265 and
/// 14302), Despawn Chained Spirits 24342 (boss_mandokir.cpp DespawnChainedSpirits, Chained Spirit 15117), and the Kodo Kombobulator 18269 (mangoszero
/// SpellEffectDummy.cpp:531-546) on any non-player target, and Despawn Buffet 29336 (boss_sapphiron.cpp DespawnBuffet) on any creature.
/// </summary>
[SpellScript(17678, 26264, 26585, 24886, 25792, 23484, 24342, 18269, 29336)]
public sealed class DespawnCreatureScript : ISpellScript
{
    private static readonly uint[] s_drakonids = [14261, 14262, 14263, 14264, 14265, 14302];

    /// <summary>The wild kodos the Kombobulator takes and the Tamed Kodo they become (Aged, Dying, Ancient 4700-4702; Tamed 11627).</summary>
    private static readonly uint[] s_kodos = [4700, 4701, 4702, 11627];

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        uint[] entries = context.Spell.Id switch
        {
            24886 => [15302u],
            25792 => [15621u],
            23484 => s_drakonids,
            24342 => [15117u],
            18269 => s_kodos,
            _ => [],
        };
        if (OtherCoresCasts.CreatureOf(context.Target, entries) is not { } creature
            || (entries.Length == 0 && !IsListedScriptTarget(context, creature)))
        {
            return;
        }

        WorldDummyCasts.Despawn(creature, context.Spell.Id == 17678 ? 3000u : 0u);
    }

    /// <summary>
    /// 17678, 26264, 26585 and 29336 name no entry in their reference scripts: the spell's script-target selection (spell_script_target) decides
    /// who is hit. A world without those rows would otherwise despawn every creature in the area, so the target must match a row (a creature or dead-creature
    /// row for its entry).
    /// </summary>
    private static bool IsListedScriptTarget(SpellEffectContext context, Creature creature)
    {
        foreach (SpellStore.ScriptTarget row in context.System.Store.GetScriptTargets(context.Spell.Id))
        {
            if (row.TargetEntry == creature.Entry && row.Type is 1 or 2)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Elemental Shield 22276 and Brood Power 22282 (cmangos SpellEffects.cpp:820-841): the caster drops whichever of the five shield auras it has (22277-22281;
/// 22283, 22285-22288) and casts one chosen at random on itself.
/// </summary>
[SpellScript(22276, 22282)]
public sealed class RandomShieldScript : ISpellScript
{
    private static readonly uint[] s_elemental = [22277, 22278, 22279, 22280, 22281];
    private static readonly uint[] s_brood = [22283, 22285, 22286, 22287, 22288];

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        uint[] set = context.Spell.Id == 22276 ? s_elemental : s_brood;
        Unit caster = context.Caster;
        foreach (uint id in set)
        {
            context.System.RemoveAuras(caster, id);
        }

        OtherCoresCasts.Cast(context, caster, caster, set[GadgetCasts.Roll(context, set.Length)]);
    }
}

/// <summary>Spawn Drakonid 22659 (cmangos SpellEffects.cpp:842-856): by the spawner's entry (14312 blue, 14310 green, 14311 bronze, 14309 red, 14307 black) it casts the matching summon 22658, 22656, 22657, 22655, 22654.</summary>
[SpellScript(22659)]
public sealed class SpawnDrakonidScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Caster is not Creature spawner)
        {
            return;
        }

        uint summon = spawner.Entry switch
        {
            14312 => 22658,
            14310 => 22656,
            14311 => 22657,
            14309 => 22655,
            14307 => 22654,
            _ => 0,
        };
        if (summon != 0)
        {
            OtherCoresCasts.Cast(context, spawner, spawner, summon);
        }
    }
}

/// <summary>Shadowblink 22664 (cmangos SpellEffects.cpp:858-868): its second effect makes the caster cast one of the nine blink spells 22668-22676 at random.</summary>
[SpellScript(22664)]
public sealed class ShadowblinkScript : ISpellScript
{
    private static readonly uint[] s_blinks = [22668, 22669, 22670, 22671, 22672, 22673, 22674, 22675, 22676];

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.EffectIndex == 1)
        {
            OtherCoresCasts.Cast(context, context.Caster, context.Caster, s_blinks[GadgetCasts.Roll(context, s_blinks.Length)]);
        }
    }
}

/// <summary>Transporter Arrival 23450 (Everlook; cmangos SpellEffects.cpp:1031-1040): one in six the caster casts Transporter Malfunction 23444, two in six Evil Twin 23445.</summary>
[SpellScript(23450)]
public sealed class EverlookTransporterArrivalScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        int roll = GadgetCasts.Roll(context, 6);
        if (roll == 0)
        {
            OtherCoresCasts.Cast(context, context.Caster, context.Caster, 23444);
        }
        else if (roll <= 2)
        {
            OtherCoresCasts.Cast(context, context.Caster, context.Caster, 23445);
        }
    }
}

/// <summary>Stinger Charge Primer 26080 (cmangos SpellEffects.cpp:1133-1143): the caster casts 26082 on a target with Vekniss Catalyst 26078, else 26081.</summary>
[SpellScript(26080)]
public sealed class StingerChargePrimerClassicScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        OtherCoresCasts.Cast(context, context.Caster, context.Target, context.System.HasAura(context.Target, 26078) ? 26082u : 26081u);
    }
}

/// <summary>Initialize Tank 27749 (cmangos SpellEffects.cpp:1240-1248): the target casts RC Tank Control 24936 and Steam Tank Passive 27747 on itself.</summary>
[SpellScript(27749)]
public sealed class InitializeTankScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        OtherCoresCasts.Cast(context, context.Target, context.Target, 24936);
        OtherCoresCasts.Cast(context, context.Target, context.Target, 27747);
    }
}

/// <summary>Land Mine Trigger 28000 (cmangos SpellEffects.cpp:1230-1239): the mine detonates (25099), drops Land Mine Periodic 27999 and despawns after 2 s.</summary>
[SpellScript(28000)]
public sealed class LandMineTriggerScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Unit mine = context.Caster;
        OtherCoresCasts.Cast(context, mine, mine, 25099);
        context.System.RemoveAuras(mine, 27999);
        WorldDummyCasts.Despawn(mine, 2000);
    }
}

/// <summary>Majordomo Teleport Visual 19484 (cmangos SpellEffects.cpp:744-751): the target gets the gossip flag and casts Majordomo Teleport 19527 on itself.</summary>
[SpellScript(19484)]
public sealed class MajordomoTeleportVisualScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target is Creature creature)
        {
            creature.NpcFlags |= 1u; // UNIT_NPC_FLAG_GOSSIP
        }

        OtherCoresCasts.Cast(context, context.Target, context.Target, 19527);
    }
}

/// <summary>Kel'Thuzad's add triggers (cmangos boss_kelthuzad.cpp TriggerKTAdd): 28415 to 28421, 28416 to 28422, 28417 to 28423, 28455 to 28454, cast by the target on itself.</summary>
[SpellScript(28415, 28416, 28417, 28455)]
public sealed class KelThuzadAddTriggerScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        uint summon = context.Spell.Id switch { 28415 => 28421u, 28416 => 28422u, 28417 => 28423u, _ => 28454u };
        OtherCoresCasts.Cast(context, context.Target, context.Target, summon);
    }
}

/// <summary>
/// Clear Web Wrap 28628 and 28629 (cmangos boss_maexxna.cpp ClearWebWrap, first effect only): 28628 strips Web Wrap 28627 and its stun 28622 from a player
/// target; 28629 makes a creature target cast Kill Web Wrap 29108 on itself.
/// </summary>
[SpellScript(28628, 28629)]
public sealed class ClearWebWrapScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.EffectIndex != 0)
        {
            return;
        }

        if (context.Spell.Id == 28628)
        {
            if (context.Target is Player)
            {
                context.System.RemoveAuras(context.Target, 28627);
                context.System.RemoveAuras(context.Target, 28622);
            }
        }
        else if (context.Target is Creature)
        {
            OtherCoresCasts.Cast(context, context.Target, context.Target, 29108);
        }
    }
}

/// <summary>Zombie Chow Search 28238 (cmangos boss_gluth.cpp ZombieChowSearchHeal, first effect): the target gains 5% of its maximum health.</summary>
[SpellScript(28238)]
public sealed class ZombieChowSearchHealScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.EffectIndex == 0 && context.Target.IsAlive)
        {
            Unit target = context.Target;
            target.Health = Math.Min(target.MaxHealth, target.Health + (uint)(target.MaxHealth * 0.05f));
        }
    }
}

/// <summary>Hate to 50% 19707 (cmangos spell_scripts.cpp HateToHalf) and Hate to Zero 26569 (same file HateToZero, mangoszero SpellEffectDummy.cpp:170-175): on the first effect the caster's threat on the target falls by 50% or 100%.</summary>
[SpellScript(19707, 26569)]
public sealed class HateReductionScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.EffectIndex == 0 && !ReferenceEquals(context.Target, context.Caster))
        {
            context.Caster.Combat.Threat.ModifyThreatPercent(context.Target, context.Spell.Id == 19707 ? -50 : -100);
        }
    }
}

/// <summary>
/// Trigger Teslas 28359 (cmangos boss_thaddius.cpp TriggerTeslas): the target (only a Tesla Coil 16218, its spell_script_target) loses all its auras and casts Shock Overload 28159 (not triggered). The
/// overload emote (broadcast text 12178) is not sent: no broadcast-text seam exists for a spell script.
/// </summary>
[SpellScript(28359)]
public sealed class TriggerTeslasScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Unit target = context.Target;
        if (target is not Creature { Entry: 16218 })
        {
            return;
        }

        foreach (SpellAuraHolder holder in context.System.GetAuras(target).ToArray())
        {
            context.System.RemoveAuras(target, holder.Spell.Id);
        }

        context.System.CastSpell(target, 28159, SpellCastTargets.ForSelf(), triggered: false);
    }
}

/// <summary>
/// Teleport Trigger 29499 (cmangos boss_heigan.cpp TeleportTrigger): each target casts Teleport Players 29273 on itself. The reference also caps the
/// spell at 3 affected targets (SetMaxAffectedTargets): <see cref="HeiganTeleportTriggerModule"/> registers it.
/// </summary>
[SpellScript(29499)]
public sealed class HeiganTeleportTriggerScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        OtherCoresCasts.Cast(context, context.Target, context.Target, 29273);
    }
}

/// <summary>cmangos boss_heigan.cpp TeleportTrigger::OnInit: spell->SetMaxAffectedTargets(3).</summary>
public sealed class HeiganTeleportTriggerModule : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterSpellMaxTargetsOverride(29499, 3);
    }
}

/// <summary>
/// Corrupted Saber Visual 16510 (cmangos felwood.cpp CorruptedSaberVisual, quest "Winna's Kitten"): the caster takes the Corrupt Saber template 10042.
/// The reference swaps only entry, display, name and scale; UpdateEntry swaps the whole template (the reference's own comment calls that not exact).
/// The owner emote is not sent.
/// </summary>
[SpellScript(16510)]
public sealed class CorruptedSaberVisualScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Caster is Creature saber && saber.System is { } creatures)
        {
            creatures.UpdateEntry(saber, 10042);
        }
    }
}

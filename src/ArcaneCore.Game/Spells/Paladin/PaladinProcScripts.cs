using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells.Mods;
using ArcaneCore.Game.Spells.Procs;

namespace ArcaneCore.Game.Spells.Paladin;

/// <summary>
/// Seal of Righteousness, the melee proc of its DUMMY aura (vmangos <c>Unit::HandleDummyAuraProc</c>, UnitAuraProcHandler.cpp:979-1053, the build
/// after 1.9.4: the seal's effect 0 is a dummy aura procced by DEAL_MELEE_SWING): a living victim and a player owner, then
/// <list type="number">
/// <item>the base damage scales with the main-hand weapon's speed (2 s without a weapon) between <c>amount / 87</c> at 1.5 s and <c>amount / 25</c>
/// at 4.0 s (the aura's amount, rank and level included);</item>
/// <item>each Improved Seal of Righteousness rank's percent SPELLMOD_ALL_EFFECTS mod adds to the base ("applied on base damage only");</item>
/// <item>the done and taken spell damage bonuses of the seal spell itself;</item>
/// <item>the owner casts the rank's Holy damage spell (25742 ... 25713) at the victim with that damage, dithered, triggered by the aura, and the
/// swing may proc the weapon's enchantments once more ("Seal of Righteousness can proc weapon enchants. mechanic removed in 2.1.0").</item>
/// </list>
/// No hidden cooldown.
/// </summary>
public sealed class SealOfRighteousnessProc : IProcScript
{
    /// <summary>Seal rank → damage spell (UnitAuraProcHandler.cpp:989-1018; 20154 is the spellbook double of rank 1).</summary>
    public static readonly IReadOnlyDictionary<uint, uint> DamageSpells = new Dictionary<uint, uint>
    {
        [20154] = 25742,
        [21084] = 25742,
        [20287] = 25740,
        [20288] = 25739,
        [20289] = 25738,
        [20290] = 25737,
        [20291] = 25736,
        [20292] = 25735,
        [20293] = 25713,
    };

    /// <summary>Improved Seal of Righteousness ranks 1-5 (UnitAuraProcHandler.cpp:1033).</summary>
    public static readonly uint[] ImprovedSealOfRighteousness = [20224, 20225, 20330, 20331, 20332];

    public const float MinWeaponSpeed = 1.5f;
    public const float MaxWeaponSpeed = 4.0f;

    /// <summary>BASE_ATTACK_TIME, the speed without a main-hand weapon.</summary>
    public const uint BaseAttackTimeMs = 2000;

    public AuraProcResult? OnProc(in AuraProcContext context)
    {
        if (context.Aura.EffectIndex != 0 || context.Aura.Type != AuraType.Dummy)
        {
            return null;
        }

        if (context.Target is not { IsAlive: true } victim || context.Owner is not Player player
            || !DamageSpells.TryGetValue(context.Holder.Spell.Id, out uint damageSpellId) || context.System.Store.Get(damageSpellId) is not { } damageSpell)
        {
            return AuraProcResult.Failed;
        }

        SpellSystem system = context.System;
        float damage = BaseDamage(context.Aura.Amount, WeaponSpeed(player));
        if (system.SpellModifiers is ISpellModEngine mods)
        {
            foreach (uint talent in ImprovedSealOfRighteousness)
            {
                // Player::GetSpellMod(SPELLMOD_ALL_EFFECTS, talent): the first mod that talent gives.
                if (mods.ModsOf(player, SpellModOp.AllEffects).FirstOrDefault(m => m.SpellId == talent) is { Type: SpellModType.Pct, Value: > 0 } mod)
                {
                    damage += damage * mod.Value / 100.0f;
                }
            }
        }

        if (damage >= 0 && system.AmountModifier is { } bonus)
        {
            damage = bonus.Modify(SpellAmountStage.DirectDamage, player, victim, context.Holder.Spell, 0, damage, 1);
        }

        int dithered = (int)Math.Floor(Math.Max(damage, 0f) + system.Random.NextSingle()); // rand_dither
        system.CastProcSpell(player, damageSpell, SpellCastTargets.ForUnit(victim.Guid), context.Holder.Spell, dithered);
        system.CastItemCombatSpell(player, victim, WeaponAttackType.BaseAttack);
        return AuraProcResult.Ok;
    }

    /// <summary>The main hand's speed in seconds (vmangos <c>GetItemByPos(INVENTORY_SLOT_BAG_0, EQUIPMENT_SLOT_MAINHAND)</c>, any item there).</summary>
    public static float WeaponSpeed(Player player)
        => (player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand) is { } item ? item.Template.Delay : BaseAttackTimeMs) / 1000.0f;

    /// <summary>The speed-scaled base damage of a seal amount (UnitAuraProcHandler.cpp:1025-1029).</summary>
    public static float BaseDamage(int triggerAmount, float weaponSpeed)
    {
        float minDamage = triggerAmount / 87.0f;
        float maxDamage = triggerAmount / 25.0f;
        return ((maxDamage - minDamage) * ((weaponSpeed - MinWeaponSpeed) / (MaxWeaponSpeed - MinWeaponSpeed))) + minDamage;
    }
}

/// <summary>
/// Judgement of Light and Judgement of Wisdom (vmangos <c>Unit::HandleProcTriggerSpell</c>, UnitAuraProcHandler.cpp:1428-1466): the judged unit's
/// PROC_TRIGGER_SPELL debuff names a trigger spell that does not exist in Spell.dbc, so the rank's heal (JoL: 20267, 20341, 20342, 20343) or mana
/// (JoW: 20268, 20352, 20353) is cast by the unit that struck the judged one, on itself, triggered ("Seal of Light healing is done by the person who
/// attacks, and does not increase threat of the original caster"). No hidden cooldown.
/// </summary>
public sealed class JudgementOfLightWisdomProc : IProcScript
{
    /// <summary>Judgement debuff rank → the spell its attacker casts on itself.</summary>
    public static readonly IReadOnlyDictionary<uint, uint> TriggerSpells = new Dictionary<uint, uint>
    {
        [20185] = 20267,
        [20344] = 20341,
        [20345] = 20342,
        [20346] = 20343,
        [20186] = 20268,
        [20354] = 20352,
        [20355] = 20353,
    };

    public AuraProcResult? OnProc(in AuraProcContext context)
    {
        if (context.Aura.Type != AuraType.ProcTriggerSpell)
        {
            return null;
        }

        if (context.Target is not { IsAlive: true } attacker || !TriggerSpells.TryGetValue(context.Holder.Spell.Id, out uint triggerId)
            || context.System.Store.Get(triggerId) is not { } trigger)
        {
            return AuraProcResult.Failed;
        }

        context.System.CastProcSpell(attacker, trigger, SpellCastTargets.ForUnit(attacker.Guid), context.Holder.Spell);
        return AuraProcResult.Ok;
    }
}

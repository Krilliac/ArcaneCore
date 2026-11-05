using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Crafting.Enchanting;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Stats;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells.Generic;

/// <summary>
/// The aura cases that vmangos keeps in the switch of <c>Aura::TriggerSpell</c> (SpellAuras.cpp:1140-1310, :1470-1485) and in the removal of
/// <c>Aura::HandleAuraPeriodicDummy</c>-family auras (:7205-7225), for spells whose DBC trigger alone does not do what retail does:
/// <list type="bullet">
/// <item>Firestone Passive 758, 17945, 17947, 17949 (:1146-1180): every tick puts the rank's temporary enchant (1803, 1823, 1824, 1825) on the
/// player's main-hand weapon for one period plus one second.</item>
/// <item>Nitrous Boost 27746 (:1291-1301): every tick burns 10 mana, and the aura ends when the target has less.</item>
/// <item>Goblin Rocket Boots 8892 and Gnomish Rocket Boots 13141 (:1480-1486): only one tick in a hundred casts the aura's trigger spell (the
/// explosion); vmangos marks the exact retail chance as unconfirmed.</item>
/// <item>Tranquilizing Poison 24002 and 24003 (:7210-7217): when the aura runs out (not when it is dispelled), the target casts 24004 on itself.</item>
/// </list>
/// Thread affinity: the world thread (aura ticks and removals). Nothing here allocates on a tick except the enchant write of the Firestone.
/// Cannon Prep 24743 and 24832 (:1246, :4243) are not here and are not inert either: retail makes the cannon game object cast Fire Cannon 24731 on
/// each tick and play its custom animation 3.6 s after the aura lands, but an aura a game object casts by itself remembers the target as its caster
/// (<see cref="SpellSystem.CastForGameObject"/>), so the object is out of reach of the tick and the apply hook. They wait for an aura caster that
/// can be a game object.
/// </summary>
public sealed class PeriodicTriggerAuraModule : ISpellHandlerModule
{
    public const uint FirestoneRank1 = 758, FirestoneRank2 = 17945, FirestoneRank3 = 17947, FirestoneRank4 = 17949;
    public const uint FirestoneEnchantRank1 = 1803, FirestoneEnchantRank2 = 1823, FirestoneEnchantRank3 = 1824, FirestoneEnchantRank4 = 1825;
    public const uint NitrousBoost = 27746, GoblinRocketBoots = 8892, GnomishRocketBoots = 13141;
    public const uint TranquilizingPoison = 24002, TranquilizingPoisonB = 24003, TranquilizingPoisonEnd = 24004;
    public const int NitrousBoostManaPerTick = 10;

    /// <summary>One tick in this many casts the rocket boots' trigger spell (vmangos urand(0, 20 * 5 - 1) != 0 returns, SpellAuras.cpp:1483).</summary>
    public const int RocketBootsExplosionOdds = 100;

    /// <summary>The extra time the Firestone enchant outlives one tick (vmangos <c>m_modifier.periodictime + 1000</c>).</summary>
    public const uint FirestoneEnchantGraceMs = 1000;

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);

        system.RegisterPeriodicTriggerScript(FirestoneRank1, FirestoneTick);
        system.RegisterPeriodicTriggerScript(FirestoneRank2, FirestoneTick);
        system.RegisterPeriodicTriggerScript(FirestoneRank3, FirestoneTick);
        system.RegisterPeriodicTriggerScript(FirestoneRank4, FirestoneTick);
        system.RegisterPeriodicTriggerScript(NitrousBoost, NitrousBoostTick);
        system.RegisterPeriodicTriggerScript(GoblinRocketBoots, RocketBootsTick);
        system.RegisterPeriodicTriggerScript(GnomishRocketBoots, RocketBootsTick);

        system.HolderRemoved += holder =>
        {
            if (holder.Spell.Id is TranquilizingPoison or TranquilizingPoisonB && holder.RemoveMode == AuraRemoveMode.Expire)
            {
                system.CastSpell(holder.Target, TranquilizingPoisonEnd, SpellCastTargets.ForSelf(), triggered: true);
            }
        };
    }

    /// <summary>The enchant a Firestone rank puts on the weapon, or 0 for a spell that is not one.</summary>
    public static uint FirestoneEnchantFor(uint spellId) => spellId switch
    {
        FirestoneRank1 => FirestoneEnchantRank1,
        FirestoneRank2 => FirestoneEnchantRank2,
        FirestoneRank3 => FirestoneEnchantRank3,
        FirestoneRank4 => FirestoneEnchantRank4,
        _ => 0,
    };

    private static void FirestoneTick(SpellSystem system, SpellAuraHolder holder, SpellAura aura)
    {
        if (holder.Target is not Player player
            || PlayerStatSystem.GetWeaponForAttack(player, WeaponAttackType.BaseAttack, nonBroken: false, useable: false) is not { } weapon)
        {
            return;
        }

        uint enchant = FirestoneEnchantFor(holder.Spell.Id);
        if (enchant == 0)
        {
            return;
        }

        // Old enchant out before the new one goes in (vmangos ApplyEnchantment false, SetEnchantment, ApplyEnchantment true).
        player.Enchantments?.Apply(weapon, EnchantSlots.Temporary, apply: false);
        ItemEnchantments.Set(weapon, EnchantSlots.Temporary, enchant, aura.Period + FirestoneEnchantGraceMs, 0, player.Guid);
        player.Enchantments?.Apply(weapon, EnchantSlots.Temporary, apply: true);
    }

    private static void NitrousBoostTick(SpellSystem system, SpellAuraHolder holder, SpellAura aura)
    {
        Unit target = holder.Target;
        if (MapCombat.GetPower(target, PowerType.Mana) >= NitrousBoostManaPerTick)
        {
            MapCombat.ModifyPower(target, PowerType.Mana, -NitrousBoostManaPerTick);
            SpellSystem.SendToSet(target, WorldOpcode.SmsgSpellenergizelog, SpellPackets.BuildSpellEnergizeLog(
                target.Guid, target.Guid, NitrousBoost, (uint)PowerType.Mana, unchecked((uint)-NitrousBoostManaPerTick)), includeSelf: true);
        }
        else
        {
            system.RemoveAuras(target, NitrousBoost);
        }
    }

    private static void RocketBootsTick(SpellSystem system, SpellAuraHolder holder, SpellAura aura)
    {
        if (system.Random.Next(RocketBootsExplosionOdds) != 0)
        {
            return;
        }

        system.CastDefaultPeriodicTrigger(holder, aura);
    }
}

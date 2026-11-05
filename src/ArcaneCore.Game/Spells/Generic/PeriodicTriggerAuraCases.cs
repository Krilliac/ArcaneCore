using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells.Generic;

/// <summary>
/// The aura cases that vmangos keeps in the switch of <c>Aura::TriggerSpell</c> (SpellAuras.cpp:1140-1310) and in
/// <c>SpellAuraHolder::HandleCastOnAuraRemoval</c> (:7202-7222), for spells whose DBC trigger alone does not do what retail does:
/// <list type="bullet">
/// <item>Nitrous Boost 27746 (:1291-1301): every tick burns 10 mana, and the aura ends when the target has less.</item>
/// <item>Cannon Prep 24743 and 24832 (:1246-1248, :4243-4255): the aura (cast by a Darkmoon Faire cannon, a goober whose data10 is the spell) has
/// a 5 s PERIODIC_TRIGGER effect with no trigger spell; each tick casts Fire Cannon 24731 (<see cref="DarkmoonCannonScript"/> carries the player),
/// and 3.6 s after the aura lands the cannon plays its custom animation (animation 0).</item>
/// <item>Tranquilizing Poison 24002 and 24003 (:7210-7217): when the aura runs out (not when it is dispelled), the target casts 24004 on itself.</item>
/// </list>
/// Cases of the same vmangos switch that need no script here, checked against the 5875 client Spell.dbc:
/// <list type="bullet">
/// <item>Firestone Passive 758, 17945, 17947, 17949 (:1146-1180) sit inside <c>if (triggeredSpellInfo == nullptr)</c> (:1139), but the DBC names a
/// trigger spell for each (1054, 17936, 17940, 17942, effect 92 ENCHANT_HELD_ITEM with enchants 1803, 1823, 1824, 1825), so the default cast and
/// <c>EnchantItemSpells.Held</c> do the work, leaving a different existing enchant in place as vmangos does.</item>
/// <item>Goblin Rocket Boots 8892 and Gnomish Rocket Boots 13141 (:1480-1486) sit in the branch for a spell with a trigger spell, but the DBC
/// effect is a single APPLY_AURA MOD_INCREASE_SPEED with amplitude 0 and no trigger: the aura never ticks, so the 1-in-100 explosion roll cannot
/// be reached on 5875 data (no spell_mod row or item spell changes that).</item>
/// </list>
/// Thread affinity: the world thread (aura ticks, removals and the map's unit scheduler). Nothing here allocates on a tick except the cannon search.
/// </summary>
public sealed class PeriodicTriggerAuraModule : ISpellHandlerModule
{
    public const uint NitrousBoost = 27746;
    public const uint TranquilizingPoison = 24002, TranquilizingPoisonB = 24003, TranquilizingPoisonEnd = 24004;
    public const uint CannonPrep = 24743, CannonPrepB = 24832, FireCannon = 24731;
    public const int NitrousBoostManaPerTick = 10;

    /// <summary>The delay between the cannon aura landing and the cannon's custom animation (vmangos SpellAuras.cpp:4249 <c>AddLambdaEventAtOffset(..., 3600)</c>).</summary>
    public const uint CannonAnimationDelayMs = 3600;

    /// <summary>How far from the player the cannon that cast the aura is looked for (the aura remembers the player, not the cannon, as its caster).</summary>
    public const float CannonSearchRange = 40f;

    /// <summary>goober.spellId, the data index that names the spell a goober casts at its user (GameObjectDefines.h:333-355).</summary>
    private const int GooberSpellData = 10;

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);

        system.RegisterPeriodicTriggerScript(NitrousBoost, NitrousBoostTick);
        system.RegisterPeriodicTriggerScript(CannonPrep, CannonPrepTick);
        system.RegisterPeriodicTriggerScript(CannonPrepB, CannonPrepTick);

        system.HolderAdded += holder =>
        {
            if (holder.Spell.Id is CannonPrep or CannonPrepB)
            {
                OnCannonPrepApplied(holder);
            }
        };
        system.HolderRemoved += holder =>
        {
            if (holder.Spell.Id is TranquilizingPoison or TranquilizingPoisonB && holder.RemoveMode == AuraRemoveMode.Expire)
            {
                system.CastSpell(holder.Target, TranquilizingPoisonEnd, SpellCastTargets.ForSelf(), triggered: true);
            }
        };
    }

    /// <summary>
    /// The nearest goober that casts <paramref name="spellId"/> (data10) at its user, within <see cref="CannonSearchRange"/> of
    /// <paramref name="user"/>; null when the map has none. An aura a game object casts keeps its target as caster
    /// (<see cref="SpellSystem.CastForGameObject"/>), so the cannon is found again by position, like ScourgeInvasionFeature.TearDownNecropolis does.
    /// </summary>
    public static GameObject? FindCannon(Unit user, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Map?.FindUpdater<GameObjectMapSystem>() is not { } objects)
        {
            return null;
        }

        GameObject? best = null;
        float bestDistance = CannonSearchRange;
        foreach (GameObject go in objects.GameObjects)
        {
            if (go.Type != GameObjectType.Goober || go.Template.GetData(GooberSpellData) != spellId)
            {
                continue;
            }

            float distance = go.DistanceTo(user);
            if (distance <= bestDistance)
            {
                best = go;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static void OnCannonPrepApplied(SpellAuraHolder holder)
    {
        Unit user = holder.Target;
        if (FindCannon(user, holder.Spell.Id) is not { } cannon)
        {
            return;
        }

        AuraEvents.After(user, CannonAnimationDelayMs, () =>
        {
            if (cannon.Map?.FindUpdater<GameObjectMapSystem>() is { } objects)
            {
                objects.SendCustomAnim(cannon, 0);
            }
        });
    }

    private static void CannonPrepTick(SpellSystem system, SpellAuraHolder holder, SpellAura aura)
    {
        if (system.Store.Get(FireCannon) is not null)
        {
            Unit user = holder.Target;
            system.CastSpellTriggeredByAura(user, FireCannon, SpellCastTargets.ForUnit(user.Guid), holder.Spell);
        }
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
}

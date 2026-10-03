using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Druid;

/// <summary>
/// The druid spell scripts that need a custom base-point cast (vmangos SpellAuras.cpp:1417-1433 Frenzied Regeneration and
/// scripts/spells/spell_druid.cpp:75-100 Enrage): Frenzied Regeneration (22842 / 22895 / 22896) turns its rage into a heal on
/// every tick, Enrage (5229) adds the armor reduction aura 25503 whose size depends on the bear form. Heart of the Wild is
/// cast by the form handler (<see cref="ShapeshiftService"/>). Applied to every spell system as a handler module.
/// </summary>
public sealed class DruidScriptsModule : ISpellHandlerModule
{
    /// <summary>Frenzied Regeneration ranks 1 to 3, a periodic trigger-spell aura whose amount is the life per rage.</summary>
    public static readonly uint[] FrenziedRegenerationSpells = [22842, 22895, 22896];

    /// <summary>The heal Frenzied Regeneration casts on every tick (its base points are the rage converted to life).</summary>
    public const uint FrenziedRegenerationHeal = 22845;

    public const uint Enrage = 5229;

    /// <summary>Enrage's armor reduction aura (ModResistancePct, physical).</summary>
    public const uint EnrageArmorReduction = 25503;

    public const uint DireBearForm = 9634;

    /// <summary>The effect index of Enrage that is a Dummy: the script runs when it executes (spell_druid.cpp:87).</summary>
    public const int EnrageDummyEffect = 1;

    /// <summary>Armor reduction percent in Bear and Dire Bear Form (spell_druid.cpp:90-91).</summary>
    public const int BearArmorReduction = -27;

    public const int DireBearArmorReduction = -16;

    /// <summary>One tick of Frenzied Regeneration may take at most 10 rage (stored x10).</summary>
    public const uint MaxRageConverted = 100;

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        foreach (uint spell in FrenziedRegenerationSpells)
        {
            system.RegisterPeriodicTriggerScript(spell, FrenziedRegenerationTick);
        }

        system.RegisterObserver(new EnrageObserver(system));
    }

    /// <summary>
    /// SpellAuras.cpp:1417-1433: the aura amount is the life per rage; up to 100 stored rage is spent and the heal
    /// 22845 is cast on the druid with <c>rage * lifePerRage / 10</c> base points, dithered to an integer.
    /// </summary>
    private static void FrenziedRegenerationTick(SpellSystem system, SpellAuraHolder holder, SpellAura aura)
    {
        Unit target = holder.Target;
        uint rage = Math.Min(SpellSystem.GetPower(target, PowerType.Rage), MaxRageConverted);
        SpellSystem.SetPower(target, PowerType.Rage, SpellSystem.GetPower(target, PowerType.Rage) - rage);
        float basePoints = rage * (float)aura.Amount / 10.0f;
        int dithered = (int)MathF.Floor(basePoints + system.Random.NextSingle());      // vmangos rand_dither
        system.CastCustomSpell(target, FrenziedRegenerationHeal, SpellCastTargets.ForSelf(), dithered);
    }

    private sealed class EnrageObserver(SpellSystem system) : ISpellCastObserver
    {
        public void OnTargetOutcome(SpellCast cast, SpellTargetOutcome outcome)
        {
            if (cast.Spell.Id != Enrage || (outcome.EffectMask & (1 << EnrageDummyEffect)) == 0 || outcome.Miss != SpellMissInfo.None)
            {
                return;
            }

            if (system.Store.Get(EnrageArmorReduction) is not { } reduction)
            {
                return;
            }

            // The base point override goes to the effect that carries the armor aura (classic-db has it on effect index 1).
            int armorEffect = reduction.Effects.ToList().FindIndex(e => e.AuraType == AuraType.ModResistancePct);
            if (armorEffect < 0)
            {
                return;
            }

            int percent = system.HasAura(outcome.Target, DireBearForm) ? DireBearArmorReduction : BearArmorReduction;
            system.CastCustomSpell(outcome.Target, EnrageArmorReduction, SpellCastTargets.ForSelf(),
                armorEffect == 0 ? percent : null, armorEffect == 1 ? percent : null, armorEffect == 2 ? percent : null);
        }
    }
}

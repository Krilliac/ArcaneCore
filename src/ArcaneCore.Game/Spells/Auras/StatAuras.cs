using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Stats;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The stat auras: SPELL_AURA_MOD_STAT (29), MOD_TOTAL_STAT_PERCENTAGE (137), MOD_RESISTANCE (22), MOD_ATTACK_POWER (99) and
/// MOD_RANGED_ATTACK_POWER (124), after vmangos <c>Aura::HandleAuraModStat</c> (SpellAuras.cpp:4641),
/// <c>HandleAuraModResistance</c> (:4551), <c>HandleAuraModAttackPower</c> (:5169) and
/// <c>HandleAuraModRangedAttackPower</c> (:5181).
/// <para>
/// vmangos accumulates every contribution in <c>m_auraModifiersGroup[unitMod][TOTAL_VALUE]</c>
/// (Unit::HandleStatModifier, Unit.cpp:7794) and rewrites the update field from the sum. The repo has no
/// such ledger: items and level-ups already write the same fields as deltas (ItemSeams.cs
/// EquipmentStatsApplier, PlayerProgression), so a flat TOTAL_VALUE contribution is the same delta here.
/// The amount that was applied is remembered per aura so removal subtracts exactly that amount.
/// </para>
/// <para>
/// Limits (documented in docs/areas/spells.md): other percent aura families (MOD_PERCENT_STAT,
/// MOD_RESISTANCE_PCT, MOD_BASE_RESISTANCE, MOD_ATTACK_POWER_PCT ...) still need their own handlers; the
/// Improved Scorpid Sting stamina reduction (:4650-4677), Faerie Fire dispel immunities (:4574-4581) and
/// SPELLMOD_ATTACK_POWER talent modifier need systems that do not exist yet.
/// </para>
/// </summary>
public sealed class StatAuras : ISpellHandlerModule
{
    /// <summary>vmangos CLASSMASK_WAND_USERS (SharedDefines.h:112): priest, mage and warlock take no ranged attack power.</summary>
    private static readonly Class[] s_wandUsers = [Class.Priest, Class.Mage, Class.Warlock];

    /// <summary>What an aura contributed when it was applied, so removal subtracts the same amount.</summary>
    private static readonly ConditionalWeakTable<SpellAura, Applied> s_applied = new();
    private static readonly ConditionalWeakTable<Unit, PercentState> s_percent = new();

    private sealed record Applied(int Amount, bool Positive);
    private sealed class PercentState
    {
        public Dictionary<SpellAura, int> Active { get; } = [];
        public Dictionary<int, float> Baseline { get; } = [];
        public Dictionary<int, float> External { get; } = [];
        public Dictionary<int, float> BuffPositiveBaseline { get; } = [];
        public Dictionary<int, float> BuffNegativeBaseline { get; } = [];
        public Dictionary<int, float> BuffPositiveExternal { get; } = [];
        public Dictionary<int, float> BuffNegativeExternal { get; } = [];
    }

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterAura(AuraType.ModStat, new AuraHandler(ApplyStat, null));
        system.RegisterAura(AuraType.ModTotalStatPercentage, new AuraHandler(ApplyTotalStatPercentage, null));
        system.RegisterAura(AuraType.ModResistance, new AuraHandler(ApplyResistance, null));
        system.RegisterAura(AuraType.ModAttackPower, new AuraHandler((_, h, a, apply) => ApplyAttackPower(h, a, apply, ranged: false), null));
        system.RegisterAura(AuraType.ModRangedAttackPower, new AuraHandler((_, h, a, apply) => ApplyAttackPower(h, a, apply, ranged: true), null));
    }

    /// <summary>Record on apply (returns the amount) or take back on removal (returns the negated amount); null when nothing was applied.</summary>
    private static Applied? Take(SpellAura aura, bool apply, bool positive)
    {
        if (apply)
        {
            var record = new Applied(aura.Amount, positive);
            s_applied.AddOrUpdate(aura, record);
            return record;
        }

        if (!s_applied.TryGetValue(aura, out Applied? previous))
        {
            return null;
        }

        s_applied.Remove(aura);
        return previous;
    }

    // --- SPELL_AURA_MOD_STAT -------------------------------------------------------------------

    /// <summary>
    /// Misc value -1/-2 = all five stats, 0-4 = one stat (strength, agility, stamina, intellect, spirit);
    /// anything else is ignored (vmangos logs and returns). Each stat moves UNIT_FIELD_STATn; a player also
    /// moves PLAYER_FIELD_POSSTATn (amount &gt; 0) or NEGSTATn (Player::ApplyStatBuffMod, Player.h:1506).
    /// Stamina and intellect then move the maximum health and mana by the difference of the stat bonus
    /// (Player::UpdateMaxHealth / UpdateMaxPower, StatSystem.cpp:165-190, bonus curves :134-150; creatures
    /// 10 health per stamina and 15 mana per intellect, :775-806).
    /// </summary>
    private static void ApplyStat(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        int misc = aura.MiscValue;
        if (misc is < -2 or > 4)
        {
            return;
        }

        if (Take(aura, apply, holder.IsPositive) is not { } applied)
        {
            return;
        }

        int delta = apply ? applied.Amount : -applied.Amount;
        Unit target = holder.Target;
        for (int stat = 0; stat < 5; stat++)
        {
            if (misc >= 0 && misc != stat)
            {
                continue;
            }

            ApplyExternalStatDelta(target, stat, delta, updateBuffFields: true, buffPositive: applied.Amount > 0);
        }
        RefreshAttachedStats(target);
    }

    private static void ApplyTotalStatPercentage(SpellSystem _, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        PercentState state = s_percent.GetValue(holder.Target, static _ => new PercentState());
        int misc = aura.MiscValue;
        if (misc is < -1 or > 4)
        {
            return;
        }

        bool preserveHealthRatio = holder.Target is Player ratioPlayer
            && ratioPlayer.IsAlive && misc == 2
            && holder.Spell.HasAttribute(SpellAttributes.IsAbility);
        uint oldMaxHealth = preserveHealthRatio ? holder.Target.MaxHealth : 0;
        uint oldHealth = preserveHealthRatio ? holder.Target.Health : 0;
        if (apply)
        {
            int[] affected = AffectedStats(misc).ToArray();
            foreach (int stat in affected)
            {
                state.Baseline.TryAdd(stat, holder.Target.GetInt32(UpdateFields.UnitFieldStat0 + stat));
                if (holder.Target is Player player)
                {
                    state.BuffPositiveBaseline.TryAdd(stat, player.GetFloat(UpdateFields.PlayerFieldPosstat0 + stat));
                    state.BuffNegativeBaseline.TryAdd(stat, player.GetFloat(UpdateFields.PlayerFieldNegstat0 + stat));
                }
            }

            state.Active[aura] = aura.Amount;
            foreach (int stat in affected)
            {
                int current = holder.Target.GetInt32(UpdateFields.UnitFieldStat0 + stat);
                float next = (state.Baseline[stat] + state.External.GetValueOrDefault(stat)) * Product(state, stat);
                ApplyStatDeltaCore(holder.Target, stat, (int)MathF.Max(0, next) - current,
                    updateBuffFields: false);
            }
            ApplyPercentBuffFields(holder.Target, state, affected);
        }
        else if (state.Active.Remove(aura))
        {
            foreach (int stat in AffectedStats(misc))
            {
                float product = Product(state, stat, aura);
                int current = holder.Target.GetInt32(UpdateFields.UnitFieldStat0 + stat);
                float next = (state.Baseline[stat] + state.External.GetValueOrDefault(stat)) * product;
                ApplyStatDeltaCore(holder.Target, stat, (int)MathF.Max(0, next) - current,
                    updateBuffFields: false);
            }
            ApplyPercentBuffFields(holder.Target, state, AffectedStats(misc));
            foreach (int stat in AffectedStats(misc))
            {
                if (!state.Active.Keys.Any(a => a.MiscValue == stat || a.MiscValue == -1))
                {
                    state.Baseline.Remove(stat);
                    state.External.Remove(stat);
                    state.BuffPositiveBaseline.Remove(stat);
                    state.BuffNegativeBaseline.Remove(stat);
                    state.BuffPositiveExternal.Remove(stat);
                    state.BuffNegativeExternal.Remove(stat);
                }
            }
        }

        RefreshAttachedStats(holder.Target);
        if (preserveHealthRatio && oldMaxHealth > 0 && holder.Target.MaxHealth > 0)
        {
            holder.Target.Health = (uint)Math.Min(
                (ulong)holder.Target.MaxHealth * oldHealth / oldMaxHealth, (ulong)holder.Target.MaxHealth);
        }
    }

    private static IEnumerable<int> AffectedStats(int misc)
        => misc == -1 ? Enumerable.Range(0, 5) : [misc];

    private static void ApplyPercentBuffFields(Unit target, PercentState state, IEnumerable<int> stats)
    {
        if (target is not Player player)
        {
            return;
        }

        foreach (int stat in stats)
        {
            float positive = (state.BuffPositiveBaseline.GetValueOrDefault(stat)
                + state.BuffPositiveExternal.GetValueOrDefault(stat)) * Product(state, stat);
            float negative = (state.BuffNegativeBaseline.GetValueOrDefault(stat)
                + state.BuffNegativeExternal.GetValueOrDefault(stat)) * Product(state, stat);
            player.SetFloat(UpdateFields.PlayerFieldPosstat0 + stat, positive);
            player.SetFloat(UpdateFields.PlayerFieldNegstat0 + stat, negative);
        }
    }

    private static float Product(PercentState state, int stat, SpellAura? excluding = null)
    {
        float product = 1;
        foreach ((SpellAura aura, int amount) in state.Active)
        {
            if (!ReferenceEquals(aura, excluding) && (aura.MiscValue == stat || aura.MiscValue == -1))
            {
                product *= 1 + (amount == -100 ? -99.99f : amount) / 100f;
            }
        }

        return product;
    }

    internal static void ApplyExternalStatDelta(Unit target, int stat, int delta, bool updateBuffFields, bool? buffPositive = null)
    {
        PercentState state = s_percent.GetValue(target, static _ => new PercentState());
        if (!state.Baseline.ContainsKey(stat))
        {
            ApplyStatDeltaCore(target, stat, delta, updateBuffFields: false);
            if (updateBuffFields && target is Player player)
            {
                ApplyFlatBuffDelta(player, stat, delta, buffPositive ?? delta > 0);
            }
            RefreshAttachedStats(target);
            return;
        }

        state.External[stat] = state.External.GetValueOrDefault(stat) + delta;
        int current = target.GetInt32(UpdateFields.UnitFieldStat0 + stat);
        int next = (int)MathF.Max(0, (state.Baseline[stat] + state.External[stat]) * Product(state, stat));
        ApplyStatDeltaCore(target, stat, next - current, updateBuffFields: false);
        if (updateBuffFields && target is Player playerWithPercent)
        {
            ApplyFlatBuffDelta(playerWithPercent, stat, delta, buffPositive ?? delta > 0);
        }
        RefreshAttachedStats(target);
    }

    private static void ApplyFlatBuffDelta(Player player, int stat, int delta, bool positive)
    {
        PercentState state = s_percent.GetValue(player, static _ => new PercentState());
        bool active = state.Active.Keys.Any(a => a.MiscValue == stat || a.MiscValue == -1);
        if (!active)
        {
            int field = (positive ? UpdateFields.PlayerFieldPosstat0 : UpdateFields.PlayerFieldNegstat0) + stat;
            player.SetFloat(field, player.GetFloat(field) + delta);
            return;
        }

        Dictionary<int, float> external = positive ? state.BuffPositiveExternal : state.BuffNegativeExternal;
        external[stat] = external.GetValueOrDefault(stat) + delta;
        ApplyPercentBuffFields(player, state, [stat]);
    }

    private static void RefreshAttachedStats(Unit target)
    {
        if (target is Player player)
        {
            player.StatState.Maintainer?.UpdateAll(player);
        }
    }

    private static void ApplyStatDeltaCore(Unit target, int stat, int delta, bool updateBuffFields, bool? buffPositive = null)
    {
        if (delta == 0)
        {
            return;
        }

        int field = UpdateFields.UnitFieldStat0 + stat;
        int before = target.GetInt32(field);
        target.SetInt32(field, before + delta);
        if (updateBuffFields && target is Player)
        {
            int buffField = ((buffPositive ?? (delta > 0)) ? UpdateFields.PlayerFieldPosstat0 : UpdateFields.PlayerFieldNegstat0) + stat;
            target.SetFloat(buffField, target.GetFloat(buffField) + delta);
        }

        if (stat == 2)
        {
            int healthDelta = target is Player
                ? (int)ExperienceFormulas.HealthBonusFromStamina((uint)Math.Max(0, before + delta))
                    - (int)ExperienceFormulas.HealthBonusFromStamina((uint)Math.Max(0, before))
                : delta * 10;
            ChangeMaxHealth(target, healthDelta);
            if (target is Player staminaPlayer) StatBonuses.NoteHealthMoved(staminaPlayer, healthDelta);
        }
        else if (stat == 3 && (target is not Player player || player.GetUInt32(UpdateFields.UnitFieldBaseMana) > 0))
        {
            int manaDelta = target is Player
                ? (int)ExperienceFormulas.ManaBonusFromIntellect((uint)Math.Max(0, before + delta))
                    - (int)ExperienceFormulas.ManaBonusFromIntellect((uint)Math.Max(0, before))
                : delta * 15;
            ChangeMaxMana(target, manaDelta);
            if (target is Player manaPlayer) StatBonuses.NoteManaMoved(manaPlayer, manaDelta);
        }
    }

    /// <summary>vmangos Unit::SetMaxHealth: the maximum is at least 1 and drags the current health down with it.</summary>
    private static void ChangeMaxHealth(Unit target, int delta)
    {
        if (delta == 0)
        {
            return;
        }

        uint max = (uint)Math.Max(1L, (long)target.MaxHealth + delta);
        target.MaxHealth = max;
        if (target.Health > max)
        {
            target.Health = max;
        }
    }

    /// <summary>vmangos Unit::SetMaxPower (mana): never negative, and the current mana follows it down.</summary>
    private static void ChangeMaxMana(Unit target, int delta)
    {
        if (delta == 0)
        {
            return;
        }

        uint max = (uint)Math.Max(0L, (long)target.GetUInt32(UpdateFields.UnitFieldMaxpower1) + delta);
        target.SetUInt32(UpdateFields.UnitFieldMaxpower1, max);
        if (target.GetUInt32(UpdateFields.UnitFieldPower1) > max)
        {
            target.SetUInt32(UpdateFields.UnitFieldPower1, max);
        }
    }

    // --- SPELL_AURA_MOD_RESISTANCE -------------------------------------------------------------

    /// <summary>
    /// The misc value is a school MASK (bit 0 = physical, which is armor). Each school moves
    /// UNIT_FIELD_RESISTANCES + school, except holy, which has no resistance in 1.12 (Player::UpdateResistances,
    /// StatSystem.cpp:117-126 writes 0); a player also moves the UI buff fields
    /// PLAYER_FIELD_RESISTANCEBUFFMODSPOSITIVE / NEGATIVE (Player::ApplyResistanceBuffModsMod, Player.h:1516) by
    /// the same change. A zero amount does nothing.
    /// </summary>
    private static void ApplyResistance(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        if (aura.Amount == 0 || Take(aura, apply, holder.IsPositive) is not { } applied)
        {
            return;
        }

        int delta = apply ? applied.Amount : -applied.Amount;
        Unit target = holder.Target;
        for (int school = 0; school < 7; school++)
        {
            if (((uint)aura.MiscValue & (1u << school)) == 0)
            {
                continue;
            }

            if (school != (int)SpellSchool.Holy)
            {
                int field = UpdateFields.UnitFieldResistances + school;
                target.SetInt32(field, target.GetInt32(field) + delta);
            }

            if (target is Player player)
            {
                int buffField = (applied.Amount > 0
                    ? UpdateFields.PlayerFieldResistancebuffmodspositive
                    : UpdateFields.PlayerFieldResistancebuffmodsnegative) + school;
                player.SetFloat(buffField, player.GetFloat(buffField) + delta);
            }
        }
    }

    // --- SPELL_AURA_MOD_ATTACK_POWER / MOD_RANGED_ATTACK_POWER -----------------------------------

    /// <summary>
    /// Unit::HandleAttackPowerModifier (Unit.cpp:7745): the positive flat mods and the negative flat mods live
    /// in the two int16 halves of UNIT_FIELD_(RANGED_)ATTACK_POWER_MODS (low = positive, high = negative; see
    /// Unit::GetTotalAttackPowerValue, Unit.cpp:8037). Which half is the aura's polarity (Aura::IsPositive).
    /// Priests, mages and warlocks ignore ranged attack power (SpellAuras.cpp:5183).
    /// </summary>
    private static void ApplyAttackPower(SpellAuraHolder holder, SpellAura aura, bool apply, bool ranged)
    {
        Unit target = holder.Target;
        if (ranged && Array.IndexOf(s_wandUsers, target.Class) >= 0)
        {
            return;
        }

        if (Take(aura, apply, holder.IsPositive) is not { } applied)
        {
            return;
        }

        int delta = apply ? applied.Amount : -applied.Amount;
        int field = ranged ? UpdateFields.UnitFieldRangedAttackPowerMods : UpdateFields.UnitFieldAttackPowerMods;
        uint packed = target.GetUInt32(field);
        short positive = unchecked((short)(packed & 0xFFFF));
        short negative = unchecked((short)(packed >> 16));
        if (applied.Positive)
        {
            positive = unchecked((short)(positive + delta));
        }
        else
        {
            negative = unchecked((short)(negative + delta));
        }

        target.SetUInt32(field, (ushort)positive | ((uint)(ushort)negative << 16));
    }
}

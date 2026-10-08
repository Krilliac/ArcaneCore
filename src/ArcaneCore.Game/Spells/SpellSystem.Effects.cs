using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Rules.Application;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

/// <summary>What an effect handler gets: the cast, the target and the effect's computed value.</summary>
public sealed class SpellEffectContext
{
    internal SpellEffectContext(SpellSystem system, SpellCast cast, Unit target, int effectIndex, int value)
    {
        System = system;
        Cast = cast;
        Target = target;
        EffectIndex = effectIndex;
        Value = value;
    }

    public SpellSystem System { get; }

    public SpellCast Cast { get; }

    public Unit Caster => Cast.Caster;

    public SpellInfo Spell => Cast.Spell;

    public Unit Target { get; }

    public int EffectIndex { get; }

    /// <summary>Effects selected for this exact target (bit i = effect i).</summary>
    public int EffectMask { get; internal set; } = (1 << SpellConstants.MaxEffects) - 1;

    public SpellEffectInfo Effect => Spell.Effects[EffectIndex];

    /// <summary>The effect value (vmangos Spell::CalculateDamage → damage).</summary>
    public int Value { get; }

    /// <summary>The aura holder SPELL_EFFECT_APPLY_AURA builds for this target (vmangos Spell::m_spellAuraHolder).</summary>
    internal SpellAuraHolder? PendingHolder { get; set; }
}

/// <summary>An effect implementation (vmangos SpellEffects[] entry).</summary>
public delegate void SpellEffectHandler(SpellEffectContext context);

public sealed partial class SpellSystem
{
    private Dictionary<SpellEffectName, SpellEffectHandler> EffectHandlers { get; }

    /// <summary>
    /// Install or replace the handler of an effect (seam for other areas: e.g. the items area
    /// registers SPELL_EFFECT_CREATE_ITEM, quests SPELL_EFFECT_QUEST_COMPLETE).
    /// </summary>
    public void RegisterEffect(SpellEffectName effect, SpellEffectHandler handler)
        => EffectHandlers[effect] = handler ?? throw new ArgumentNullException(nameof(handler));

    public bool HasEffectHandler(SpellEffectName effect) => EffectHandlers.ContainsKey(effect);

    /// <summary>The handler currently installed for <paramref name="effect"/>, or null; lets a module that shares an effect chain to the earlier one instead of replacing it.</summary>
    public SpellEffectHandler? GetEffectHandler(SpellEffectName effect) => EffectHandlers.GetValueOrDefault(effect);

    /// <summary>
    /// Whether the handler of <paramref name="effect"/> is still the one this system installed itself
    /// (not replaced through <see cref="RegisterEffect"/>). Quest reward preflight models only the
    /// built-in teleport and summon handlers; replacement code cannot be modelled.
    /// </summary>
    public bool HasBuiltInEffectHandler(SpellEffectName effect)
        => _builtInEffectHandlers.TryGetValue(effect, out SpellEffectHandler? builtIn)
            && EffectHandlers.TryGetValue(effect, out SpellEffectHandler? active) && ReferenceEquals(builtIn, active);

    /// <summary>
    /// The effect set, after vmangos SpellEffects.cpp: SCHOOL_DAMAGE (EffectSchoolDMG),
    /// TELEPORT_UNITS (EffectTeleportUnits), APPLY_AURA (EffectApplyAura), HEAL (EffectHeal),
    /// ENERGIZE (EffectEnergize), LEARN_SPELL (EffectLearnSpell), TRIGGER_SPELL
    /// (EffectTriggerSpell), DUMMY (no generic behaviour; scripts hook it), and the second set:
    /// ENVIRONMENTAL_DAMAGE, HEALTH_LEECH, the weapon damage family (WEAPON_DAMAGE,
    /// WEAPON_DAMAGE_NOSCHOOL, NORMALIZED_WEAPON_DMG, WEAPON_PERCENT_DAMAGE), DISPEL,
    /// INTERRUPT_CAST, SUMMON (through <see cref="ISpellSummonSink"/>) and APPLY_AREA_AURA_PARTY, plus the
    /// combat abilities PARRY (EffectParry :5280), BLOCK (EffectBlock :5286) and DUAL_WIELD (EffectDualWield :2620),
    /// which set the player's ability flag (<see cref="Stats.PlayerStatState"/>) and do nothing for other targets.
    /// </summary>
    private Dictionary<SpellEffectName, SpellEffectHandler> CreateEffectHandlers() => new()
    {
        [SpellEffectName.SchoolDamage] = EffectSchoolDamage,
        [SpellEffectName.Dummy] = static _ => { },
        [SpellEffectName.TeleportUnits] = EffectTeleportUnits,
        [SpellEffectName.ApplyAura] = EffectApplyAura,
        [SpellEffectName.Heal] = EffectHeal,
        [SpellEffectName.Energize] = EffectEnergize,
        [SpellEffectName.LearnSpell] = EffectLearnSpell,
        [SpellEffectName.TriggerSpell] = EffectTriggerSpell,
        [SpellEffectName.EnvironmentalDamage] = EffectEnvironmentalDamage,
        [SpellEffectName.HealthLeech] = EffectHealthLeech,
        [SpellEffectName.WeaponDamageNoschool] = EffectWeaponDamage,
        [SpellEffectName.WeaponDamage] = EffectWeaponDamage,
        [SpellEffectName.NormalizedWeaponDmg] = EffectWeaponDamage,
        [SpellEffectName.WeaponPercentDamage] = EffectWeaponDamage,
        [SpellEffectName.Dispel] = EffectDispel,
        [SpellEffectName.InterruptCast] = EffectInterruptCast,
        [SpellEffectName.Summon] = EffectSummon,
        [SpellEffectName.ApplyAreaAuraParty] = EffectApplyAreaAuraParty,
        [SpellEffectName.Parry] = static context => (context.Target as Player)?.StatState.SetCanParry(true),
        [SpellEffectName.Block] = static context => (context.Target as Player)?.StatState.SetCanBlock(true),
        [SpellEffectName.DualWield] = static context => (context.Target as Player)?.StatState.SetCanDualWield(true),
    };

    /// <summary>vmangos Spell::DoAllEffectOnTarget → HandleEffects per effect, then the built aura holder is added.</summary>
    private SpellTargetOutcome? ApplyEffects(SpellCast cast, Unit target, int effectMask, float[]? multipliers = null)
    {
        if (IsQuestSettlementPending(cast.Caster) || IsQuestSettlementPending(target))
        {
            return null;
        }

        // Application rules (mechanic resistance, diminishing returns): they narrow the effect mask before any effect runs.
        SpellApplication? application = null;
        if (ApplicationRules.Count > 0)
        {
            application = new SpellApplication(this, cast, target, effectMask);
            foreach (ISpellApplicationRule rule in ApplicationRules)
            {
                rule.Begin(application);
            }

            effectMask = application.EffectMask;
            if (effectMask == 0)
            {
                return null;
            }
        }

        // Damage and healing dealt by the effect handlers is credited to this target's outcome; a nested
        // triggered cast builds its own and restores ours.
        var outcome = new OutcomeBuilder(cast, target, effectMask);
        OutcomeBuilder? outer = _outcome;
        _outcome = outcome;
        try
        {
            SpellAuraHolder? holder = null;
            bool dealsDamage = false; // rogue lane: vmangos m_damage != 0 proxy for the hostile-action interrupt
            for (int i = 0; i < SpellConstants.MaxEffects; i++)
            {
                if ((effectMask & (1 << i)) == 0)
                {
                    continue;
                }

                SpellEffectInfo effect = cast.Spell.Effects[i];
                if (!EffectHandlers.TryGetValue(effect.Effect, out SpellEffectHandler? handler))
                {
                    ReportUnsupported("effect", (uint)effect.Effect, cast.Spell.Id);
                    continue;
                }

                // vmangos CalculateSpellEffectValue (spell mods) comes before the chain damage multiplier. SPELLMOD_ALL_EFFECTS
                // runs after the registered value modifiers (combo points) like SpellCaster.cpp:1197-1199.
                int value = ModInt(cast.Caster, cast.Spell, SpellModOp.AllEffects,
                    ModifyValue(SpellValueKind.EffectValue, cast.Caster, cast.Spell, i, cast.Spell.CalculateEffectValue(i, CasterLevelOf(cast.Caster), Random), target));
                if (multipliers is not null && multipliers[i] != 1.0f)
                {
                    value = (int)(value * multipliers[i]);
                }

                dealsDamage |= IsDamageEffectWithValue(effect.Effect, value);
                var context = new SpellEffectContext(this, cast, target, i, value) { EffectMask = effectMask, PendingHolder = holder };
                handler(context);
                holder = context.PendingHolder;
            }

            // The application rules (diminishing returns, ...) may drop the holder; its non-aura effects already ran.
            if (holder is not null && !holder.IsEmpty
                && (application is null || ApplicationRules.All(rule => rule.AcceptHolder(application, holder))))
            {
                AddAuraHolder(holder);
            }

            FlushQueuedMeleeSpellDamage(outcome);

            InterruptTargetOfHostileSpell(cast, target, hit: true, dealsDamage); // rogue lane (vmangos Spell.cpp:1622-1650)
            SpellHitTarget?.Invoke(cast.Caster, target, cast.Spell.Id);
            SpellHit?.Invoke(cast.Caster, target, cast.Spell);
        }
        finally
        {
            _outcome = outer;
        }

        return outcome.Build();
    }

    /// <summary>
    /// Raised after a cast effect reaches a unit for quest objectives.
    /// </summary>
    public event Action<Unit, Unit, uint>? SpellHitTarget;

    /// <summary>
    /// vmangos Spell::EffectSchoolDMG + Unit::DealDamage path: armor (physical), crit and partial
    /// resist from <see cref="CombatRules"/>, then <see cref="IDamageSink"/>, pushback, and
    /// SMSG_SPELLNONMELEEDAMAGELOG to the caster's set.
    /// </summary>
    private void EffectSchoolDamage(SpellEffectContext context)
    {
        if (!context.Target.IsAlive || context.Value <= 0)
        {
            return;
        }

        DealDirectDamage(context.Caster, context.Target, context.Spell, ModifyDirect(SpellAmountStage.DirectDamage, context, (uint)context.Value), allowCrit: true);
    }

    /// <summary>vmangos Spell::EffectHeal → SpellCaster::DealHeal → SendHealSpellLog (to the set); a critical heal is +50% (SpellCriticalHealingBonus).</summary>
    private void EffectHeal(SpellEffectContext context)
    {
        if (!context.Target.IsAlive || context.Value <= 0)
        {
            return;
        }

        DeliverHeal(context, ModifyDirect(SpellAmountStage.DirectHeal, context, (uint)context.Value));
    }

    /// <summary>
    /// The shared tail of the heal effects (vmangos Spell::DoSpellHitOnUnit, m_healing): crit roll,
    /// <see cref="IDamageSink.Heal"/> and SMSG_SPELLHEALLOG. HEAL_MAX_HEALTH uses it too
    /// (<see cref="DirectCombatEffects"/>).
    /// </summary>
    internal void DeliverHeal(SpellEffectContext context, uint amount)
    {
        bool crit = !IsGameObjectStandIn(context.Caster) && CombatRules.RollCrit(this, context.Caster, context.Target, context.Spell);
        if (crit)
        {
            // Exact vmangos amount (+50% / creature-type multiplier) when the rules offer it; else the plain multiplier.
            amount = CombatRules is Rules.ISpellCritAmounts exact
                ? exact.CriticalHeal(this, context.Caster, context.Target, context.Spell, amount)
                : (uint)(amount * CombatRules.CritMultiplier(context.Spell));
        }

        uint healed = Damage.Heal(context.Caster, context.Target, context.Spell, amount, periodic: false);
        RecordHealing(context.Caster, context.Target, context.Spell, healed, crit);
        SendToSet(context.Caster, WorldOpcode.SmsgSpellheallog,
            SpellPackets.BuildSpellHealLog(context.Target.Guid, context.Caster.Guid, context.Spell.Id, healed, crit), includeSelf: true);
    }

    /// <summary>
    /// vmangos Spell::EffectEnergize → SpellCaster::EnergizeBySpell: MiscValue names the power;
    /// the gain is clamped to the maximum; SMSG_SPELLENERGIZELOG to the set.
    /// </summary>
    private void EffectEnergize(SpellEffectContext context)
    {
        int powerType = context.Effect.MiscValue;
        if (!context.Target.IsAlive || context.Value <= 0 || powerType is < 0 or > (int)PowerType.Happiness)
        {
            return;
        }

        var power = (PowerType)powerType;
        uint before = GetPower(context.Target, power);
        SetPower(context.Target, power, before + (uint)context.Value);
        SendToSet(context.Caster, WorldOpcode.SmsgSpellenergizelog, SpellPackets.BuildSpellEnergizeLog(
            context.Target.Guid, context.Caster.Guid, context.Spell.Id, (uint)powerType, (uint)context.Value), includeSelf: true);
    }

    /// <summary>
    /// vmangos Spell::EffectTeleportUnits: the destination comes from implicit target B —
    /// TARGET_LOCATION_DATABASE (spell_target_position) or TARGET_LOCATION_CASTER_HOME_BIND
    /// (the player's bind point); <see cref="ITeleportSink"/> moves the unit.
    /// </summary>
    private void EffectTeleportUnits(SpellEffectContext context)
    {
        if (!TryResolveTeleportDestination(context.Spell, context.EffectIndex, context.Target, context.Cast.Targets,
                out SpellTargetPosition d))
        {
            ReportUnsupported("teleport destination", (uint)context.Effect.TargetB, context.Spell.Id);
            return;
        }

        if (!Teleports.Teleport(context.Target, d.MapId, d.X, d.Y, d.Z, d.Orientation))
        {
            ReportUnsupported("teleport to map", d.MapId, context.Spell.Id);
        }
    }

    /// <summary>
    /// The destination <see cref="EffectTeleportUnits"/> would use for <paramref name="target"/>
    /// (implicit target B of effect <paramref name="effectIndex"/>); no side effects. Quest reward
    /// preflight shares it so that what is checked before the commit is what runs after it.
    /// </summary>
    public bool TryResolveTeleportDestination(SpellInfo spell, int effectIndex, Unit target, SpellCastTargets targets,
        out SpellTargetPosition destination)
    {
        ArgumentNullException.ThrowIfNull(spell);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(targets);
        SpellTargetPosition? resolved = spell.Effects[effectIndex].TargetB switch
        {
            SpellImplicitTarget.LocationDatabase => Store.GetTargetPosition(spell.Id),
            SpellImplicitTarget.LocationCasterHomeBind when target is Player player && !player.Home.IsUnset
                => new SpellTargetPosition(player.Home.MapId, player.Home.X, player.Home.Y, player.Home.Z, player.Orientation),
            SpellImplicitTarget.LocationCasterDest when targets.HasDest
                => new SpellTargetPosition(target.MapId, targets.Dest.X, targets.Dest.Y, targets.Dest.Z, target.Orientation),
            _ => null,
        };
        destination = resolved.GetValueOrDefault();
        return resolved.HasValue;
    }

    /// <summary>
    /// vmangos Spell::EffectLearnSpell: players learn EffectTriggerSpell (Player::LearnSpell →
    /// SMSG_LEARNED_SPELL); pets are the pet area's.
    /// </summary>
    private void EffectLearnSpell(SpellEffectContext context)
    {
        uint spellId = context.Effect.TriggerSpell;
        if (context.Target is not Player player || Store.Get(spellId) is null)
        {
            return;
        }

        LearnSpell(player, spellId);
    }

    /// <summary>vmangos Spell::EffectTriggerSpell: the caster casts EffectTriggerSpell, triggered, at the same target.</summary>
    private void EffectTriggerSpell(SpellEffectContext context)
    {
        uint spellId = context.Effect.TriggerSpell;
        if (Store.Get(spellId) is not null)
        {
            CastSpell(context.Caster, spellId, SpellCastTargets.ForUnit(context.Target.Guid), triggered: true, triggeringSpell: context.Spell);
        }
    }

    /// <summary>
    /// Teach a player a spell (vmangos Player::LearnSpell): stored through <see cref="ISpellbook"/>
    /// and announced with SMSG_LEARNED_SPELL. Returns false when it was already known.
    /// </summary>
    public bool LearnSpell(Player player, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(player);
        // Observers (SpellSystem.Unlearn.cs: talent rank replacement, profession points) see the learn before the book add
        // and after the packet, as vmangos Player::AddSpell does its talent and profession bookkeeping.
        if (IsQuestSettlementPending(player) || Spellbook is null || !NotifyBeforeLearn(player, spellId) || !Spellbook.LearnSpell(player, spellId))
        {
            return false;
        }

        AnnounceLearnedSpell(player, spellId);
        CastLearnedPassive(player, spellId);
        NotifyAfterLearn(player, spellId);
        return true;
    }

    /// <summary>
    /// SMSG_LEARNED_SPELL for a spell the player's book already contains (quest rewards commit the
    /// spell with the journal and announce it from the publication step, which must not mutate the book).
    /// </summary>
    public void AnnounceLearnedSpell(Player player, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(player);
        player.Session.Send(WorldOpcode.SmsgLearnedSpell, SpellPackets.BuildLearnedSpell(spellId));
    }

    /// <summary>
    /// vmangos Player::AddSpell casts learned passive spells on the player (CastSpell(this, spellId, true)).
    /// Refused, like every cast, while the player is held by a quest settlement.
    /// </summary>
    public void CastLearnedPassive(Player player, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (Store.Get(spellId) is { IsPassive: true } passive)
        {
            CastSpell(player, passive.Id, SpellCastTargets.ForSelf(), triggered: true);
        }
    }
}

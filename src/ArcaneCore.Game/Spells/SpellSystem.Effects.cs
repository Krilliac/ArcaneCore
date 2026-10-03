using ArcaneCore.Game.Entities;
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

    /// <summary>
    /// The first effect set, after vmangos SpellEffects.cpp: SCHOOL_DAMAGE (EffectSchoolDMG),
    /// TELEPORT_UNITS (EffectTeleportUnits), APPLY_AURA (EffectApplyAura), HEAL (EffectHeal),
    /// ENERGIZE (EffectEnergize), LEARN_SPELL (EffectLearnSpell), TRIGGER_SPELL
    /// (EffectTriggerSpell) and DUMMY (no generic behaviour; scripts hook it).
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
    };

    /// <summary>
    /// Per effect, the units it hits (vmangos Spell::FillTargetMap / SetTargetMap, subset):
    /// TARGET_UNIT_CASTER → caster; the explicit single-unit targets (enemy, friend, any, party)
    /// → the client's unit target, or the caster for a self-cast; no implicit target → the
    /// explicit unit or the caster. Area and chain targets are not implemented yet.
    /// Returns unit → effect mask, in first-hit order.
    /// </summary>
    private Dictionary<Unit, int> SelectTargets(SpellCast cast, Unit? unitTarget)
    {
        var result = new Dictionary<Unit, int>();
        IReadOnlyList<SpellEffectInfo> effects = cast.Spell.Effects;
        for (int i = 0; i < effects.Count; i++)
        {
            SpellEffectInfo effect = effects[i];
            if (effect.IsEmpty)
            {
                continue;
            }

            Unit? target = effect.TargetA switch
            {
                SpellImplicitTarget.UnitCaster => cast.Caster,
                SpellImplicitTarget.UnitEnemy or SpellImplicitTarget.UnitFriend or SpellImplicitTarget.Unit or SpellImplicitTarget.UnitParty
                    => unitTarget ?? (cast.Targets.Mask == SpellCastTargetFlags.Self ? cast.Caster : null),
                SpellImplicitTarget.None => unitTarget ?? cast.Caster,
                _ => null,
            };

            if (target is null)
            {
                ReportUnsupported("implicit target", (uint)effect.TargetA, cast.Spell.Id);
                continue;
            }

            result[target] = result.GetValueOrDefault(target) | (1 << i);
        }

        return result;
    }

    /// <summary>vmangos Spell::DoAllEffectOnTarget → HandleEffects per effect, then the built aura holder is added.</summary>
    private void ApplyEffects(SpellCast cast, Unit target, int effectMask)
    {
        SpellAuraHolder? holder = null;
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

            int value = cast.Spell.CalculateEffectValue(i, cast.Caster.Level, Random);
            var context = new SpellEffectContext(this, cast, target, i, value) { PendingHolder = holder };
            handler(context);
            holder = context.PendingHolder;
        }

        if (holder is not null && !holder.IsEmpty)
        {
            AddAuraHolder(holder);
        }
    }

    /// <summary>
    /// vmangos Spell::EffectSchoolDMG + Unit::DealDamage path (damage handed to <see cref="IDamageSink"/>):
    /// SMSG_SPELLNONMELEEDAMAGELOG to the caster's set.
    /// </summary>
    private void EffectSchoolDamage(SpellEffectContext context)
    {
        if (!context.Target.IsAlive || context.Value <= 0)
        {
            return;
        }

        uint dealt = Damage.DealSpellDamage(context.Caster, context.Target, context.Spell, (uint)context.Value, periodic: false);
        SendToSet(context.Caster, WorldOpcode.SmsgSpellnonmeleedamagelog, SpellPackets.BuildSpellNonMeleeDamageLog(
            context.Target.Guid, context.Caster.Guid, context.Spell.Id, dealt, context.Spell.School), includeSelf: true);
    }

    /// <summary>vmangos Spell::EffectHeal → SpellCaster::DealHeal → SendHealSpellLog (to the set).</summary>
    private void EffectHeal(SpellEffectContext context)
    {
        if (!context.Target.IsAlive || context.Value <= 0)
        {
            return;
        }

        uint healed = Damage.Heal(context.Caster, context.Target, context.Spell, (uint)context.Value);
        SendToSet(context.Caster, WorldOpcode.SmsgSpellheallog,
            SpellPackets.BuildSpellHealLog(context.Target.Guid, context.Caster.Guid, context.Spell.Id, healed), includeSelf: true);
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
        SpellTargetPosition? destination = context.Effect.TargetB switch
        {
            SpellImplicitTarget.LocationDatabase => Store.GetTargetPosition(context.Spell.Id),
            SpellImplicitTarget.LocationCasterHomeBind when context.Target is Player player && !player.Home.IsUnset
                => new SpellTargetPosition(player.Home.MapId, player.Home.X, player.Home.Y, player.Home.Z, player.Orientation),
            SpellImplicitTarget.LocationCasterDest when context.Cast.Targets.HasDest
                => new SpellTargetPosition(context.Target.MapId, context.Cast.Targets.Dest.X, context.Cast.Targets.Dest.Y, context.Cast.Targets.Dest.Z, context.Target.Orientation),
            _ => null,
        };

        if (destination is not { } d)
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
            CastSpell(context.Caster, spellId, SpellCastTargets.ForUnit(context.Target.Guid), triggered: true);
        }
    }

    /// <summary>
    /// Teach a player a spell (vmangos Player::LearnSpell): stored through <see cref="ISpellbook"/>
    /// and announced with SMSG_LEARNED_SPELL. Returns false when it was already known.
    /// </summary>
    public bool LearnSpell(Player player, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (Spellbook is null || !Spellbook.LearnSpell(player, spellId))
        {
            return false;
        }

        player.Session.Send(WorldOpcode.SmsgLearnedSpell, SpellPackets.BuildLearnedSpell(spellId));
        if (Store.Get(spellId) is { IsPassive: true } passive)
        {
            // vmangos Player::AddSpell casts learned passive spells on the player (CastSpell(this, spellId, true)).
            CastSpell(player, passive.Id, SpellCastTargets.ForSelf(), triggered: true);
        }

        return true;
    }
}

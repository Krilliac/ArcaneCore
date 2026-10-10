using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Druid;
using ArcaneCore.Kernel.WorldData;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// Shapeshift forms: the SPELL_AURA_MOD_SHAPESHIFT handler and the cast gate. Follows vmangos Aura::HandleAuraModShapeshift
/// (SpellAuras.cpp:2420-2625), HandleShapeshiftBoosts (:5433-5597) and Player::InitDataForForm (Player.cpp:18271-18312) at
/// the 1.12.1 build for the warrior stances and the druid forms (Cat, Tree of Life, Travel, Aquatic, Bear, Dire Bear and
/// Moonkin): the Shapeshift Form Effect spell, the display and its scale, the power type switch (Cat energy, Bear rage, a
/// druid leaving a form back to mana), Furor, the form byte, the linked boost spells, the known passives that need the
/// form and Leader of the Pack; and for the forms that only share the form byte, Ghost Wolf (display and scale),
/// Shadowform and Stealth (the byte alone: the stance gate of the 21 stealth spells and the Holy spells Shadowform blocks); and the Spirit of Redemption
/// form 32 (display 16031, the linked spells 27792 and 27795, SpellAuras.cpp:2405-2407 and 5480-5483; docs/areas/unit-control.md).
/// A form without a handler (the ones no spell uses) leaves its aura unhandled and is reported once.
/// </summary>
/// <remarks>
/// Deliberate differences, all documented in docs/areas/druid-forms.md: the previous form is removed before the new one is
/// applied (in vmangos the aura stacking rules remove it when the new spell is added, before the handler runs, so the
/// display and power switch of the new form are not undone by the old form's removal); the weapon-dependent crit refresh of
/// HandleShapeshiftBoosts (Elemental Sharpening Stone) is not implemented.
/// </remarks>
public sealed class ShapeshiftService
{
    /// <summary>
    /// vmangos AURA_INTERRUPT_SHAPESHIFTING_CANCELS (SpellDefines.h:592): auras with this interrupt flag end when a
    /// non-stance form is applied.
    /// </summary>
    public const uint ShapeshiftingCancelsFlag = 0x00008000;

    /// <summary>The Shapeshift Form Effect spell, cast on the target by the druid forms (SpellAuras.cpp:2433-2449).</summary>
    public const uint ShapeshiftFormEffectSpell = ShapeshiftFormEffectRules.SpellId;

    /// <summary>Furor's proc spells: 17099 in Cat Form (energy), 17057 in Bear and Dire Bear Form (rage) (SpellAuras.cpp:2512-2548).</summary>
    public const uint FurorEnergySpell = 17099;

    public const uint FurorRageSpell = 17057;

    /// <summary>SpellIconID of the Furor talent's dummy aura (SpellAuras.cpp:2520).</summary>
    public const uint FurorIconId = 238;

    /// <summary>Tactical Mastery's class-script ids and the rage they keep over a stance change, raw (SpellAuras.cpp:2541-2557).</summary>
    private static readonly (int Script, uint Rage)[] TacticalMastery = [(831, 50), (832, 100), (833, 150), (834, 200), (835, 250)];

    /// <summary>The forms this service handles (the others are reported and left alone).</summary>
    private static readonly HashSet<byte> HandledForms =
    [
        (byte)ShapeshiftForm.BattleStance, (byte)ShapeshiftForm.DefensiveStance, (byte)ShapeshiftForm.BerserkerStance,
        DruidForms.Cat, DruidForms.Tree, DruidForms.Travel, DruidForms.Aquatic, DruidForms.Bear, DruidForms.DireBear, DruidForms.Moonkin,
        DruidForms.GhostWolf, DruidForms.Shadow, DruidForms.Stealth,
        (byte)ShapeshiftForm.SpiritOfRedemption,
    ];

    /// <summary>The linked spells of the Spirit of Redemption form, in vmangos order ("must be second", SpellAuras.cpp:5480-5483).</summary>
    public const uint SpiritOfRedemptionBoost1 = 27792;

    public const uint SpiritOfRedemptionBoost2 = 27795;

    private readonly SpellSystem _spells;
    private readonly ShapeshiftFormCatalog _forms;
    private readonly CombatOptions _options;
    private readonly Func<Player, IEnumerable<uint>> _knownSpells;
    private readonly ILogger _logger;
    private readonly HashSet<uint> _reportedForms = [];
    private readonly List<IFormChangeListener> _listeners = [];
    private bool _switching;

    public ShapeshiftService(
        SpellSystem spells,
        ShapeshiftFormCatalog forms,
        CombatOptions options,
        Func<Player, IEnumerable<uint>> knownSpells,
        ILogger? logger = null)
    {
        _spells = spells ?? throw new ArgumentNullException(nameof(spells));
        _forms = forms ?? throw new ArgumentNullException(nameof(forms));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _knownSpells = knownSpells ?? throw new ArgumentNullException(nameof(knownSpells));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Install the aura handler and the stance cast check on the spell system.</summary>
    public void Install()
    {
        _spells.RegisterAura(AuraType.ModShapeshift, new AuraHandler(OnShapeshiftAura, null));
        _spells.RegisterCastCheck(new StanceCastCheck(this));
    }

    /// <summary>Be told after every form change (see <see cref="IFormChangeListener"/>).</summary>
    public void AddListener(IFormChangeListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        _listeners.Add(listener);
    }

    /// <summary>The form of a unit: UNIT_FIELD_BYTES_1 byte 2 (vmangos Unit::GetShapeshiftForm, UnitDefines.h:87).</summary>
    public static ShapeshiftForm GetForm(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return (ShapeshiftForm)unit.GetByte(UpdateFields.UnitFieldBytes1, 2);
    }

    private static void SetForm(Unit unit, ShapeshiftForm form) => unit.SetByte(UpdateFields.UnitFieldBytes1, 2, (byte)form);

    /// <summary>The flags1 of a form, or null when the catalog has no row (vmangos then treats the form as castable).</summary>
    public uint? GetFormFlags(ShapeshiftForm form) => _forms.TryGet((uint)form, out ShapeshiftFormInfo info) ? info.Flags1 : null;

    /// <summary>The stance gate of <see cref="SpellInfo.GetErrorAtShapeshiftedCast"/> for the caster's current form.</summary>
    public SpellCastResult CheckCast(Unit caster, SpellInfo spell)
    {
        ShapeshiftForm form = GetForm(caster);
        return spell.GetErrorAtShapeshiftedCast((uint)form, form == ShapeshiftForm.None ? null : GetFormFlags(form));
    }

    /// <summary>The boost passives cast on a stance: Battle 21156, Defensive 7376, Berserker 7381 (SpellAuras.cpp:5468-5476).</summary>
    public static uint GetBoostSpell(ShapeshiftForm form) => form switch
    {
        ShapeshiftForm.BattleStance => 21156,
        ShapeshiftForm.DefensiveStance => 7376,
        ShapeshiftForm.BerserkerStance => 7381,
        _ => 0,
    };

    /// <summary>Whether this service handles the form (the others leave their aura unhandled and are reported once).</summary>
    public static bool HandlesForm(ShapeshiftForm form) => HandledForms.Contains((byte)form);

    public static bool IsWarriorStance(ShapeshiftForm form)
        => form is ShapeshiftForm.BattleStance or ShapeshiftForm.DefensiveStance or ShapeshiftForm.BerserkerStance;

    /// <summary>Every linked boost spell of a form, in application order (SpellAuras.cpp:5433-5480).</summary>
    private static (uint First, uint Second) GetBoosts(ShapeshiftForm form)
    {
        uint stance = GetBoostSpell(form);
        if (stance != 0)
        {
            return (stance, 0);
        }

        if (form == ShapeshiftForm.SpiritOfRedemption)
        {
            return (SpiritOfRedemptionBoost1, SpiritOfRedemptionBoost2);
        }

        FormBoosts boosts = FormBoostTable.Get((byte)form);
        return (boosts.Spell1, boosts.Spell2);
    }

    /// <summary>
    /// The forms that cast the Shapeshift Form Effect (9033) when applied: Cat, Tree, Travel, Aquatic, Bear, Dire Bear and
    /// Moonkin (SpellAuras.cpp:2436-2445). Not Ghost Wolf, Stealth, Shadowform or the warrior stances.
    /// </summary>
    public static bool CastsShapeshiftFormEffect(ShapeshiftForm form)
        => form is ShapeshiftForm.Cat or ShapeshiftForm.Tree or ShapeshiftForm.Travel or ShapeshiftForm.Aqua
            or ShapeshiftForm.Bear or ShapeshiftForm.DireBear or ShapeshiftForm.Moonkin;

    private void OnShapeshiftAura(SpellSystem spells, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        var form = (ShapeshiftForm)aura.MiscValue;
        if (!HandledForms.Contains((byte)form))
        {
            if (_reportedForms.Add((uint)form))
            {
                _logger.LogInformation("Spell {Spell}: shapeshift form {Form} is not implemented yet", holder.Spell.Id, (uint)form);
            }

            return;
        }

        if (!_forms.TryGet((uint)form, out ShapeshiftFormInfo info))
        {
            // vmangos HandleAuraModShapeshift: unknown form -> error log, nothing else.
            _logger.LogError("Unknown shapeshift form {Form} in spell {Spell}", (uint)form, holder.Spell.Id);
            return;
        }

        if (apply)
        {
            ApplyForm(holder, form, info);
        }
        else
        {
            RemoveForm(holder, form);
        }
    }

    private void ApplyForm(SpellAuraHolder holder, ShapeshiftForm form, ShapeshiftFormInfo info)
    {
        Unit target = holder.Target;

        // Remove other shapeshift before applying a new one (RemoveSpellsCausingAura(MOD_SHAPESHIFT, holder)).
        bool outerSwitching = _switching;
        _switching = true;
        try
        {
            foreach (SpellAuraHolder other in _spells.GetAuras(target).Where(h => !ReferenceEquals(h, holder) && h.Spell.Id != holder.Spell.Id && !h.IsRemoved && h.HasAura(AuraType.ModShapeshift)).ToArray())
            {
                _spells.RemoveAuras(target, other.Spell.Id);
            }
        }
        finally
        {
            _switching = outerSwitching;
        }

        // Cast Shapeshift Form Effect to remove slows and roots (SpellAuras.cpp:2433-2449).
        if (CastsShapeshiftFormEffect(form) && _spells.Store.Get(ShapeshiftFormEffectSpell) is not null)
        {
            _spells.CastSpell(target, ShapeshiftFormEffectSpell, SpellCastTargets.ForSelf(), triggered: true);
        }

        ApplyDisplay(target, form);

        if ((info.Flags1 & (uint)ShapeshiftFlags.Stance) == 0)
        {
            foreach (SpellAuraHolder other in _spells.GetAuras(target).Where(h => h.Spell.Id != holder.Spell.Id && ((uint)h.Spell.AuraInterruptFlags & ShapeshiftingCancelsFlag) != 0).ToArray())
            {
                _spells.RemoveAuras(target, other.Spell.Id);
            }
        }

        if (_spells.IsRestoringAuras)
        {
            KeepPowersAtRestore(target, form);
        }
        else
        {
            SwitchPower(target, form);
        }

        byte oldForm = (byte)GetForm(target);
        SetForm(target, form);

        // HandleShapeshiftBoosts(true): the form's passives, then every known passive bound to this form.
        (uint boost1, uint boost2) = GetBoosts(form);
        foreach (uint boost in new[] { boost1, boost2 })
        {
            if (boost != 0 && _spells.Store.Get(boost) is not null)
            {
                _spells.CastSpell(target, boost, SpellCastTargets.ForSelf(), triggered: true);
            }
        }

        if (target is Player player)
        {
            uint[] known = _knownSpells(player).ToArray();
            foreach (uint spellId in known)
            {
                if (spellId != boost1 && spellId != boost2 && _spells.Store.Get(spellId) is { } spell && spell.IsNeedCastSpellAtFormApply((uint)form))
                {
                    _spells.CastSpell(target, spellId, SpellCastTargets.ForSelf(), triggered: true);
                }
            }

            // Leader of the Pack (SpellAuras.cpp:5497-5503).
            if (known.Contains(FormBoostTable.LeaderOfThePackKnownSpell) && _spells.Store.Get(FormBoostTable.LeaderOfThePackEffectSpell) is { } leader
                && FormBoostTable.LeaderOfThePackApplies(true, leader.Stances, (byte)form))
            {
                _spells.CastSpell(target, FormBoostTable.LeaderOfThePackEffectSpell, SpellCastTargets.ForSelf(), triggered: true);
            }

            CastHeartOfTheWild(target, form);
        }

        InitDataForForm(target, oldForm, (byte)form);
    }

    /// <summary>
    /// Heart of the Wild (SpellAuras.cpp:5505-5530): the talent's aura (ModTotalStatPercentage, spell icon 240, misc value 3)
    /// carries the percent; Cat and the bears cast their effect spell (24900 / 24899) with that amount as base points.
    /// Those spells are bound to the form by their Stances, so the form's end removes them.
    /// </summary>
    private void CastHeartOfTheWild(Unit target, ShapeshiftForm form)
    {
        uint effectSpell = FormBoostTable.Get((byte)form).HeartOfTheWildSpell;
        if (effectSpell == 0 || _spells.Store.Get(effectSpell) is null)
        {
            return;
        }

        foreach (SpellAuraHolder holder in _spells.GetAuras(target))
        {
            if (holder.IsRemoved || holder.Spell.SpellIconId != FormBoostTable.HeartOfTheWildIconId)
            {
                continue;
            }

            SpellAura? talent = holder.Auras.FirstOrDefault(a => a is { Type: AuraType.ModTotalStatPercentage } && a.MiscValue == FormBoostTable.HeartOfTheWildMiscValue);
            if (talent is not null)
            {
                _spells.CastCustomSpell(target, effectSpell, SpellCastTargets.ForSelf(), talent.Amount);
                return;
            }
        }
    }

    private void RemoveForm(SpellAuraHolder holder, ShapeshiftForm form)
    {
        Unit target = holder.Target;
        bool wasWarriorStance = IsWarriorStance(form);

        RemoveDisplay(target, form);

        if (target is Player { Class: Class.Druid })
        {
            PowerTypeSwitch.SetPowerType(target, PowerType.Mana);
            SpellSystem.SetPower(target, PowerType.Rage, 0);
        }

        byte oldForm = (byte)GetForm(target);
        SetForm(target, ShapeshiftForm.None);

        // HandleShapeshiftBoosts(false): drop the form's passives, then everything that needs a form.
        (uint boost1, uint boost2) = GetBoosts(form);
        foreach (uint boost in new[] { boost1, boost2 })
        {
            if (boost != 0)
            {
                _spells.RemoveAuras(target, boost);
            }
        }

        // Combat:StanceShiftKeepsSelfBuffs is about warrior stances (SpellAuras.cpp:5537-5539); a druid form always takes its own buffs along.
        if (!(wasWarriorStance && _switching && _options.StanceShiftKeepsSelfBuffs))
        {
            foreach (SpellAuraHolder buff in _spells.GetAuras(target).Where(h => !h.IsRemoved && IsRemovedOnShapeLost(h)).ToArray())
            {
                _spells.RemoveAuras(target, buff.Spell.Id);
            }

            // Interrupt current shape-specific spells (preparing, queued next swing, channel).
            if (_spells.GetState(target.Guid) is { } state)
            {
                foreach (SpellCast? cast in new[] { state.CurrentCast, state.MeleeCast })
                {
                    if (cast is { State: not SpellCastState.Finished } && cast.Spell.IsRemovedOnShapeLost)
                    {
                        _spells.Interrupt(cast);
                    }
                }
            }
        }

        InitDataForForm(target, oldForm, (byte)ShapeshiftForm.None);
    }

    /// <summary>
    /// Display and model scale of the form (SpellAuras.cpp:2463-2475): skipped for a unit under a Transform aura and for a form
    /// with no display (stances, Stealth, Shadowform).
    /// </summary>
    private void ApplyDisplay(Unit target, ShapeshiftForm form)
    {
        if (FormDisplayTable.Get((byte)form, target is not Player player || player.Team == Team.Alliance) is { } display && !HasTransform(target))
        {
            TransformScale.Set(target, display.Scale, _spells);
            target.DisplayId = display.DisplayId;
        }
    }

    private void RemoveDisplay(Unit target, ShapeshiftForm form)
    {
        if (FormDisplayTable.Get((byte)form, true) is not null && !HasTransform(target))
        {
            TransformScale.Reset(target, _spells);
            target.DisplayId = target.NativeDisplayId;
        }
    }

    /// <summary>vmangos Unit::GetTransForm: a Transform aura (type 56) is active.</summary>
    private bool HasTransform(Unit unit) => _spells.GetAuras(unit).Any(h => !h.IsRemoved && h.HasAura(AuraType.Transform));

    /// <summary>
    /// The power switch of HandleAuraModShapeshift (SpellAuras.cpp:2480-2575): a form with its own power type resets the
    /// power at the type change; Cat starts at 0 energy and Bear keeps the rage it had; both roll Furor (in the 1.12.1 build
    /// Cat shares the bear block, SpellAuras.cpp:2495-2498); a warrior stance keeps at most Tactical Mastery's rage.
    /// </summary>
    private void SwitchPower(Unit target, ShapeshiftForm form)
    {
        PowerType power = form switch
        {
            ShapeshiftForm.Cat => PowerType.Energy,
            ShapeshiftForm.Bear or ShapeshiftForm.DireBear => PowerType.Rage,
            ShapeshiftForm.BattleStance or ShapeshiftForm.DefensiveStance or ShapeshiftForm.BerserkerStance => PowerType.Rage,
            _ => PowerType.Mana,
        };
        if (power == PowerType.Mana)
        {
            return;
        }

        // reset power to default values only at power change
        uint before = SpellSystem.GetPower(target, power);
        if (target.PowerType != power)
        {
            PowerTypeSwitch.SetPowerType(target, power);
        }

        switch (form)
        {
            case ShapeshiftForm.Cat:
                SpellSystem.SetPower(target, PowerType.Energy, 0);
                RollFuror(target, FurorEnergySpell);
                break;
            case ShapeshiftForm.Bear:
            case ShapeshiftForm.DireBear:
                SpellSystem.SetPower(target, PowerType.Rage, before);
                RollFuror(target, FurorRageSpell);
                break;
            default:
                // Stances: the rage a stance change keeps is Tactical Mastery's (SpellAuras.cpp:2528-2569).
                uint keep = target is Player ? GetTacticalMasteryRage(target) : 0;
                if (SpellSystem.GetPower(target, PowerType.Rage) > keep)
                {
                    SpellSystem.SetPower(target, PowerType.Rage, keep);
                }

                break;
        }
    }

    /// <summary>
    /// A form restored at login: the unit takes the form's power type with its maximum, but keeps the powers it was loaded
    /// with and rolls no Furor (vmangos restores the saved powers after the auras, Player.cpp:15057-15070, which overwrites
    /// what the power switch and a Furor proc did).
    /// </summary>
    private static void KeepPowersAtRestore(Unit target, ShapeshiftForm form)
    {
        PowerType power = form switch
        {
            ShapeshiftForm.Cat => PowerType.Energy,
            ShapeshiftForm.Bear or ShapeshiftForm.DireBear => PowerType.Rage,
            ShapeshiftForm.BattleStance or ShapeshiftForm.DefensiveStance or ShapeshiftForm.BerserkerStance => PowerType.Rage,
            _ => PowerType.Mana,
        };
        if (power == PowerType.Mana)
        {
            return;
        }

        PowerTypeSwitch.EnsureFeralPowerCaps(target);
        target.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)power);
    }

    /// <summary>Furor (SpellAuras.cpp:2512-2548): the dummy aura of icon 238 holds the chance in percent; irand(1, 100) at or below it casts the proc spell.</summary>
    private void RollFuror(Unit target, uint procSpell)
    {
        int chance = 0;
        foreach (SpellAuraHolder holder in _spells.GetAuras(target))
        {
            if (holder.IsRemoved || holder.Spell.SpellIconId != FurorIconId)
            {
                continue;
            }

            SpellAura? dummy = holder.Auras.FirstOrDefault(a => a is { Type: AuraType.Dummy });
            if (dummy is not null)
            {
                chance = dummy.Amount;
                break;
            }
        }

        if (_spells.Random.Next(1, 101) <= chance && _spells.Store.Get(procSpell) is not null)
        {
            _spells.CastSpell(target, procSpell, SpellCastTargets.ForSelf(), triggered: true);
        }
    }

    /// <summary>
    /// The power type part of Player::InitDataForForm (Player.cpp:18271-18312): Cat energy, Bear and Dire Bear rage, any other
    /// form the class power type. The attack times and attack power of that function belong to the stat area, which hears about
    /// the change through <see cref="IFormChangeListener"/>.
    /// </summary>
    private void InitDataForForm(Unit target, byte oldForm, byte newForm)
    {
        if (target is Player player)
        {
            PowerType wanted = newForm switch
            {
                DruidForms.Cat => PowerType.Energy,
                DruidForms.Bear or DruidForms.DireBear => PowerType.Rage,
                _ => FormPowerRules.ClassPowerType(player.Class),
            };
            if (target.PowerType != wanted)
            {
                PowerTypeSwitch.SetPowerType(target, wanted);
            }
        }

        foreach (IFormChangeListener listener in _listeners)
        {
            listener.OnFormChanged(target, oldForm, newForm);
        }
    }

    /// <summary>vmangos SpellAuraHolder::m_isRemovedOnShapeLost (SpellAuras.cpp:6672): cast on itself and bound to a form.</summary>
    private static bool IsRemovedOnShapeLost(SpellAuraHolder holder)
        => holder.CasterGuid == holder.Target.Guid && holder.Spell.IsRemovedOnShapeLost;

    /// <summary>The raw rage a stance change keeps: the Tactical Mastery class-script aura (831-835) or 0.</summary>
    private uint GetTacticalMasteryRage(Unit unit)
    {
        foreach (SpellAuraHolder holder in _spells.GetAuras(unit))
        {
            foreach (SpellAura? aura in holder.AuraSpan)
            {
                if (aura is { Type: AuraType.OverrideClassScripts })
                {
                    foreach ((int script, uint rage) in TacticalMastery)
                    {
                        if (aura.MiscValue == script)
                        {
                            return rage;
                        }
                    }
                }
            }
        }

        return 0;
    }
}

/// <summary>
/// The shapeshift gate of <c>Spell::CheckCast</c> (vmangos Spell.cpp:5349-5351): only for the strict check of a
/// non-triggered cast, before the caster aura state.
/// </summary>
public sealed class StanceCastCheck(ShapeshiftService service) : ISpellCastCheck
{
    public SpellCheckPhase Phase => SpellCheckPhase.Caster;

    public int Order => SpellCastCheckOrder.Shapeshift;

    public SpellCastResult Check(in SpellCastCheckContext context)
        => context.Triggered || !context.Strict ? SpellCastResult.CastOk : service.CheckCast(context.Caster, context.Spell);
}

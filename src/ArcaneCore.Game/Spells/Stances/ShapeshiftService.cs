using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// Warrior stances (Battle, Defensive, Berserker): the SPELL_AURA_MOD_SHAPESHIFT handler and the cast gate.
/// Follows vmangos Aura::HandleAuraModShapeshift (SpellAuras.cpp:2420-2575) and HandleShapeshiftBoosts
/// (:5433-5597) at the 1.12.1 build. Druid and priest forms (models, speed, energy/rage swap, Furor, Heart of
/// the Wild, Leader of the Pack) are not implemented; their aura is left unhandled and reported once.
/// </summary>
public sealed class ShapeshiftService
{
    /// <summary>
    /// vmangos AURA_INTERRUPT_SHAPESHIFTING_CANCELS (SpellDefines.h:592): auras with this interrupt flag end when a
    /// non-stance form is applied.
    /// </summary>
    public const uint ShapeshiftingCancelsFlag = 0x00008000;

    /// <summary>Tactical Mastery's class-script ids and the rage they keep over a stance change, raw (SpellAuras.cpp:2541-2557).</summary>
    private static readonly (int Script, uint Rage)[] TacticalMastery = [(831, 50), (832, 100), (833, 150), (834, 200), (835, 250)];

    private readonly SpellSystem _spells;
    private readonly ShapeshiftFormCatalog _forms;
    private readonly CombatOptions _options;
    private readonly Func<Player, IEnumerable<uint>> _knownSpells;
    private readonly ILogger _logger;
    private readonly HashSet<uint> _reportedForms = [];
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

    public static bool IsWarriorStance(ShapeshiftForm form)
        => form is ShapeshiftForm.BattleStance or ShapeshiftForm.DefensiveStance or ShapeshiftForm.BerserkerStance;

    private void OnShapeshiftAura(SpellSystem spells, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        var form = (ShapeshiftForm)aura.MiscValue;
        if (!IsWarriorStance(form))
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
            ApplyStance(holder, form, info);
        }
        else
        {
            RemoveStance(holder, form);
        }
    }

    private void ApplyStance(SpellAuraHolder holder, ShapeshiftForm form, ShapeshiftFormInfo info)
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

        if ((info.Flags1 & (uint)ShapeshiftFlags.Stance) == 0)
        {
            foreach (SpellAuraHolder other in _spells.GetAuras(target).Where(h => h.Spell.Id != holder.Spell.Id && ((uint)h.Spell.AuraInterruptFlags & ShapeshiftingCancelsFlag) != 0).ToArray())
            {
                _spells.RemoveAuras(target, other.Spell.Id);
            }
        }

        // Stances use rage; the rage a stance change keeps is Tactical Mastery's (SpellAuras.cpp:2528-2569).
        if (target.PowerType != PowerType.Rage)
        {
            target.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Rage);
        }

        uint keep = target is Player ? GetTacticalMasteryRage(target) : 0;
        if (SpellSystem.GetPower(target, PowerType.Rage) > keep)
        {
            SpellSystem.SetPower(target, PowerType.Rage, keep);
        }

        SetForm(target, form);

        // HandleShapeshiftBoosts(true): the stance passive, then every known passive bound to this form.
        uint boost = GetBoostSpell(form);
        if (boost != 0 && _spells.Store.Get(boost) is not null)
        {
            _spells.CastSpell(target, boost, SpellCastTargets.ForSelf(), triggered: true);
        }

        if (target is Player player)
        {
            foreach (uint spellId in _knownSpells(player).ToArray())
            {
                if (spellId != boost && _spells.Store.Get(spellId) is { } spell && spell.IsNeedCastSpellAtFormApply((uint)form))
                {
                    _spells.CastSpell(target, spellId, SpellCastTargets.ForSelf(), triggered: true);
                }
            }
        }
    }

    private void RemoveStance(SpellAuraHolder holder, ShapeshiftForm form)
    {
        Unit target = holder.Target;
        SetForm(target, ShapeshiftForm.None);

        // HandleShapeshiftBoosts(false): drop the stance passive, then everything that needs a form.
        uint boost = GetBoostSpell(form);
        if (boost != 0)
        {
            _spells.RemoveAuras(target, boost);
        }

        if (_switching && _options.StanceShiftKeepsSelfBuffs)
        {
            return;
        }

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

    /// <summary>vmangos SpellAuraHolder::m_isRemovedOnShapeLost (SpellAuras.cpp:6672): cast on itself and bound to a form.</summary>
    private static bool IsRemovedOnShapeLost(SpellAuraHolder holder)
        => holder.CasterGuid == holder.Target.Guid && holder.Spell.IsRemovedOnShapeLost;

    /// <summary>The raw rage a stance change keeps: the Tactical Mastery class-script aura (831-835) or 0.</summary>
    private uint GetTacticalMasteryRage(Unit unit)
    {
        foreach (SpellAuraHolder holder in _spells.GetAuras(unit))
        {
            foreach (SpellAura? aura in holder.Auras)
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

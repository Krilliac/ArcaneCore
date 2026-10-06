using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Druid;
using ArcaneCore.Kernel.WorldData;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// Warrior stances (Battle, Defensive, Berserker): the SPELL_AURA_MOD_SHAPESHIFT handler and the cast gate.
/// Follows vmangos Aura::HandleAuraModShapeshift (SpellAuras.cpp:2420-2575) and HandleShapeshiftBoosts
/// (:5433-5597) at the 1.12.1 build. Catalog-backed druid display overlays are supported; druid
/// power/speed/boost mechanics, model geometry and priest form producers remain separate slices.
/// </summary>
public sealed class ShapeshiftService
{
    private static readonly FormDisplay GhostWolfDisplay = new(4613, 0.80f);
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
        _spells.ItemEquipFormCheck = CheckCast;
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
            if (form == ShapeshiftForm.Shadow && _forms.TryGet((uint)form, out _))
            {
                if (apply)
                {
                    _spells.RemoveOtherShapeshiftHolders(holder.Target, holder);
                    RemoveShapeshiftingCancels(holder);
                    SetForm(holder.Target, form);
                    holder.Target.FormHolder = holder;
                    if (holder.Target is Player changedPlayer)
                    {
                        changedPlayer.Inventory.EquipSpellSink?.OnPlayerFormChanged(changedPlayer);
                    }
                }
                else if (ReferenceEquals(holder.Target.FormHolder, holder))
                {
                    SetForm(holder.Target, ShapeshiftForm.None);
                    holder.Target.FormHolder = null;
                    if (holder.Target is Player changedPlayer)
                    {
                        changedPlayer.Inventory.EquipSpellSink?.OnPlayerFormChanged(changedPlayer);
                    }
                    RemoveShapeLostAurasAndInterrupt(holder.Target);
                }
            }
            else if (form == ShapeshiftForm.GhostWolf && _forms.TryGet((uint)form, out _))
            {
                if (apply)
                {
                    _spells.RemoveOtherShapeshiftHolders(holder.Target, holder);
                    RemoveShapeshiftingCancels(holder);
                    RemoveWaterWalk(holder.Target);
                    SetForm(holder.Target, form);
                    holder.Target.FormHolder = holder;
                    if (holder.Target is Player changedPlayer)
                    {
                        changedPlayer.Inventory.EquipSpellSink?.OnPlayerFormChanged(changedPlayer);
                    }
                    ApplyGhostWolfVisual(_spells, holder.Target);
                }
                else if (ReferenceEquals(holder.Target.FormHolder, holder))
                {
                    SetForm(holder.Target, ShapeshiftForm.None);
                    holder.Target.FormHolder = null;
                    if (holder.Target is Player changedPlayer)
                    {
                        changedPlayer.Inventory.EquipSpellSink?.OnPlayerFormChanged(changedPlayer);
                    }
                    RemoveShapeLostAurasAndInterrupt(holder.Target);
                    ClearFormVisual(_spells, holder.Target);
                }
            }
            else if (IsDruidForm(form) && _forms.TryGet((uint)form, out ShapeshiftFormInfo druidInfo))
            {
                if (apply)
                {
                    _spells.RemoveOtherShapeshiftHolders(holder.Target, holder);
                    if ((druidInfo.Flags1 & (uint)ShapeshiftFlags.Stance) == 0)
                    {
                        RemoveShapeshiftingCancels(holder);
                    }

                    ApplyDruidPower(holder.Target, form);

                    SetForm(holder.Target, form);
                    holder.Target.FormHolder = holder;
                    ApplyDruidFormBoosts(holder, form);
                    if (holder.Target is Player changedPlayer)
                    {
                        changedPlayer.Inventory.EquipSpellSink?.OnPlayerFormChanged(changedPlayer);
                    }
                }

                if (apply)
                {
                    ApplyFormVisual(_spells, holder.Target);
                }
                else if (ReferenceEquals(holder.Target.FormHolder, holder))
                {
                    RemoveDruidFormBoosts(holder, form);
                    RemoveDruidPower(holder.Target);
                    SetForm(holder.Target, ShapeshiftForm.None);
                    holder.Target.FormHolder = null;
                    if (holder.Target is Player changedPlayer)
                    {
                        changedPlayer.Inventory.EquipSpellSink?.OnPlayerFormChanged(changedPlayer);
                    }
                    RemoveShapeLostAurasAndInterrupt(holder.Target);
                    ClearFormVisual(_spells, holder.Target);
                }
            }
            else if (_reportedForms.Add((uint)form))
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

    private void ApplyDruidPower(Unit target, ShapeshiftForm form)
    {
        if (form is not (ShapeshiftForm.Cat or ShapeshiftForm.Bear or ShapeshiftForm.DireBear))
        {
            return;
        }

        if (form == ShapeshiftForm.Cat)
        {
            PowerTypeSwitch.SetPowerType(target, PowerType.Energy);
            MapCombat.SetPower(target, PowerType.Energy, 0);
        }
        else
        {
            uint rage = MapCombat.GetPower(target, PowerType.Rage);
            PowerTypeSwitch.SetPowerType(target, PowerType.Rage);
            MapCombat.SetPower(target, PowerType.Rage, rage);
        }

        int chance = FurorChance(target);
        if (!FurorRules.Procs(chance, _spells.Random.Next(1, 101)))
        {
            return;
        }

        uint proc = FurorRules.ProcSpell((byte)form);
        if (proc == 0)
        {
            return;
        }

        if (_spells.Store.Get(proc) is null)
        {
            _logger.LogWarning("Druid Furor proc spell {Spell} is missing for form {Form}; skipping proc", proc, form);
            return;
        }

        _spells.CastSpell(target, proc, SpellCastTargets.ForSelf(), triggered: true);
    }

    private int FurorChance(Unit target)
    {
        foreach (SpellAuraHolder aura in _spells.GetAuras(target))
        {
            if (aura.IsRemoved || aura.Spell.SpellIconId != FurorRules.DummyIconId)
            {
                continue;
            }

            SpellAura? dummy = aura.Auras.FirstOrDefault(a => a is { Type: AuraType.Dummy });
            if (dummy is not null)
            {
                return dummy.Amount;
            }
        }

        return 0;
    }

    private static void RemoveDruidPower(Unit target)
    {
        if (target is not { Class: Class.Druid })
        {
            return;
        }

        PowerTypeSwitch.SetPowerType(target, PowerType.Mana);
        MapCombat.SetPower(target, PowerType.Rage, 0);
    }

    private void ApplyDruidFormBoosts(SpellAuraHolder holder, ShapeshiftForm form)
    {
        Unit target = holder.Target;
        FormBoosts boosts = FormBoostTable.Get((byte)form);
        foreach (uint spellId in new[] { boosts.Spell1, boosts.Spell2 }.Where(id => id != 0).Distinct())
        {
            if (_spells.Store.Get(spellId) is null)
            {
                _logger.LogWarning("Druid form boost spell {Spell} is missing for form {Form}; skipping", spellId, form);
                continue;
            }

            _spells.CastSpell(target, spellId, SpellCastTargets.ForSelf(), triggered: true, triggeringSpell: holder.Spell);
        }

        if (target is Player player)
        {
            foreach (uint spellId in _knownSpells(player).ToArray())
            {
                if (spellId != boosts.Spell1 && spellId != boosts.Spell2
                    && _spells.Store.Get(spellId) is { } spell && spell.IsNeedCastSpellAtFormApply((uint)form))
                {
                    _spells.CastSpell(target, spellId, SpellCastTargets.ForSelf(), triggered: true, triggeringSpell: holder.Spell);
                }
            }

            bool knowsLeader = _knownSpells(player).Contains(FormBoostTable.LeaderOfThePackKnownSpell);
            if (knowsLeader && _spells.Store.Get(FormBoostTable.LeaderOfThePackEffectSpell) is not { })
            {
                _logger.LogWarning("Druid Leader of the Pack effect spell {Spell} is missing for form {Form}; skipping",
                    FormBoostTable.LeaderOfThePackEffectSpell, form);
            }
            else if (knowsLeader && _spells.Store.Get(FormBoostTable.LeaderOfThePackEffectSpell) is { } leader
                && FormBoostTable.LeaderOfThePackApplies(true, leader.Stances, (byte)form))
            {
                _spells.CastSpell(target, leader.Id, SpellCastTargets.ForSelf(), triggered: true, triggeringSpell: holder.Spell);
            }
        }

        if (boosts.HeartOfTheWildSpell == 0 || _spells.Store.Get(boosts.HeartOfTheWildSpell) is not { } heart)
        {
            if (boosts.HeartOfTheWildSpell != 0)
            {
                _logger.LogWarning("Druid Heart of the Wild spell {Spell} is missing for form {Form}; skipping", boosts.HeartOfTheWildSpell, form);
            }

            return;
        }

        SpellAura? hotw = _spells.GetAuras(target)
            .SelectMany(h => h.Auras.OfType<SpellAura>().Select(a => (Holder: h, Aura: a)))
            .FirstOrDefault(x => x.Holder.Spell.SpellIconId == FormBoostTable.HeartOfTheWildIconId
                && x.Aura.Type == AuraType.ModTotalStatPercentage && x.Aura.MiscValue == FormBoostTable.HeartOfTheWildMiscValue).Aura;
        if (hotw is null)
        {
            return;
        }

        int effectIndex = -1;
        for (int i = 0; i < heart.Effects.Count; i++)
        {
            if (heart.Effects[i].Effect == SpellEffectName.ApplyAura
                && heart.Effects[i].AuraType == AuraType.ModTotalStatPercentage)
            {
                effectIndex = i;
                break;
            }
        }
        if (effectIndex >= 0)
        {
            _spells.CastSpellWithCustomAuraAmount(target, heart.Id, SpellCastTargets.ForSelf(), triggered: true,
                triggeringSpell: holder.Spell, effectIndex: effectIndex, amount: hotw.Amount);
        }
        else
        {
            _logger.LogWarning("Druid Heart of the Wild spell {Spell} has no stat aura effect for form {Form}; skipping",
                heart.Id, form);
        }
    }

    private void RemoveDruidFormBoosts(SpellAuraHolder holder, ShapeshiftForm form)
    {
        FormBoosts boosts = FormBoostTable.Get((byte)form);
        foreach (uint spellId in new[] { boosts.Spell1, boosts.Spell2, boosts.HeartOfTheWildSpell }.Where(id => id != 0).Distinct())
        {
            foreach (SpellAuraHolder linked in _spells.GetAuras(holder.Target)
                .Where(h => h.Spell.Id == spellId && h.CasterGuid == holder.Target.Guid).ToArray())
            {
                _spells.RemoveAuraHolder(linked);
            }
        }
    }

    private void RemoveShapeLostAurasAndInterrupt(Unit target)
    {
        if (!(_switching && _options.StanceShiftKeepsSelfBuffs))
        {
            foreach (SpellAuraHolder buff in _spells.GetAuras(target).Where(h => !h.IsRemoved && IsRemovedOnShapeLost(h)).ToArray())
            {
                _spells.RemoveAuraHolder(buff);
            }
        }

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

    private static bool IsDruidForm(ShapeshiftForm form)
        => FormDisplayTable.Get((byte)form, alliance: true) is not null;

    private void RemoveWaterWalk(Unit target)
    {
        foreach (SpellAura waterWalk in _spells.AurasOfType(target, AuraType.WaterWalk).ToArray())
        {
            foreach (SpellAuraHolder holder in _spells.GetAuras(target)
                .Where(h => !h.IsRemoved && h.Auras.Any(a => ReferenceEquals(a, waterWalk))).ToArray())
            {
                _spells.RemoveAuraHolder(holder);
            }
        }
    }

    private void RemoveShapeshiftingCancels(SpellAuraHolder keep)
    {
        foreach (SpellAuraHolder holder in _spells.GetAuras(keep.Target)
            .Where(h => !ReferenceEquals(h, keep) && !h.IsRemoved
                && ((uint)h.Spell.AuraInterruptFlags & ShapeshiftingCancelsFlag) != 0).ToArray())
        {
            _spells.RemoveAuraHolder(holder);
        }
    }

    private static void ApplyGhostWolfVisual(SpellSystem spells, Unit target)
        => ApplyFormVisual(spells, target, GhostWolfDisplay);

    internal static void ApplyFormVisual(SpellSystem spells, Unit target)
    {
        ShapeshiftForm form = GetForm(target);
        FormDisplay? display = form == ShapeshiftForm.GhostWolf
            ? GhostWolfDisplay
            : FormDisplayTable.Get((byte)form, target is not Player player || player.Race is Race.Human or Race.Dwarf or Race.Gnome or Race.NightElf);
        if (display is not { } formDisplay)
        {
            return;
        }

        ApplyFormVisual(spells, target, formDisplay);
    }

    private static void ApplyFormVisual(SpellSystem spells, Unit target, FormDisplay display)
    {
        if (target.FormBaseScale == 0)
        {
            target.FormBaseScale = target.TransformSpellId != 0 ? target.TransformBaseScale
                : target.GetFloat(UpdateFields.ObjectFieldScaleX) / VisualAuras.ActiveScaleFactor(spells, target);
        }

        target.FormDisplayId = display.DisplayId;
        target.FormScale = display.Scale;
        if (target.TransformSpellId != 0)
        {
            return;
        }

        target.DisplayId = display.DisplayId;
        target.SetFloat(UpdateFields.ObjectFieldScaleX,
            display.Scale * VisualAuras.ActiveScaleFactor(spells, target));
        spells.UpdateDisplayModel(target);
    }

    private static void ClearFormVisual(SpellSystem spells, Unit target)
    {
        float baseScale = target.FormBaseScale;
        target.FormDisplayId = 0;
        target.FormScale = 1.0f;
        target.FormBaseScale = 0;
        if (target.TransformSpellId != 0)
        {
            return;
        }

        target.DisplayId = target.NativeDisplayId;
        target.SetFloat(UpdateFields.ObjectFieldScaleX,
            baseScale == 0 ? target.GetFloat(UpdateFields.ObjectFieldScaleX)
                : baseScale * VisualAuras.ActiveScaleFactor(spells, target));
        spells.UpdateDisplayModel(target);
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
        if (target is Player changedPlayer)
        {
            changedPlayer.Inventory.EquipSpellSink?.OnPlayerFormChanged(changedPlayer);
        }

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
        if (target is Player changedPlayer)
        {
            changedPlayer.Inventory.EquipSpellSink?.OnPlayerFormChanged(changedPlayer);
        }

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
            _spells.RemoveAuraHolder(buff);
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
